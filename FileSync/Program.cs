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
        private readonly DropboxClient _client;

        public DropboxStorageProvider(string accessToken) {
            _client = new DropboxClient(accessToken);
        }

        public async Task<Dictionary<string, (DateTime, long)>> ListFilesAsync(string remotePath) {
            var files = new Dictionary<string, (DateTime, long)>();
            try {
                var list = await _client.Files.ListFolderAsync(remotePath);
                foreach (var entry in list.Entries.Where(e => e.IsFile)) {
                    var file = entry.AsFile;
                    files[file.Name] = (file.ServerModified.ToUniversalTime(), (long)file.Size);
                }
                return files;
            } catch (Exception ex) {
                Console.WriteLine($"Error listing files in {remotePath}: {ex.Message}");
                return files;
            }
        }

        public async Task UploadFileAsync(string localPath, string remotePath) {
            try {
                // Ensure remotePath uses forward slashes and is the full path
                var normalizedRemotePath = remotePath.Replace('\\', '/');
                using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read);
                await _client.Files.UploadAsync(normalizedRemotePath, body: stream);
                Console.WriteLine($"Uploaded {localPath} to {normalizedRemotePath}");
            } catch (Exception ex) {
                Console.WriteLine($"Error uploading {localPath} to {remotePath}: {ex.Message}");
            }
        }

        public async Task DownloadFileAsync(string remotePath, string localPath) {
            try {
                var normalizedRemotePath = remotePath.Replace('\\', '/');
                using var response = await _client.Files.DownloadAsync(normalizedRemotePath);
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
                    var remotePath = $"{pair.RemotePath}/{local.Key}";

                    if (!remoteFiles.ContainsKey(local.Key)) {
                        syncActions.Add(("Upload", localPath, remotePath, local.Value.Size));
                    } else if (local.Value.ModifiedTime > remoteFiles[local.Key].ModifiedTime) {
                        syncActions.Add(("Upload", localPath, remotePath, local.Value.Size));
                    }
                }

                foreach (var remote in remoteFiles) {
                    var localPath = Path.Combine(pair.LocalPath, remote.Key);
                    var remotePath = $"{pair.RemotePath}/{remote.Key}";

                    if (!localFiles.ContainsKey(remote.Key)) {
                        syncActions.Add(("Download", localPath, remotePath, remote.Value.Size));
                    } else if (remote.Value.ModifiedTime > localFiles[remote.Key].ModifiedTime) {
                        syncActions.Add(("Download", localPath, remotePath, remote.Value.Size));
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
                    await _provider.UploadFileAsync(action.LocalPath, action.RemotePath); // Use full remote path
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

    class Program {
        static async Task Main(string[] args) {
            try {
                var configText = File.ReadAllText("config.json");
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var config = JsonSerializer.Deserialize<Config>(configText, options);

                ICloudStorageProvider provider = config.CloudProvider.ToLower() switch {
                    "dropbox" => new DropboxStorageProvider(config.Credentials),
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