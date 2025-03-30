using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace TcpServer
{
    internal class WebSocketFileClient
    {
        private static string _serverUrl = string.Empty;
        private const string PartialSuffix = ".partial";
        private const int PartialFileTimeoutSeconds = 10 * 60;
        private static readonly string SyncFolder = Path.Combine(Directory.GetCurrentDirectory(), "SyncedFiles");

        private static ILogger<WebSocketFileClient> _logger = null!;

        // Persistent notification socket – used only for receiving notifications.
        private static ClientWebSocket? _notificationSocket;
        private static readonly string[] IgnoredPrefixes = ["~$", "."];

        private static readonly string[] IgnoredSuffixes =
        [
            ".swp", ".tmp", ".lock", ".part", ".partial", ".crdownload", ".download", ".bak", ".old", ".temp", ".sha256"
        ];

        private static readonly ConcurrentDictionary<string, CancellationTokenSource> DebounceTokens = new();
        private static readonly ConcurrentDictionary<string, long> RecentDownloads = new();
        private static readonly ConcurrentDictionary<string, long> RecentUploads = new();

        private const int UploadCooldownSeconds = 5;
        private static CancellationTokenSource? _notificationCts;

        private static async Task Main()
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appSettings.json", optional: false, reloadOnChange: true)
                .Build();

            _serverUrl = configuration["ServerUrl"] ?? throw new Exception("ServerUrl not configured.");
            _logger = SetupLogging();
            Console.WriteLine("[INFO] Welcome to the WebSocket File Transfer Client!");

            if (!Directory.Exists(SyncFolder))
                Directory.CreateDirectory(SyncFolder);
            StartStaleFileCleanup();
            PrintHelp();

            // Start the persistent notification receiver.
            _notificationCts = new CancellationTokenSource();
            var notificationTask = StartNotificationReceiverAsync(_notificationCts.Token);

            StartLocalFileWatcher();

            while (true)
            {
                Console.Write("Enter command: ");
                var userInput = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(userInput))
                {
                    Console.WriteLine("Invalid input. Please enter a command.");
                    continue;
                }

                if (userInput.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
                {
                    PrintHelp();
                }
                else if (userInput.StartsWith("/upload", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = userInput.Split(' ', 2);
                    if (parts.Length < 2)
                    {
                        Console.WriteLine("Usage: /upload <file_path>");
                        continue;
                    }

                    var filePath = parts[1].Trim().Trim('\'', '"');
                    var relativePath = Path.GetRelativePath(SyncFolder, filePath);
                    await UploadFileAsync(relativePath);
                }
                else if (userInput.StartsWith("/download", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = userInput.Split(' ', 2);
                    if (parts.Length < 2)
                    {
                        Console.WriteLine("Usage: /download <file_name>");
                        continue;
                    }

                    var fileName = parts[1].Trim();
                    var relativePath = Path.GetRelativePath(SyncFolder, fileName);
                    await DownloadFileAsync(relativePath);
                }
                else if (userInput.StartsWith("/delete", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = userInput.Split(' ', 2);
                    if (parts.Length < 2)
                    {
                        Console.WriteLine("Usage: /delete <file_name>");
                        continue;
                    }

                    var fileName = parts[1].Trim();
                    var relativePath = Path.GetRelativePath(SyncFolder, fileName);
                    await DeleteFileAsync(relativePath);
                }
                else if (userInput.StartsWith("/list", StringComparison.OrdinalIgnoreCase))
                {
                    await ListFilesAsync();
                }
                else if (userInput.StartsWith("/quit", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Shutting down notifications and exiting...");
                    await _notificationCts.CancelAsync();
                    await notificationTask;
                    break;
                }
                else
                {
                    Console.WriteLine("Unknown command. Type /help for a list of commands.");
                }
            }
        }

        private static ILogger<WebSocketFileClient> SetupLogging()
        {
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddSimpleConsole(options =>
                {
                    options.IncludeScopes = false;
                    options.SingleLine = true;
                    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                }).SetMinimumLevel(LogLevel.Information);
            });
            return loggerFactory.CreateLogger<WebSocketFileClient>();
        }

        private static void PrintHelp()
        {
            Console.WriteLine($@"
Available commands:
------------------------------------------------------
/help                  - Show this help message.
/upload <file_path>    - Upload a file to the server.
/delete <file_name>    - Delete a file from the server.
/download <file_name>  - Download a file from the server.
/list                  - List all files on the server.
/quit                  - Exit the application.
------------------------------------------------------");
        }

        /// <summary>
        /// Returns true if the fullPath refers to a directory.
        /// </summary>
        private static bool IsDirectory(string fullPath) => Directory.Exists(fullPath);

        /// <summary>
        /// Returns true if the relative path (for deletion events) looks like a directory.
        /// </summary>
        private static bool LooksLikeDirectory(string relativePath) => !Path.HasExtension(relativePath);

        /// <summary>
        /// Starts the persistent notification receiver.
        /// Immediately sends a subscription message so that the server treats this connection as persistent.
        /// </summary>
        private static async Task StartNotificationReceiverAsync(CancellationToken cancellationToken)
        {
            var retryDelay = 2000;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation($"[DEBUG] Connecting to notification server: {_serverUrl}");
                    _notificationSocket = new ClientWebSocket();
                    _notificationSocket.Options.RemoteCertificateValidationCallback =
                        (sender, certificate, chain, sslPolicyErrors) => true;
                    await _notificationSocket.ConnectAsync(new Uri(_serverUrl), cancellationToken);

                    // Immediately subscribe
                    var subscribeMsg = JsonConvert.SerializeObject(new { subscribe = true });
                    var subscribeBytes = Encoding.UTF8.GetBytes(subscribeMsg);
                    await _notificationSocket.SendAsync(new ArraySegment<byte>(subscribeBytes),
                        WebSocketMessageType.Text, true, CancellationToken.None);

                    _logger.LogInformation($"[INFO] Notification connection established to {_serverUrl}.");
                    
                    var syncRequest = JsonConvert.SerializeObject(new { command = "SYNC" });
                    var syncBytes = Encoding.UTF8.GetBytes(syncRequest);
                    await _notificationSocket.SendAsync(new ArraySegment<byte>(syncBytes), WebSocketMessageType.Text, true, cancellationToken);
                    
                    _logger.LogInformation("[INFO] Synchronization request sent to server.");
                    
                    var buffer = new byte[8192];
                    while (_notificationSocket.State == WebSocketState.Open &&
                           !cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogDebug("[DEBUG] Waiting for notification message...");
                        var result =
                            await _notificationSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _logger.LogWarning("[WARNING] Notification socket closed. Reconnecting...");
                            break;
                        }

                        if (result.MessageType != WebSocketMessageType.Text) continue;
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        _logger.LogInformation($"[INFO] Received notification: {message}");
                        await HandleNotificationAsync(message);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[ERROR] Notification receiver exception: {ex.Message}");
                }

                Console.WriteLine($"[INFO] Reconnecting notification socket in {retryDelay / 1000} seconds...");
                await Task.Delay(retryDelay, cancellationToken);
                retryDelay = Math.Min(retryDelay * 2, 30000);
            }
        }

        private static async Task<string?> ComputeFileHashAsync(string filePath)
        {
            try
            {
                using var sha256 = SHA256.Create();
                await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var hashBytes = await sha256.ComputeHashAsync(stream);
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
            catch (Exception ex)
            {
                _logger.LogError("Error computing hash for {FilePath}: {Message}", filePath, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Sends a notification to the server (only for file events, not directories).
        /// </summary>
        private static async Task SendNotificationAsync(string eventType, string relativePath, bool useFileTime = true)
        {
            var fullPath = Path.Combine(SyncFolder, relativePath);
            if ((eventType.Equals("created", StringComparison.OrdinalIgnoreCase) ||
                 eventType.Equals("modified", StringComparison.OrdinalIgnoreCase)) &&
                IsDirectory(fullPath))
            {
                _logger.LogInformation($"[INFO] Skipping notification for directory '{relativePath}'.");
                return;
            }

            if (eventType.Equals("deleted", StringComparison.OrdinalIgnoreCase) && LooksLikeDirectory(relativePath))
            {
                _logger.LogInformation($"[INFO] Skipping deletion notification for directory '{relativePath}'.");
                return;
            }

            if (_notificationSocket == null || _notificationSocket.State != WebSocketState.Open)
            {
                _logger.LogWarning($"[WARNING] Notification socket closed. Reconnecting...");
                await ReconnectNotificationSocketAsync();
            }

            if (_notificationSocket == null || _notificationSocket.State != WebSocketState.Open)
            {
                _logger.LogError(
                    $"[ERROR] Notification socket still closed. Not sending notification for '{relativePath}'.");
                return;
            }

            var timestamp = (useFileTime && File.Exists(fullPath))
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath)).ToUnixTimeSeconds()
                : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var fileSize = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
            string? fileHash = null;
            if ((eventType.Equals("created", StringComparison.OrdinalIgnoreCase) ||
                 eventType.Equals("modified", StringComparison.OrdinalIgnoreCase)) &&
                File.Exists(fullPath))
            {
                try
                {
                    fileHash = await ComputeFileHashAsync(fullPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Failed to compute hash for '{FileName}': {Message}", relativePath, ex.Message);
                }
            }

            var notification = new
            {
                @event = eventType,
                filename = relativePath,
                timestamp,
                size = fileSize,
                hash = fileHash
            };

            var json = JsonConvert.SerializeObject(notification);
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            try
            {
                _logger.LogInformation(
                    $"[INFO] Sending notification for '{relativePath}' ({eventType}) to server: {json}");
                await _notificationSocket.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, true,
                    CancellationToken.None);
            }
            catch (WebSocketException ex)
            {
                _logger.LogError(
                    $"[ERROR] WebSocketException while sending notification for '{relativePath}': {ex.Message}");
                await ReconnectNotificationSocketAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ERROR] Exception while sending notification for '{relativePath}': {ex.Message}");
            }
        }

        private static async Task ReconnectNotificationSocketAsync()
        {
            try
            {
                if (_notificationSocket != null)
                {
                    _logger.LogWarning("Closing existing notification socket...");
                    _notificationSocket.Dispose();
                }

                _notificationSocket = new ClientWebSocket();
                _notificationSocket.Options.RemoteCertificateValidationCallback =
                    (sender, certificate, chain, sslPolicyErrors) => true;
                await _notificationSocket.ConnectAsync(new Uri(_serverUrl), CancellationToken.None);
                // Immediately subscribe again.
                var subscribeMsg = JsonConvert.SerializeObject(new { subscribe = true });
                var subscribeBytes = Encoding.UTF8.GetBytes(subscribeMsg);
                await _notificationSocket.SendAsync(new ArraySegment<byte>(subscribeBytes), WebSocketMessageType.Text,
                    true, CancellationToken.None);
                Console.WriteLine("[INFO] Reconnected to notification server.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to reconnect to notification server: {ex.Message}");
            }
        }

        /// <summary>
        /// Processes messages from the server.
        /// </summary>
        private static async Task HandleNotificationAsync(string message)
        {
            try
            {
                var jsonObj = JsonConvert.DeserializeObject<dynamic>(message);
                if (jsonObj == null)
                    return;

                string file = jsonObj.filename.ToString();
                if (ShouldIgnoreFile(file))
                {
                    Console.WriteLine(
                        $"[INFO] Ignoring file '{file}' as it matches ignored prefixes/suffixes or is a directory.");
                    return;
                }
                
                // Handle sync request
                if (jsonObj.command != null && jsonObj.command == "SYNC_DATA")
                {
                    var fileList = jsonObj.files;
                    foreach (var syncFile in fileList)
                    {
                        var filename = syncFile.filename.ToString();
                        var serverTimestamp = (long)syncFile.timestamp;
                        var serverSize = (long)syncFile.size;
                        var serverHash = syncFile.hash?.ToString() ?? "";

                        var localPath = Path.Combine(SyncFolder, filename);
                        if (!File.Exists(localPath))
                        {
                            Console.WriteLine($"[SYNC] File '{filename}' missing locally. Requesting download.");
                            await DownloadFileAsync(filename);
                            continue;
                        }

                        var localSize = new FileInfo(localPath).Length;
                        var localTimestamp = new DateTimeOffset(File.GetLastWriteTimeUtc(localPath)).ToUnixTimeSeconds();
                        var localHash = await ComputeFileHashAsync(localPath);

                        if (string.Equals(serverHash, localHash, StringComparison.OrdinalIgnoreCase) &&
                            serverTimestamp <= localTimestamp && serverSize == localSize) continue;
                        Console.WriteLine($"[SYNC] File '{filename}' out of date. Downloading...");
                        await DownloadFileAsync(filename);
                    }

                    return;
                }

                // Handle upload request
                if (jsonObj.command != null && jsonObj.command == "REQUEST_UPLOAD")
                {
                    string relativePath = jsonObj.filename;
                    Console.WriteLine($"[SERVER REQUEST] Upload requested for: {relativePath}");
                    var fullPath = Path.Combine(SyncFolder, relativePath);

                    if (File.Exists(fullPath))
                    {
                        if (RecentDownloads.TryGetValue(relativePath, out var lastDownloadTime))
                        {
                            var secondsSinceDownload = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lastDownloadTime;
                            if (secondsSinceDownload < UploadCooldownSeconds)
                            {
                                Console.WriteLine(
                                    $"[INFO] Skipping upload of '{relativePath}' (downloaded {secondsSinceDownload}s ago).");
                                return;
                            }
                        }

                        await UploadFileAsync(relativePath);
                    }
                    else
                    {
                        Console.WriteLine($"[INFO] File '{relativePath}' does not exist locally, skipping upload.");
                    }

                    return;
                }

                // Handle change notification
                if (jsonObj.@event != null)
                {
                    var eventType = jsonObj.@event.ToString();
                    var filename = jsonObj.filename.ToString();
                    var serverTimestamp = (long)jsonObj.timestamp;
                    var serverFileSize = (long)jsonObj.size;
                    var serverHash = jsonObj.hash?.ToString();
                    var localFilePath = Path.Combine(SyncFolder, filename);

                    Console.WriteLine(
                        $"[SERVER NOTIFICATION] File '{filename}' {eventType} at {serverTimestamp} (size: {serverFileSize} bytes).");

                    // Deleted file handling
                    if (eventType == "deleted")
                    {
                        if (File.Exists(localFilePath))
                        {
                            File.Delete(localFilePath);
                            Console.WriteLine($"[INFO] File '{filename}' was deleted remotely. Deleted it locally.");
                        }
                        else
                        {
                            Console.WriteLine(
                                $"[INFO] File '{filename}' was already deleted locally. No action needed.");
                        }

                        return;
                    }

                    // Created or Modified handling
                    if (eventType == "created" || eventType == "modified")
                    {
                        // Ensure directory exists
                        var dir = Path.GetDirectoryName(localFilePath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);

                        // If the file doesn't exist at all
                        if (!File.Exists(localFilePath))
                        {
                            Console.WriteLine($"[INFO] File '{filename}' does not exist locally. Downloading...");
                            await DownloadFileAsync(filename);
                            RecentDownloads[filename] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            return;
                        }

                        // Prevent redundant re-downloads for recent uploads
                        if (RecentUploads.TryGetValue(filename, out long recentUploadTime))
                        {
                            var elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - recentUploadTime;
                            if (elapsed < 20)
                            {
                                Console.WriteLine(
                                    $"[INFO] Notification for '{filename}' ignored (recent upload {elapsed}s ago).");
                                return;
                            }
                        }

                        // Hash check
                        if (!string.IsNullOrEmpty(serverHash))
                        {
                            var localHash = await ComputeFileHashAsync(localFilePath);
                            if (serverHash?.Equals(localHash, StringComparison.OrdinalIgnoreCase))
                            {
                                Console.WriteLine(
                                    $"[INFO] Local hash matches server for '{filename}'; skipping download.");
                                return;
                            }
                        }

                        // Fallback: timestamp or filesize mismatch
                        var localModifiedTime =
                            new DateTimeOffset(File.GetLastWriteTimeUtc(localFilePath)).ToUnixTimeSeconds();
                        var localFileSize = new FileInfo(localFilePath).Length;

                        if (serverTimestamp > localModifiedTime || serverFileSize != localFileSize)
                        {
                            Console.WriteLine($"[INFO] Difference detected for '{filename}'; downloading...");
                            await DownloadFileAsync(filename);
                            RecentDownloads[filename] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        }
                        else
                        {
                            Console.WriteLine($"[INFO] No significant difference for '{filename}', skipping download.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error handling notification: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Uploads a file using a transient WebSocket connection.
        /// </summary>
        private static async Task UploadFileAsync(string relativePath)
        {
            var filePath = Path.Combine(SyncFolder, relativePath);
            if (!File.Exists(filePath))
            {
                _logger.LogError("File '{FilePath}' does not exist.", filePath);
                return;
            }

            var metadata = new { command = "UPLOAD", filename = relativePath };
            try
            {
                using var clientWebSocket = new ClientWebSocket();
                clientWebSocket.Options.RemoteCertificateValidationCallback =
                    (sender, certificate, chain, sslPolicyErrors) => true;
                await clientWebSocket.ConnectAsync(new Uri(_serverUrl), CancellationToken.None);
                Console.WriteLine("[INFO] Connected to server for upload.");
                var metadataJson = JsonConvert.SerializeObject(metadata);
                var metadataBytes = Encoding.UTF8.GetBytes(metadataJson);
                await clientWebSocket.SendAsync(new ArraySegment<byte>(metadataBytes), WebSocketMessageType.Text, true,
                    CancellationToken.None);
                await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                var buffer = new byte[8192];
                int bytesRead;
                while ((bytesRead = await fileStream.ReadAsync(buffer)) > 0)
                {
                    await clientWebSocket.SendAsync(new ArraySegment<byte>(buffer, 0, bytesRead),
                        WebSocketMessageType.Binary, true, CancellationToken.None);
                }

                var eofBytes = Encoding.UTF8.GetBytes("EOF");
                await clientWebSocket.SendAsync(new ArraySegment<byte>(eofBytes), WebSocketMessageType.Text, true,
                    CancellationToken.None);
                Console.WriteLine($"[INFO] File '{relativePath}' uploaded successfully.");
                await clientWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Upload complete",
                    CancellationToken.None);

                // Record the upload time so subsequent notifications are ignored.
                RecentUploads[relativePath] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
            catch (Exception ex)
            {
                _logger.LogError("Error uploading file: {Message}", ex.Message);
            }
        }

        private static void StartStaleFileCleanup()
        {
            var partialFilesExist = Directory.EnumerateFiles(SyncFolder)
                .Any(f => f.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase));
            if (partialFilesExist)
            {
                _logger.LogInformation("Stale partial file(s) detected. Starting cleanup task.");
                _ = Task.Run(() => CleanStalePartialFilesAsync());
            }
            else
            {
                _logger.LogInformation("No partial files found. Cleanup task not needed.");
            }
        }

        private static async Task CleanStalePartialFilesAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(PartialFileTimeoutSeconds + 1));
            DateTime currentTime = DateTime.UtcNow;
            try
            {
                var partialFiles = Directory.EnumerateFiles(SyncFolder)
                    .Where(f => f.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase));
                foreach (var file in partialFiles)
                {
                    var lastWriteTime = File.GetLastWriteTimeUtc(file);
                    if ((currentTime - lastWriteTime).TotalSeconds > PartialFileTimeoutSeconds)
                    {
                        _logger.LogInformation("Deleting stale file: {FilePath}", file);
                        try
                        {
                            File.Delete(file);
                        }
                        catch (Exception deleteEx)
                        {
                            _logger.LogError(deleteEx, "Unable to delete stale file: {FilePath}", file);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during stale partial files cleanup.");
            }
        }

        /// <summary>
        /// Downloads a file using a transient WebSocket connection.
        /// </summary>
        private static async Task DownloadFileAsync(string relativePath)
        {
            var tempFileName = relativePath + PartialSuffix;
            var metadata = new { command = "DOWNLOAD", filename = relativePath };
            try
            {
                using var clientWebSocket = new ClientWebSocket();
                clientWebSocket.Options.RemoteCertificateValidationCallback =
                    (sender, certificate, chain, sslPolicyErrors) => true;
                await clientWebSocket.ConnectAsync(new Uri(_serverUrl), CancellationToken.None);
                Console.WriteLine("[INFO] Connected to server for download.");
                var metadataJson = JsonConvert.SerializeObject(metadata);
                var metadataBytes = Encoding.UTF8.GetBytes(metadataJson);
                await clientWebSocket.SendAsync(new ArraySegment<byte>(metadataBytes), WebSocketMessageType.Text, true,
                    CancellationToken.None);
                var tempFilePath = Path.Combine(SyncFolder, tempFileName);
                var newFilePath = Path.Combine(SyncFolder, relativePath);
                var fileDirectory = Path.GetDirectoryName(newFilePath);
                if (!string.IsNullOrEmpty(fileDirectory) && !Directory.Exists(fileDirectory))
                {
                    Directory.CreateDirectory(fileDirectory);
                }

                await using var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write);
                var buffer = new byte[8192];
                bool eofReceived = false;
                while (clientWebSocket.State == WebSocketState.Open && !eofReceived)
                {
                    var result =
                        await clientWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        if (message != "EOF") continue;
                        eofReceived = true;
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, result.Count));
                    }
                }

                if (File.Exists(newFilePath))
                {
                    File.Delete(newFilePath);
                }

                File.Move(tempFilePath, newFilePath);

                Console.WriteLine($"[INFO] File '{relativePath}' downloaded successfully.");
                await clientWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Download complete",
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error downloading file: {Message}", ex.Message);
            }
            finally
            {
                StartStaleFileCleanup();
            }
        }

        /// <summary>
        /// Deletes a file via a transient WebSocket connection.
        /// </summary>
        private static async Task DeleteFileAsync(string relativePath)
        {
            var metadata = new { command = "DELETE", filename = relativePath };
            try
            {
                using var clientWebSocket = new ClientWebSocket();
                clientWebSocket.Options.RemoteCertificateValidationCallback =
                    (sender, certificate, chain, sslPolicyErrors) => true;
                await clientWebSocket.ConnectAsync(new Uri(_serverUrl), CancellationToken.None);
                Console.WriteLine("[INFO] Connected to server for deletion request.");
                var metadataJson = JsonConvert.SerializeObject(metadata);
                var metadataBytes = Encoding.UTF8.GetBytes(metadataJson);
                await clientWebSocket.SendAsync(new ArraySegment<byte>(metadataBytes), WebSocketMessageType.Text, true,
                    CancellationToken.None);
                var buffer = new byte[8192];
                var result = await clientWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                var responseText = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var response = JsonConvert.DeserializeObject<dynamic>(responseText);
                if (response?.status == "OK")
                {
                    var filePath = Path.Combine(SyncFolder, relativePath);
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        Console.WriteLine($"[INFO] File '{relativePath}' deleted locally.");
                    }
                }
                else
                {
                    Console.WriteLine($"[INFO] Server response: {response?.message}");
                }

                await clientWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Delete complete",
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error deleting file: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Lists files via a transient WebSocket connection.
        /// </summary>
        private static async Task ListFilesAsync()
        {
            var metadata = new { command = "LIST" };
            try
            {
                using var clientWebSocket = new ClientWebSocket();
                clientWebSocket.Options.RemoteCertificateValidationCallback =
                    (sender, certificate, chain, sslPolicyErrors) => true;
                await clientWebSocket.ConnectAsync(new Uri(_serverUrl), CancellationToken.None);
                Console.WriteLine("[INFO] Connected to server for listing files.");
                var metadataJson = JsonConvert.SerializeObject(metadata);
                var metadataBytes = Encoding.UTF8.GetBytes(metadataJson);
                await clientWebSocket.SendAsync(new ArraySegment<byte>(metadataBytes), WebSocketMessageType.Text, true,
                    CancellationToken.None);
                var buffer = new byte[8192];
                var result = await clientWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                var responseText = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var response = JsonConvert.DeserializeObject<dynamic>(responseText);
                if (response?.files != null)
                {
                    Console.WriteLine("[INFO] Files on server:");
                    foreach (var file in response.files)
                    {
                        Console.WriteLine($"- {file}");
                    }
                }
                else
                {
                    Console.WriteLine("[INFO] No files found on server.");
                }

                await clientWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "List complete",
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error listing files: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Debounces file change notifications.
        /// </summary>
        private static void DebounceNotification(string relativePath, string eventType)
        {
            if (DebounceTokens.TryRemove(relativePath, out var existingCts))
            {
                existingCts.Cancel();
                existingCts.Dispose();
            }

            var cts = new CancellationTokenSource();
            DebounceTokens[relativePath] = cts;
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(3000, cts.Token);
                    if (cts.Token.IsCancellationRequested) return;
                    Console.WriteLine($"[LOCAL] {eventType}: {relativePath}");
                    await SendNotificationAsync(eventType, relativePath);
                }
                catch (TaskCanceledException)
                {
                }
                finally
                {
                    DebounceTokens.TryRemove(relativePath, out _);
                }
            }, cts.Token);
        }

        /// <summary>
        /// Returns true if the file should be ignored.
        /// </summary>
        private static bool ShouldIgnoreFile(string path)
        {
            // Get the file name from the path.
            var fileName = Path.GetFileName(path);

            // Check if the file name starts with any ignored prefix.
            if (IgnoredPrefixes.Any(prefix => fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                return true;

            // Check if the file name ends with any ignored suffix.
            if (IgnoredSuffixes.Any(suffix => fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                return true;

            // If the path exists and is a directory, ignore it.
            if (Directory.Exists(path))
                return true;

            // If the file does not exist, we can use a heuristic:
            // Assume that if the file name does not have an extension, it is likely a directory.
            return !Path.HasExtension(fileName);
        }

        /// <summary>
        /// Starts the file system watcher.
        /// </summary>
        private static void StartLocalFileWatcher()
        {
            var watcher = new FileSystemWatcher(SyncFolder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                Filter = "*.*",
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            watcher.Created += async (_, e) =>
            {
                var relativePath = Path.GetRelativePath(Path.GetFullPath(SyncFolder), Path.GetFullPath(e.FullPath));
                if (Directory.Exists(e.FullPath)) return;
                if (ShouldIgnoreFile(relativePath)) return;
                await Task.Delay(500);
                Console.WriteLine($"[LOCAL] Created: {relativePath}");
                DebounceNotification(relativePath, "created");
            };

            watcher.Changed += async (_, e) =>
            {
                var relativePath = Path.GetRelativePath(Path.GetFullPath(SyncFolder), Path.GetFullPath(e.FullPath));
                if (Directory.Exists(e.FullPath)) return;
                if (ShouldIgnoreFile(relativePath)) return;
                await Task.Delay(500);
                Console.WriteLine($"[LOCAL] Changed: {relativePath}");
                DebounceNotification(relativePath, "modified");
            };

            watcher.Deleted += async (_, e) =>
            {
                var relativePath = Path.GetRelativePath(Path.GetFullPath(SyncFolder), Path.GetFullPath(e.FullPath));
                if (!Path.HasExtension(relativePath)) return;
                if (ShouldIgnoreFile(relativePath)) return;
                Console.WriteLine($"[LOCAL] Deleted: {relativePath}");
                await SendNotificationAsync("deleted", relativePath);
            };

            watcher.Renamed += async (_, e) =>
            {
                var oldRelative = Path.GetRelativePath(Path.GetFullPath(SyncFolder), Path.GetFullPath(e.OldFullPath));
                var newRelative = Path.GetRelativePath(Path.GetFullPath(SyncFolder), Path.GetFullPath(e.FullPath));
                if (!Path.HasExtension(newRelative)) return;
                if (ShouldIgnoreFile(newRelative)) return;
                Console.WriteLine($"[LOCAL] Renamed: {oldRelative} -> {newRelative}");
                await SendNotificationAsync("deleted", oldRelative);
                await SendNotificationAsync("created", newRelative);
            };

            Console.WriteLine("[INFO] Local file watcher started.");
        }
    }
}