using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Text.RegularExpressions;


namespace TcpServer
{
    internal class WebSocketFileClient
    {
        private const string ServerUrl = "ws://127.0.0.1:5678";
        private static readonly string SyncFolder = Path.Combine(Directory.GetCurrentDirectory(), "SyncedFiles");
        private static ILogger<WebSocketFileClient> _logger = null!;
        private static ClientWebSocket? _notificationSocket;
        private static readonly ConcurrentDictionary<string, long> LastNotificationTimes = new();

        // Cancellation token for the notification receiver
        private static CancellationTokenSource? _notificationCts;

        private static async Task Main()
        {
            _logger = SetupLogging();
            Console.WriteLine("[INFO] Welcome to the WebSocket File Transfer Client!");
            PrintHelp();

            if (!Directory.Exists(SyncFolder))
                Directory.CreateDirectory(SyncFolder);

            // Start the persistent notification receiver
            _notificationCts = new CancellationTokenSource();
            var notificationTask = StartNotificationReceiverAsync(_notificationCts.Token);

            // Start the local file watcher to detect changes and send notifications to the server
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
                    await UploadFileAsync(filePath);
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
                    await DownloadFileAsync(fileName);
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
                    await DeleteFileAsync(fileName);
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
                    })
                    .SetMinimumLevel(LogLevel.Information);
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
        
        // FE10 sanitize and compatibility
        public static class FileNameSanitizer
        {
            public static string SanitizeFilename(string filename, int maxLength = 200, string targetCharset = "UTF-8")
            {
                if (string.IsNullOrWhiteSpace(filename))
                {
                    throw new ArgumentException("Filename cannot be null or empty.", nameof(filename));
                }


                // Normaliseer naar Unicode NFC-formaat.
                filename = filename.Normalize(NormalizationForm.FormC);

                // Verwijder alle Unicode-verwijzingen.
                filename = Regex.Replace(filename, @"[<>:""/\\|?*\x00-\x1F;`&|$!'()]", "");
                
                if (targetCharset.Equals("ASCII", StringComparison.OrdinalIgnoreCase))
                {
                    // Replace non-ASCII characters with an empty string.
                    var asciiBytes = Encoding.ASCII.GetBytes(filename);
                    filename = Encoding.ASCII.GetString(asciiBytes);
                }
                // Trim leading and trailing spaces or dots.
                filename = filename.Trim(' ', '.');
                filename = filename.ToLowerInvariant();

                // Choose encoding based on targetCharset.
                Encoding encoding;
                try
                {
                    encoding = Encoding.GetEncoding(targetCharset);
                }
                catch (Exception ex)
                {
                    throw new ArgumentException($"The provided target charset '{targetCharset}' is not valid.", nameof(targetCharset), ex);
                }


                // Check if the filename length exceeds the maximum allowed length in the given encoding.
                int byteLength = encoding.GetByteCount(filename);
                if (byteLength > maxLength)
                {
                    throw new ArgumentException($"Filename exceeds the maximum length of {maxLength} bytes in {targetCharset} encoding.");
                }

                // If the filename becomes empty after sanitization, throw an error.
                if (string.IsNullOrEmpty(filename))
                {
                    throw new ArgumentException("Filename became empty after sanitization.");
                }
                
                return filename;
            }
        }


        /// <summary>
        /// Starts a persistent ClientWebSocket connection to receive notifications from the server.
        /// Reconnects automatically if the connection is lost.
        /// </summary>
        private static async Task StartNotificationReceiverAsync(CancellationToken cancellationToken)
        {
            var retryDelay = 2000; // Start with a 2-second delay

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _notificationSocket = new ClientWebSocket();
                    await _notificationSocket.ConnectAsync(new Uri(ServerUrl), cancellationToken);
                    _logger.LogInformation($"[INFO] Connected to notification server {ServerUrl}.");

                    var buffer = new byte[8192];

                    while (_notificationSocket.State == WebSocketState.Open &&
                           !cancellationToken.IsCancellationRequested)
                    {
                        var result =
                            await _notificationSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _logger.LogWarning("[WARNING] Notification server closed the connection. Reconnecting...");
                            break; // Break out of loop to trigger reconnect
                        }

                        if (result.MessageType != WebSocketMessageType.Text) continue;
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        await HandleNotificationAsync(message);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError("Error in notification receiver: {Message}", ex.Message);
                }
                // Exponential backoff to avoid frequent retries on failures
                Console.WriteLine($"[INFO] Reconnecting in {retryDelay / 1000} seconds...");
                await Task.Delay(retryDelay, cancellationToken);
        
                retryDelay = Math.Min(retryDelay * 2, 30000); // Max delay of 30 seconds
            }
        }

        /// <summary>
        /// Sends a notification message to the server. 
        /// </summary>
        private static async Task SendNotificationAsync(string eventType, string fullPath, bool useFileTime = true)
        {
            var filename = Path.GetFileName(fullPath);
            var timestamp = (useFileTime && File.Exists(fullPath))
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath)).ToUnixTimeSeconds()
                : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var fileSize = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;

            // **Prevent duplicate notifications within 5 seconds**
            LastNotificationTimes.AddOrUpdate(
                filename,
                timestamp, // If new entry, store timestamp
                (_, lastSent) =>
                {
                    if (timestamp - lastSent < 5)
                    {
                        _logger.LogWarning($"Skipping duplicate notification for '{filename}' within cooldown period.");
                        return lastSent; // Keep existing timestamp
                    }

                    return timestamp; // Update timestamp
                });

            // **Ignore empty files to prevent premature uploads**
            if (fileSize == 0)
            {
                _logger.LogWarning($"Ignoring file '{filename}' because its size is 0 bytes.");
                return;
            }

            // **Ensure WebSocket connection is alive**
            if (_notificationSocket is not { State: WebSocketState.Open })
            {
                _logger.LogWarning($"Notification socket is not open, attempting to reconnect...");
                await ReconnectNotificationSocketAsync();
                if (_notificationSocket is not { State: WebSocketState.Open })
                {
                    _logger.LogError(
                        $"Failed to reconnect notification socket. Notification for '{filename}' not sent.");
                    return;
                }
            }

            var notification = new
            {
                @event = eventType,
                filename = filename,
                timestamp = timestamp,
                size = fileSize
            };

            var json = JsonConvert.SerializeObject(notification);
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            await _notificationSocket.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, true,
                CancellationToken.None);

            Console.WriteLine(
                $"[INFO] Sent notification: File '{filename}' {eventType} at {timestamp} (size: {fileSize} bytes)");
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
                await _notificationSocket.ConnectAsync(new Uri(ServerUrl), CancellationToken.None);
                Console.WriteLine("[INFO] Reconnected to notification server.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to reconnect to notification server: {ex.Message}");
            }
        }

        /// <summary>
        /// Processes a notification message from the server.
        /// Expected JSON format: { "event": "created" | "modified" | "deleted", "filename": "example.txt", "timestamp": 1700000000 }
        /// </summary>
        private static async Task HandleNotificationAsync(string message)
        {
            try
            {
                var jsonObj = JsonConvert.DeserializeObject<dynamic>(message);
                if (jsonObj == null)
                    return;

                // Handle a "REQUEST_UPLOAD" command from the server
                if (jsonObj.command != null && jsonObj.command == "REQUEST_UPLOAD")
                {
                    string filename = jsonObj.filename;
                    Console.WriteLine($"[SERVER REQUEST] Upload requested for: {filename}");
                    var filePath = Path.Combine(SyncFolder, filename);

                    if (File.Exists(filePath))
                    {
                        await UploadFileAsync(filePath);
                    }
                    else
                    {
                        _logger.LogWarning($"[INFO] File '{filename}' does not exist locally, skipping upload.");
                    }

                    return;
                }

                // Process a file change notification
                if (jsonObj.@event != null)
                {
                    var eventType = jsonObj.@event.ToString();
                    var filename = jsonObj.filename.ToString();
                    var serverTimestamp = (long)jsonObj.timestamp;
                    var serverFileSize = (long)jsonObj.size;
                    Console.WriteLine(
                        $"[SERVER NOTIFICATION] File '{filename}' {eventType} at {serverTimestamp} (size: {serverFileSize} bytes).");

                    var localFilePath = Path.Combine(SyncFolder, filename);
                    var fileExists = File.Exists(localFilePath);
                    var shouldDownload = false;

                    if (eventType == "deleted")
                    {
                        if (fileExists)
                        {
                            File.Delete(localFilePath);
                            Console.WriteLine(
                                $"[INFO] File '{filename}' deleted locally as per server notification.");
                        }
                        else
                        {
                            Console.WriteLine($"[INFO] File '{filename}' was already deleted locally.");
                        }

                        return; // Stop further processing
                    }

                    if (!fileExists)
                    {
                        Console.WriteLine($"[INFO] File '{filename}' does not exist locally. Downloading...");
                        shouldDownload = true;
                    }
                    else
                    {
                        var localModifiedTime =
                            new DateTimeOffset(File.GetLastWriteTimeUtc(localFilePath)).ToUnixTimeSeconds();
                        var localFileSize = new FileInfo(localFilePath).Length;

                        if (serverTimestamp > localModifiedTime || serverFileSize != localFileSize)
                        {
                            Console.WriteLine(
                                $"[INFO] Newer version of '{filename}' detected (server: {serverTimestamp}, local: {localModifiedTime}). Downloading...");
                            shouldDownload = true;
                        }
                    }

                    if ((eventType == "created" || eventType == "modified") && shouldDownload)
                    {
                        await DownloadFileAsync(filename);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error handling notification: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Uploads a file to the server using ClientWebSocket.
        /// </summary>
        private static async Task UploadFileAsync(string filePath)
        {
            if (!File.Exists(filePath))
            {
                _logger.LogError("File '{FilePath}' does not exist.", filePath);
                return;
            }

            var fileName = Path.GetFileName(filePath);
            var metadata = new { command = "UPLOAD", filename = fileName };

            try
            {
                using var clientWebSocket = new ClientWebSocket();
                await clientWebSocket.ConnectAsync(new Uri(ServerUrl), CancellationToken.None);
                Console.WriteLine("[INFO] Connected to server for upload.");

                // Send upload command
                var metadataJson = JsonConvert.SerializeObject(metadata);
                var metadataBytes = Encoding.UTF8.GetBytes(metadataJson);
                await clientWebSocket.SendAsync(new ArraySegment<byte>(metadataBytes), WebSocketMessageType.Text, true,
                    CancellationToken.None);

                // Open file and send its contents
                await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                var buffer = new byte[8192];
                int bytesRead;
                while ((bytesRead = await fileStream.ReadAsync(buffer)) > 0)
                {
                    await clientWebSocket.SendAsync(new ArraySegment<byte>(buffer, 0, bytesRead),
                        WebSocketMessageType.Binary, true, CancellationToken.None);
                }

                // Send EOF marker
                var eofBytes = Encoding.UTF8.GetBytes("EOF");
                await clientWebSocket.SendAsync(new ArraySegment<byte>(eofBytes), WebSocketMessageType.Text, true,
                    CancellationToken.None);
                Console.WriteLine("[INFO] File '{0}' uploaded successfully.", fileName);

                await clientWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Upload complete",
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error uploading file: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Downloads a file from the server using ClientWebSocket.
        /// </summary>
        private static async Task DownloadFileAsync(string fileName)
        {
            var metadata = new { command = "DOWNLOAD", filename = fileName };

            try
            {
                using var clientWebSocket = new ClientWebSocket();
                await clientWebSocket.ConnectAsync(new Uri(ServerUrl), CancellationToken.None);
                Console.WriteLine("[INFO] Connected to server for download.");

                // Send download command
                var metadataJson = JsonConvert.SerializeObject(metadata);
                var metadataBytes = Encoding.UTF8.GetBytes(metadataJson);
                await clientWebSocket.SendAsync(new ArraySegment<byte>(metadataBytes), WebSocketMessageType.Text, true,
                    CancellationToken.None);

                var filePath = Path.Combine(SyncFolder, fileName);
                await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
                var buffer = new byte[8192];
                var eofReceived = false;

                while (clientWebSocket.State == WebSocketState.Open && !eofReceived)
                {
                    var result =
                        await clientWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        // Check if it's the EOF marker
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        if (message == "EOF")
                        {
                            eofReceived = true;
                            break;
                        }

                        {
                            _logger.LogWarning("Unexpected text message: {Message}", message);
                        }
                    }
                    else if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, result.Count));
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Console.WriteLine("[INFO] Server closed the connection.");
                        break;
                    }
                }

                Console.WriteLine(eofReceived
                    ? $"File '{fileName}' downloaded successfully."
                    : $"File '{fileName}' download incomplete.");

                await clientWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Download complete",
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error downloading file: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Deletes a file from the server and locally.
        /// </summary>
        private static async Task DeleteFileAsync(string fileName)
        {
            var metadata = new { command = "DELETE", filename = fileName };

            try
            {
                using var clientWebSocket = new ClientWebSocket();
                await clientWebSocket.ConnectAsync(new Uri(ServerUrl), CancellationToken.None);
                Console.WriteLine("[INFO] Connected to server for deletion request.");

                // Send DELETE command
                var metadataJson = JsonConvert.SerializeObject(metadata);
                var metadataBytes = Encoding.UTF8.GetBytes(metadataJson);
                await clientWebSocket.SendAsync(new ArraySegment<byte>(metadataBytes), WebSocketMessageType.Text, true,
                    CancellationToken.None);

                // Await server response
                var buffer = new byte[8192];
                var result = await clientWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                var responseText = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var response = JsonConvert.DeserializeObject<dynamic>(responseText);

                if (response?.status == "OK")
                {
                    var filePath = Path.Combine(SyncFolder, fileName);
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        Console.WriteLine($"[INFO] File '{fileName}' deleted locally.");
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
        /// Lists files on the server using ClientWebSocket.
        /// </summary>
        private static async Task ListFilesAsync()
        {
            var metadata = new { command = "LIST" };

            try
            {
                using var clientWebSocket = new ClientWebSocket();
                await clientWebSocket.ConnectAsync(new Uri(ServerUrl), CancellationToken.None);
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

        private static void StartLocalFileWatcher()
        {
            var watcher = new FileSystemWatcher(SyncFolder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                Filter = "*.*",
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            watcher.Created += async (_, e) =>
            {
                // Sla tijdelijke of verborgen bestanden over.
                string originalFileName = Path.GetFileName(e.FullPath);
                if (originalFileName.StartsWith("~$") || originalFileName.StartsWith("."))
                    return; 

                await Task.Delay(500); 

                string sanitizedFileName;
                try
                {
                    sanitizedFileName = FileNameSanitizer.SanitizeFilename(originalFileName);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LOCAL] Fout tijdens het sanitizen van de bestandsnaam: {ex.Message}");
                    return;
                }

                string directory = Path.GetDirectoryName(e.FullPath);
                string sanitizedFullPath = Path.Combine(directory, sanitizedFileName);

                if (!string.Equals(originalFileName, sanitizedFileName, StringComparison.Ordinal))
                {
                    try
                    {
                        File.Move(e.FullPath, sanitizedFullPath);
                        Console.WriteLine($"[LOCAL] Bestand hernoemd van '{originalFileName}' naar '{sanitizedFileName}'.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[LOCAL] Fout tijdens hernoemen: {ex.Message}");
                        return;
                    }
                }
                else
                {
                    // Indien de naam niet gewijzigd wordt, gebruik dan het originele pad.
                    sanitizedFullPath = e.FullPath;
                }

                Console.WriteLine($"[LOCAL] File created: {sanitizedFileName}");
                await SendNotificationAsync("created", sanitizedFullPath);
            };


            watcher.Changed += async (_, e) =>
            {
                if (Path.GetFileName(e.FullPath).StartsWith("~$") || Path.GetFileName(e.FullPath).StartsWith("."))
                    return;
                await Task.Delay(500); // Prevent multiple rapid events
                Console.WriteLine($"[LOCAL] File changed: {e.Name}");
                await SendNotificationAsync("modified", e.FullPath);
            };

            watcher.Deleted += async (_, e) =>
            {
                if (Path.GetFileName(e.FullPath).StartsWith("~$") || Path.GetFileName(e.FullPath).StartsWith("."))
                    return;
                Console.WriteLine($"[LOCAL] File deleted: {e.Name}");
                await SendNotificationAsync("deleted", e.FullPath, useFileTime: false);
            };

            Console.WriteLine("[INFO] Local file watcher started.");
        }
    }
}