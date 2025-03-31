using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dropbox.Api;
using Dropbox.Api.Files;

namespace GameSaveSync {
    public class Config {
        public List<SyncPair> SyncPairs { get; set; } = new List<SyncPair>();
        public string CloudProvider { get; set; } = string.Empty;
        public string Credentials { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public string AppKey { get; set; } = string.Empty;
        public string AppSecret { get; set; } = string.Empty;
    }

    public class SyncPair {
        public string LocalPath { get; set; } = string.Empty;
        public string RemotePath { get; set; } = string.Empty;
    }

    public interface ICloudStorageProvider {
        Task UploadFileAsync(string localPath, string remotePath);
        Task DownloadFileAsync(string remotePath, string localPath);
        Task CreateFolderAsync(string remotePath);
        Task DeleteFileAsync(string remotePath);
        Task<List<string>> ListFoldersAsync(string remotePath);
        Task<string> DownloadTextFileAsync(string remotePath);
        Task UploadTextFileAsync(string content, string remotePath);
    }

    public class DropboxStorageProvider : ICloudStorageProvider {
        private readonly DropboxClient _client;

        public DropboxStorageProvider(string accessToken, string refreshToken, string appKey, string appSecret) {
            if (!string.IsNullOrEmpty(refreshToken) && !string.IsNullOrEmpty(appKey) && !string.IsNullOrEmpty(appSecret)) {
                _client = new DropboxClient(refreshToken, appKey, appSecret);
            } else if (!string.IsNullOrEmpty(accessToken)) {
                _client = new DropboxClient(accessToken);
            } else {
                throw new ArgumentException("Either a valid access token or a refresh token with app key and secret must be provided.");
            }
        }

        public async Task UploadFileAsync(string localPath, string remotePath) {
            try {
                var normalizedRemotePath = remotePath.Replace('\\', '/');
                using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read);
                await _client.Files.UploadAsync(normalizedRemotePath, body: stream, mute: true);
                Console.WriteLine($"Uploaded {localPath} to {normalizedRemotePath}");
            } catch (Exception ex) {
                Console.WriteLine($"Error uploading {localPath} to {remotePath}: {ex.Message}");
            }
        }

        public async Task DownloadFileAsync(string remotePath, string localPath) {
            try {
                var normalizedRemotePath = remotePath.Replace('\\', '/');
                using var response = await _client.Files.DownloadAsync(normalizedRemotePath);
                Directory.CreateDirectory(Path.GetDirectoryName(localPath));
                using var stream = await response.GetContentAsStreamAsync();
                using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write);
                await stream.CopyToAsync(fileStream);
                Console.WriteLine($"Downloaded {normalizedRemotePath} to {localPath}");
            } catch (Exception ex) {
                Console.WriteLine($"Error downloading {remotePath} to {localPath}: {ex.Message}");
            }
        }

        public async Task CreateFolderAsync(string remotePath) {
            try {
                var normalizedRemotePath = remotePath.Replace('\\', '/');
                await _client.Files.CreateFolderV2Async(normalizedRemotePath);
                Console.WriteLine($"Created folder: {normalizedRemotePath}");
            } catch (Exception ex) {
                if (!ex.Message.Contains("path/conflict/folder"))
                    Console.WriteLine($"Error creating folder {remotePath}: {ex.Message}");
            }
        }

        public async Task DeleteFileAsync(string remotePath) {
            try {
                var normalizedRemotePath = remotePath.Replace('\\', '/');
                await _client.Files.DeleteV2Async(normalizedRemotePath);
                Console.WriteLine($"Deleted {normalizedRemotePath} from cloud");
            } catch (Exception ex) {
                Console.WriteLine($"Error deleting {remotePath}: {ex.Message}");
            }
        }

        public async Task<List<string>> ListFoldersAsync(string remotePath) {
            var folders = new List<string>();
            try {
                var list = await _client.Files.ListFolderAsync(remotePath, recursive: false);
                foreach (var entry in list.Entries.Where(e => e.IsFolder)) {
                    folders.Add(entry.Name);
                }
                while (list.HasMore) {
                    list = await _client.Files.ListFolderContinueAsync(list.Cursor);
                    foreach (var entry in list.Entries.Where(e => e.IsFolder)) {
                        folders.Add(entry.Name);
                    }
                }
            } catch (Exception ex) {
                Console.WriteLine($"Error listing folders in {remotePath}: {ex.Message}");
            }
            return folders;
        }

        public async Task<string> DownloadTextFileAsync(string remotePath) {
            try {
                using var response = await _client.Files.DownloadAsync(remotePath);
                return await response.GetContentAsStringAsync();
            } catch (ApiException<DownloadError> ex) when (ex.ErrorResponse.IsPath && ex.ErrorResponse.AsPath.Value.IsNotFound) {
                return null;
            } catch (Exception ex) {
                Console.WriteLine($"Error downloading text file {remotePath}: {ex.Message}");
                throw;
            }
        }

        public async Task UploadTextFileAsync(string content, string remotePath) {
            try {
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
                await _client.Files.UploadAsync(remotePath, body: stream);
                Console.WriteLine($"Uploaded text file to {remotePath}");
            } catch (Exception ex) {
                Console.WriteLine($"Error uploading text file to {remotePath}: {ex.Message}");
            }
        }
    }

    public class SyncManager {
        private readonly ICloudStorageProvider _provider;

        public SyncManager(ICloudStorageProvider provider) {
            _provider = provider;
        }

        public async Task AnalyzeAndSyncAsync(List<SyncPair> pairs) {
            foreach (var pair in pairs) {
                Directory.CreateDirectory(pair.LocalPath);
                await SyncDirectoryAsync(pair.LocalPath, pair.RemotePath);
            }
        }

        private async Task SyncDirectoryAsync(string localDir, string remoteDir) {
            // Ensure remote directory exists
            await _provider.CreateFolderAsync(remoteDir);

            // Download and parse metadata
            var cloudFiles = await DownloadAndParseMetadataAsync(remoteDir);

            // Get local files
            var localFiles = Directory.GetFiles(localDir).ToDictionary(
                Path.GetFileName,
                f => File.GetLastWriteTimeUtc(f)
            );

            // Get local subdirectories
            var localSubDirs = Directory.GetDirectories(localDir).Select(Path.GetFileName).ToList();

            // List cloud subdirectories
            var cloudSubDirs = await _provider.ListFoldersAsync(remoteDir);

            // Sync files
            var finalTimestamps = new Dictionary<string, DateTime>();
            var localOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);

            // Handle local files
            foreach (var file in localFiles.Keys) {
                var localUtcTime = localFiles[file];
                var localTime = localUtcTime + localOffset; // Convert UTC to local time for comparison

                if (cloudFiles.TryGetValue(file, out var cloudUtcTime)) {
                    var cloudLocalTime = cloudUtcTime + localOffset; // Convert cloud UTC to local time

                    if (localTime > cloudLocalTime) {
                        // Local file is newer, upload it
                        await _provider.UploadFileAsync(Path.Combine(localDir, file), $"{remoteDir}/{file}");
                        finalTimestamps[file] = localUtcTime; // Store UTC time in metadata
                        Console.WriteLine($"Uploaded {file}: local {localTime} > cloud {cloudLocalTime}");
                    } else if (cloudLocalTime > localTime) {
                        // Cloud file is newer, download it
                        await _provider.DownloadFileAsync($"{remoteDir}/{file}", Path.Combine(localDir, file));
                        File.SetLastWriteTimeUtc(Path.Combine(localDir, file), cloudUtcTime);
                        finalTimestamps[file] = cloudUtcTime;
                        Console.WriteLine($"Downloaded {file}: cloud {cloudLocalTime} > local {localTime}");
                    } else {
                        // Timestamps equal, no action needed
                        finalTimestamps[file] = localUtcTime; // Could use cloudUtcTime, as they're equivalent in UTC
                        Console.WriteLine($"Skipped {file}: local {localTime} = cloud {cloudLocalTime}");
                    }
                } else {
                    // File not in cloud, upload it
                    await _provider.UploadFileAsync(Path.Combine(localDir, file), $"{remoteDir}/{file}");
                    finalTimestamps[file] = localUtcTime;
                    Console.WriteLine($"Uploaded {file}: not found in cloud");
                }
            }

            // Handle cloud files not present locally
            foreach (var file in cloudFiles.Keys.Except(localFiles.Keys)) {
                var localPath = Path.Combine(localDir, file);
                await _provider.DownloadFileAsync($"{remoteDir}/{file}", localPath);
                File.SetLastWriteTimeUtc(localPath, cloudFiles[file]);
                finalTimestamps[file] = cloudFiles[file];
                Console.WriteLine($"Downloaded {file}: not found locally");
            }

            // Upload updated metadata
            await UploadMetadataAsync(remoteDir, finalTimestamps);

            // Sync subdirectories
            foreach (var subDir in localSubDirs) {
                var localSubDirPath = Path.Combine(localDir, subDir);
                var remoteSubDirPath = $"{remoteDir}/{subDir}";
                await SyncDirectoryAsync(localSubDirPath, remoteSubDirPath);
            }

            // Handle cloud subdirectories not present locally
            foreach (var subDir in cloudSubDirs.Except(localSubDirs)) {
                var localSubDirPath = Path.Combine(localDir, subDir);
                Directory.CreateDirectory(localSubDirPath);
                var remoteSubDirPath = $"{remoteDir}/{subDir}";
                await SyncDirectoryAsync(localSubDirPath, remoteSubDirPath);
            }
        }

        private async Task<Dictionary<string, DateTime>> DownloadAndParseMetadataAsync(string remoteDir) {
            var metadataPath = $"{remoteDir}/file_sync_metadata.txt";
            var content = await _provider.DownloadTextFileAsync(metadataPath);
            if (string.IsNullOrEmpty(content))
                return new Dictionary<string, DateTime>();

            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var dict = new Dictionary<string, DateTime>();
            foreach (var line in lines) {
                var parts = line.Split('\t');
                if (parts.Length == 2 && DateTime.TryParse(parts[1], out var dt)) {
                    dict[parts[0]] = dt; // UTC time from metadata
                }
            }
            return dict;
        }

        private async Task UploadMetadataAsync(string remoteDir, Dictionary<string, DateTime> timestamps) {
            var content = string.Join("\n", timestamps.Select(kv => $"{kv.Key}\t{kv.Value.ToString("o")}"));
            var metadataPath = $"{remoteDir}/file_sync_metadata.txt";
            await _provider.UploadTextFileAsync(content, metadataPath);
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