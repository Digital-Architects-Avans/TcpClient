using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TcpServer
{
    internal class TcpServer
    {
        // Default server settings
        private static string _serverHost = "127.0.0.1";
        private static int _serverPort = 12345;

        // Logger instance
        private static ILogger<TcpServer> _logger;

        private static async Task Main()
        {
            // Set up logging (make sure Microsoft.Extensions.Logging.Console is installed)
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder
                    .AddSimpleConsole(options =>
                    {
                        options.IncludeScopes = false;
                        options.SingleLine = true;
                        options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                    })
                    .SetMinimumLevel(LogLevel.Information);
            });
            _logger = loggerFactory.CreateLogger<TcpServer>();

            Console.WriteLine("Welcome to the File Transfer Client!");
            PrintHelp();

            while (true)
            {
                Console.Write("Enter command: ");
                var userInput = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(userInput))
                {
                    Console.Write("Please enter a command. ");
                    continue;
                    // TODO: Add a loop to prevent empty input
                }

                if (userInput.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
                {
                    PrintHelp();
                }
                else if (userInput.StartsWith("/upload", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = userInput.Split([' '], 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2)
                    {
                        Console.WriteLine("Usage: /upload <file_path>");
                        continue;
                    }
                    var filePath = parts[1].Trim().Trim('\'', '"');
                    await SendFileAsync(_serverHost, _serverPort, filePath);
                }
                else if (userInput.StartsWith("/download", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = userInput.Split([' '], 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2)
                    {
                        Console.WriteLine("Usage: /download <file_name>");
                        continue;
                    }
                    var fileName = parts[1].Trim();
                    await DownloadFileAsync(_serverHost, _serverPort, fileName);
                }
                else if (userInput.StartsWith("/list", StringComparison.OrdinalIgnoreCase))
                {
                    await ListFilesAsync(_serverHost, _serverPort);
                }
                else if (userInput.StartsWith("/set", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = userInput.Split([' '], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 3)
                    {
                        Console.WriteLine("Usage: /set <server_host> <server_port>");
                        continue;
                    }
                    _serverHost = parts[1];
                    if (!int.TryParse(parts[2], out _serverPort))
                    {
                        Console.WriteLine("Server port must be an integer.");
                        continue;
                    }
                    Console.WriteLine($"Server settings updated to {_serverHost}:{_serverPort}");
                }
                else if (userInput.StartsWith("/quit", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Exiting.");
                    break;
                }
                else
                {
                    Console.WriteLine("Unknown command. Type /help for a list of commands.");
                }
            }
        }

        // TODO: Move the help functionality to the server side, display on connection or setting server ip/port
        private static void PrintHelp()
        {
            var helpText = $"""

                            Available commands:
                            /help                             - Show this help message.
                            /upload <file_path>               - Upload a file to the server.
                            /download <file_name>             - Download a file from the server.
                            /list                             - List all files on the server.
                            /set <server_host> <server_port>  - Set the server host and port (default is {_serverHost}:{_serverPort}).
                            /quit                             - Exit the application.

                            """;
            Console.WriteLine(helpText);
        }

        // UPLOAD
        private static async Task SendFileAsync(string serverHost, int serverPort, string filePath)
        {
            if (!File.Exists(filePath))
            {
                _logger.LogError("File '{FilePath}' does not exist.", filePath);
                return;
            }

            var fileInfo = new FileInfo(filePath);
            var fileName = fileInfo.Name;
            var totalSize = fileInfo.Length;
            var totalSizeStr = totalSize.ToString();

            try
            {
                using var client = new TcpClient();
                // Connect with a timeout of 60 seconds
                var connectTask = client.ConnectAsync(serverHost, serverPort);
                if (await Task.WhenAny(connectTask, Task.Delay(60000)) != connectTask)
                {
                    _logger.LogError("Connection timed out.");
                    return;
                }

                await using var stream = client.GetStream();
                stream.ReadTimeout = 60000;
                stream.WriteTimeout = 60000;

                // Build request header
                var headerLines = new[]
                {
                    "UPLOAD",
                    $"File-Name: {fileName}",
                    $"Content-Length: {totalSizeStr}",
                    ""
                };
                var header = string.Join("\r\n", headerLines) + "\r\n";
                var headerBytes = Encoding.UTF8.GetBytes(header);
                await stream.WriteAsync(headerBytes);

                // Wait for server response
                var responseBuffer = new byte[1024];
                var bytesRead = await stream.ReadAsync(responseBuffer);
                var response = Encoding.UTF8.GetString(responseBuffer, 0, bytesRead).Trim();
                _logger.LogInformation("Server response: {Response}", response);

                long resumeOffset = 0;
                if (response.StartsWith("Status: RESUME", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3 && long.TryParse(parts[2], out long offset))
                    {
                        resumeOffset = offset;
                        _logger.LogInformation("Resuming transfer from byte {Offset}", resumeOffset);
                    }
                    else
                    {
                        _logger.LogError("Invalid resume response format.");
                        return;
                    }
                }
                else if (response.StartsWith("Status: 200", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Starting new transfer");
                }
                else
                {
                    _logger.LogError("Unexpected server response: {Response}", response);
                    return;
                }

                // Open the file and send data starting at resumeOffset
                await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                fileStream.Seek(resumeOffset, SeekOrigin.Begin);
                var sentBytes = resumeOffset;
                var buffer = new byte[4096];
                int read;
                while ((read = await fileStream.ReadAsync(buffer)) > 0)
                {
                    // Ensure that we don't send more than the remaining bytes
                    if (sentBytes + read > totalSize)
                    {
                        read = (int)(totalSize - sentBytes);
                    }

                    await stream.WriteAsync(buffer, 0, read);
                    sentBytes += read;
                }
                _logger.LogInformation("File '{FileName}' sent successfully. Total bytes sent: {SentBytes}/{TotalSize}", fileName, sentBytes, totalSize);
            }
            catch (SocketException ex)
            {
                _logger.LogError("Socket error sending file: {Message}", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error sending file: {Message}", ex.Message);
            }
        }

        // DOWNLOAD
        private static async Task DownloadFileAsync(string serverHost, int serverPort, string fileName)
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(serverHost, serverPort);
                if (await Task.WhenAny(connectTask, Task.Delay(60000)) != connectTask)
                {
                    _logger.LogError("Connection timed out.");
                    return;
                }

                await using var stream = client.GetStream();
                stream.ReadTimeout = 60000;
                stream.WriteTimeout = 60000;

                // Build download request header
                var headerLines = new[]
                {
                    "DOWNLOAD",
                    $"File-Name: {fileName}",
                    ""
                };
                var header = string.Join("\r\n", headerLines) + "\r\n";
                var headerBytes = Encoding.UTF8.GetBytes(header);
                await stream.WriteAsync(headerBytes);

                // Read the response header
                var headerBuffer = new byte[1024];
                var headerBytesRead = await stream.ReadAsync(headerBuffer, 0, headerBuffer.Length);
                var headerResponse = Encoding.UTF8.GetString(headerBuffer, 0, headerBytesRead).Trim();

                if (headerResponse.StartsWith("Status: ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("Server error: {Response}", headerResponse);
                    return;
                }
                if (!headerResponse.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("Unexpected header from server: {HeaderResponse}", headerResponse);
                    return;
                }

                long totalSize;
                try
                {
                    var sizeStr = headerResponse.Split(":", 2)[1].Trim();
                    totalSize = long.Parse(sizeStr);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Invalid Content-Length header: {HeaderResponse}. Exception: {Message}", headerResponse, ex.Message);
                    return;
                }

                _logger.LogInformation("Downloading '{FileName}' ({TotalSize} bytes) from the server.", fileName, totalSize);

                long receivedBytes = 0;
                await using FileStream fileStream = new FileStream(fileName, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[4096];

                while (receivedBytes < totalSize)
                {
                    var toRead = (int)Math.Min(buffer.Length, totalSize - receivedBytes);
                    var read = await stream.ReadAsync(buffer, 0, toRead);
                    if (read <= 0)
                    {
                        break;
                    }
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    receivedBytes += read;
                }

                if (receivedBytes == totalSize)
                {
                    _logger.LogInformation("File '{FileName}' downloaded successfully.", fileName);
                }
                else
                {
                    _logger.LogWarning("Incomplete download: received {ReceivedBytes} of {TotalSize} bytes.", receivedBytes, totalSize);
                }
            }
            catch (SocketException ex)
            {
                _logger.LogError("Socket error downloading file: {Message}", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error downloading file: {Message}", ex.Message);
            }
        }

        // LIST
        private static async Task ListFilesAsync(string serverHost, int serverPort)
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(serverHost, serverPort);
                if (await Task.WhenAny(connectTask, Task.Delay(60000)) != connectTask)
                {
                    _logger.LogError("Connection timed out.");
                    return;
                }

                await using var stream = client.GetStream();
                stream.ReadTimeout = 60000;
                stream.WriteTimeout = 60000;

                const string request = "LIST\r\n\r\n";
                var requestBytes = Encoding.UTF8.GetBytes(request);
                await stream.WriteAsync(requestBytes);

                var buffer = new byte[4096];
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                var response = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                if (!string.IsNullOrWhiteSpace(response))
                {
                    Console.WriteLine("Files on server:");
                    foreach (var line in response.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        Console.WriteLine(line);
                    }
                }
                else
                {
                    Console.WriteLine("No files found on server.");
                }
            }
            catch (SocketException ex)
            {
                _logger.LogError("Socket error listing files: {Message}", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error listing files: {Message}", ex.Message);
            }
        }
    }
}