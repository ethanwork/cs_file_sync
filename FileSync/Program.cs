using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Dropbox.Api;
using Dropbox.Api.Files;

namespace GameSaveSync {
    public class Config {
        public List<SyncPair> SyncPairs { get; set; } = new List<SyncPair>();
        public string CloudProvider { get; set; } = string.Empty;
        public string Credentials { get; set; } = string.Empty; // Short-lived access token
        public string RefreshToken { get; set; } = string.Empty; // Long-lived refresh token
        public string AppKey { get; set; } = string.Empty; // Add these for refresh
        public string AppSecret { get; set; } = string.Empty;
    }

    public class SyncPair {
        public string LocalPath { get; set; } = string.Empty;
        public string RemotePath { get; set; } = string.Empty;
    }

    public interface ICloudStorageProvider {
        Task<Dictionary<string, (DateTime ModifiedTime, long Size)>> ListFilesAsync(string remotePath);
        Task UploadFileAsync(string localPath, string remotePath);
        Task DownloadFileAsync(string remotePath, string localPath);
        Task CreateFolderAsync(string remotePath);
    }

    public class DropboxStorageProvider : ICloudStorageProvider {
        private DropboxClient _client;
        private readonly string _appKey;
        private readonly string _appSecret;
        private string _accessToken;
        private readonly string _refreshToken;
        private DateTime _tokenExpiration;

        public DropboxStorageProvider(string accessToken, string refreshToken, string appKey, string appSecret) {
            _accessToken = accessToken;
            _refreshToken = refreshToken;
            _appKey = appKey;
            _appSecret = appSecret;
            _tokenExpiration = DateTime.UtcNow.AddHours(4); // Assume 4-hour lifespan
            _client = new DropboxClient(_accessToken);
        }

        private async Task RefreshAccessTokenAsync() {
            if (DateTime.UtcNow >= _tokenExpiration) {
                using var httpClient = new HttpClient();
                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.dropbox.com/oauth2/token");
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("grant_type", "refresh_token"),
                    new KeyValuePair<string, string>("refresh_token", _refreshToken),
                    new KeyValuePair<string, string>("client_id", _appKey),
                    new KeyValuePair<string, string>("client_secret", _appSecret)
                });
                var response = await httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                var tokenData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                _accessToken = tokenData["access_token"].GetString();
                _tokenExpiration = DateTime.UtcNow.AddSeconds(tokenData["expires_in"].GetInt32());
                _client = new DropboxClient(_accessToken);
                Console.WriteLine($"Refreshed access token, expires at {_tokenExpiration}");
            }
        }

        public async Task<Dictionary<string, (DateTime ModifiedTime, long Size)>> ListFilesAsync(string remotePath) {
            await RefreshAccessTokenAsync();
            var files = new Dictionary<string, (DateTime, long)>();
            try {
                var list = await _client.Files.ListFolderAsync(remotePath);
                foreach (var entry in list.Entries.Where(e => e.IsFile)) {
                    var file = entry.AsFile;
                    var timestamp = ParseTimestampFromFilename(file.Name) ?? file.ServerModified.ToUniversalTime();
                    files[file.Name] = (timestamp, (long)file.Size);
                }
                return files;
            } catch (Exception ex) {
                Console.WriteLine($"Error listing files in {remotePath}: {ex.Message}");
                return files;
            }
        }

        public async Task UploadFileAsync(string localPath, string remotePath) {
            await RefreshAccessTokenAsync();
            try {
                var localModified = File.GetLastWriteTimeUtc(localPath);
                var filename = Path.GetFileName(localPath);
                var timestampedFilename = $"{localModified:yyyyMMdd_HHmmss}_{filename}";
                var normalizedRemotePath = $"{remotePath.Replace('\\', '/')}/{timestampedFilename}";
                using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read);
                await _client.Files.UploadAsync(normalizedRemotePath, body: stream);
                Console.WriteLine($"Uploaded {localPath} to {normalizedRemotePath}");
            } catch (Exception ex) {
                Console.WriteLine($"Error uploading {localPath} to {remotePath}: {ex.Message}");
            }
        }

        public async Task DownloadFileAsync(string remotePath, string localPath) {
            await RefreshAccessTokenAsync();
            try {
                var normalizedRemotePath = remotePath.Replace('\\', '/');
                using var response = await _client.Files.DownloadAsync(normalizedRemotePath);
                var cloudFilename = Path.GetFileName(normalizedRemotePath);
                var timestamp = ParseTimestampFromFilename(cloudFilename) ?? response.Response.ServerModified.ToUniversalTime();
                var originalFilename = StripTimestampFromFilename(cloudFilename);
                var finalLocalPath = Path.Combine(Path.GetDirectoryName(localPath), originalFilename);

                using var stream = await response.GetContentAsStreamAsync();
                using var fileStream = new FileStream(finalLocalPath, FileMode.Create, FileAccess.Write);
                await stream.CopyToAsync(fileStream);
                fileStream.Close();
                File.SetLastWriteTimeUtc(finalLocalPath, timestamp);
                Console.WriteLine($"Downloaded {normalizedRemotePath} to {finalLocalPath} (set timestamp: {timestamp})");
            } catch (Exception ex) {
                Console.WriteLine($"Error downloading {remotePath} to {localPath}: {ex.Message}");
            }
        }

        public async Task CreateFolderAsync(string remotePath) {
            await RefreshAccessTokenAsync();
            try {
                var normalizedRemotePath = remotePath.Replace('\\', '/');
                await _client.Files.CreateFolderV2Async(normalizedRemotePath);
                Console.WriteLine($"Created folder: {normalizedRemotePath}");
            } catch (Exception ex) {
                if (!ex.Message.Contains("path/conflict/folder"))
                    Console.WriteLine($"Error creating folder {remotePath}: {ex.Message}");
            }
        }

        private static DateTime? ParseTimestampFromFilename(string filename) {
            if (filename.Length > 15 && filename[8] == '_' && filename[15] == '_') {
                var timestampPart = filename.Substring(0, 15); // yyyyMMdd_HHmmss
                if (DateTime.TryParseExact(timestampPart, "yyyyMMdd_HHmmss", null, System.Globalization.DateTimeStyles.AssumeUniversal, out var timestamp))
                    return timestamp;
            }
            return null;
        }

        private static string StripTimestampFromFilename(string filename) {
            if (filename.Length > 15 && filename[8] == '_' && filename[15] == '_')
                return filename.Substring(16);
            return filename;
        }
    }

    public class SyncManager {
        private readonly ICloudStorageProvider _provider;
        private int _totalFilesToSync;
        private long _totalBytesToSync;
        private int _filesSynced;
        private long _bytesSynced;

        public SyncManager(ICloudStorageProvider provider) {
            _provider = provider;
            _totalFilesToSync = 0;
            _totalBytesToSync = 0;
            _filesSynced = 0;
            _bytesSynced = 0;
        }

        public static string StripTimestampFromFilename(string filename) {
            if (filename.Length > 15 && filename[8] == '_' && filename[15] == '_')
                return filename.Substring(16); // Remove yyyyMMdd_HHmmss_
            return filename;
        }

        public async Task AnalyzeAndSyncAsync(List<SyncPair> pairs) {
            var syncActions = new List<(string Action, string LocalPath, string RemotePath, long Size)>();
            foreach (var pair in pairs) {
                Directory.CreateDirectory(pair.LocalPath);
                await _provider.CreateFolderAsync(pair.RemotePath);

                var localFiles = Directory.GetFiles(pair.LocalPath)
                    .ToDictionary(
                        Path.GetFileName,
                        f => (ModifiedTime: File.GetLastWriteTimeUtc(f), Size: new FileInfo(f).Length),
                        StringComparer.OrdinalIgnoreCase);

                var remoteFiles = await _provider.ListFilesAsync(pair.RemotePath);

                foreach (var local in localFiles) {
                    var localPath = Path.Combine(pair.LocalPath, local.Key);
                    var localTime = local.Value.ModifiedTime.TruncateToSeconds();
                    var remoteFilename = $"{localTime:yyyyMMdd_HHmmss}_{local.Key}";
                    var remotePath = $"{pair.RemotePath}/{remoteFilename}";

                    if (!remoteFiles.ContainsKey(remoteFilename)) {
                        syncActions.Add(("Upload", localPath, remotePath, local.Value.Size));
                        Console.WriteLine($"Will upload {local.Key} (missing in cloud)");
                    } else {
                        var remoteTime = remoteFiles[remoteFilename].ModifiedTime.TruncateToSeconds();
                        if (localTime > remoteTime) {
                            syncActions.Add(("Upload", localPath, remotePath, local.Value.Size));
                            Console.WriteLine($"Will upload {local.Key} (local newer: {localTime} vs cloud {remoteTime})");
                        } else if (remoteTime > localTime) {
                            syncActions.Add(("Download", localPath, remotePath, remoteFiles[remoteFilename].Size));
                            Console.WriteLine($"Will download {local.Key} (cloud newer: {remoteTime} vs local {localTime})");
                        } else {
                            Console.WriteLine($"Skipping {local.Key} (timestamps match: {localTime})");
                        }
                    }
                }

                foreach (var remote in remoteFiles) {
                    var originalFilename = StripTimestampFromFilename(remote.Key);
                    var localPath = Path.Combine(pair.LocalPath, originalFilename);
                    var remoteTime = remote.Value.ModifiedTime.TruncateToSeconds();

                    if (!localFiles.ContainsKey(originalFilename)) {
                        syncActions.Add(("Download", localPath, $"{pair.RemotePath}/{remote.Key}", remote.Value.Size));
                        Console.WriteLine($"Will download {originalFilename} (missing locally)");
                    }
                }
            }

            _totalFilesToSync = syncActions.Count;
            _totalBytesToSync = syncActions.Sum(a => a.Size);
            double totalMB = _totalBytesToSync / (1024.0 * 1024.0);

            Console.WriteLine($"Sync Analysis Complete:");
            Console.WriteLine($"Total Files to Sync: {_totalFilesToSync}");
            Console.WriteLine($"Total Size to Sync: {totalMB:F2} MB");
            Console.WriteLine();

            foreach (var action in syncActions) {
                if (action.Action == "Upload")
                    await _provider.UploadFileAsync(action.LocalPath, action.RemotePath);
                else if (action.Action == "Download")
                    await _provider.DownloadFileAsync(action.RemotePath, action.LocalPath);

                _filesSynced++;
                _bytesSynced += action.Size;

                double filePercent = (_filesSynced / (double)_totalFilesToSync) * 100;
                double bytesMB = _bytesSynced / (1024.0 * 1024.0);
                double bytesPercent = (_bytesSynced / (double)_totalBytesToSync) * 100;

                Console.WriteLine($"Progress: {_filesSynced}/{_totalFilesToSync} files synced ({filePercent:F1}%)");
                Console.WriteLine($"Data: {bytesMB:F2}/{totalMB:F2} MB synced ({bytesPercent:F1}%)");
                Console.WriteLine();
            }
        }
    }

    public static class DateTimeExtensions {
        public static DateTime TruncateToSeconds(this DateTime dt) {
            return new DateTime(dt.Ticks - (dt.Ticks % TimeSpan.TicksPerSecond), dt.Kind);
        }
    }

    class Program {
        static async Task Main(string[] args) {
            try {
                var configText = File.ReadAllText("config.json");
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var config = JsonSerializer.Deserialize<Config>(configText, options);

                ICloudStorageProvider provider = config.CloudProvider.ToLower() switch {
                    "dropbox" => new DropboxStorageProvider(config.Credentials, config.RefreshToken, config.AppKey, config.AppSecret),
                    _ => throw new NotSupportedException($"Cloud provider '{config.CloudProvider}' is not supported.")
                };

                var syncManager = new SyncManager(provider);
                await syncManager.AnalyzeAndSyncAsync(config.SyncPairs);

                Console.WriteLine("Sync completed successfully.");
            } catch (Exception ex) {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }
}