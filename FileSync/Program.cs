using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dropbox.Api;
using Dropbox.Api.Files;

namespace GameSaveSync {
    // Configuration structure for JSON deserialization
    public class Config {
        public List<SyncPair> SyncPairs { get; set; } = new List<SyncPair>();
        public string CloudProvider { get; set; } = string.Empty;
        public string Credentials { get; set; } = string.Empty; // Path to credentials or token
    }

    public class SyncPair {
        public string LocalPath { get; set; } = string.Empty;
        public string RemotePath { get; set; } = string.Empty;
    }

    // Interface for cloud storage providers
    public interface ICloudStorageProvider {
        Task<Dictionary<string, DateTime>> ListFilesAsync(string remotePath);
        Task UploadFileAsync(string localPath, string remotePath);
        Task DownloadFileAsync(string remotePath, string localPath);
    }

    // Dropbox-specific implementation
    public class DropboxStorageProvider : ICloudStorageProvider {
        private readonly DropboxClient _client;

        public DropboxStorageProvider(string accessToken) {
            _client = new DropboxClient(accessToken);
        }

        public async Task<Dictionary<string, DateTime>> ListFilesAsync(string remotePath) {
            var files = new Dictionary<string, DateTime>();
            try {
                var list = await _client.Files.ListFolderAsync(remotePath);
                foreach (var entry in list.Entries.Where(e => e.IsFile)) {
                    var file = entry.AsFile;
                    files[file.Name] = file.ServerModified.ToUniversalTime();
                }
                return files;
            } catch (Exception ex) {
                Console.WriteLine($"Error listing files in {remotePath}: {ex.Message}");
                return files;
            }
        }

        public async Task UploadFileAsync(string localPath, string remotePath) {
            try {
                using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read);
                var remoteFilePath = $"{remotePath}/{Path.GetFileName(localPath)}";
                await _client.Files.UploadAsync(remoteFilePath, body: stream);
                Console.WriteLine($"Uploaded {localPath} to {remoteFilePath}");
            } catch (Exception ex) {
                Console.WriteLine($"Error uploading {localPath}: {ex.Message}");
            }
        }

        public async Task DownloadFileAsync(string remotePath, string localPath) {
            try {
                using var response = await _client.Files.DownloadAsync(remotePath);
                using var stream = await response.GetContentAsStreamAsync();
                using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write);
                await stream.CopyToAsync(fileStream);
                Console.WriteLine($"Downloaded {remotePath} to {localPath}");
            } catch (Exception ex) {
                Console.WriteLine($"Error downloading {remotePath}: {ex.Message}");
            }
        }
    }

    // Core syncing logic, agnostic to the cloud provider
    public class SyncManager {
        private readonly ICloudStorageProvider _provider;

        public SyncManager(ICloudStorageProvider provider) {
            _provider = provider;
        }

        public async Task SyncAsync(SyncPair pair) {
            // Ensure local directory exists
            Directory.CreateDirectory(pair.LocalPath);

            // Get local files
            var localFiles = Directory.GetFiles(pair.LocalPath)
                .ToDictionary(
                    Path.GetFileName,
                    f => File.GetLastWriteTimeUtc(f),
                    StringComparer.OrdinalIgnoreCase);

            // Get remote files
            var remoteFiles = await _provider.ListFilesAsync(pair.RemotePath);

            // Sync from local to remote
            foreach (var localFile in localFiles) {
                var localPath = Path.Combine(pair.LocalPath, localFile.Key);
                var remoteModified = remoteFiles.ContainsKey(localFile.Key)
                    ? remoteFiles[localFile.Key]
                    : DateTime.MinValue;

                if (!remoteFiles.ContainsKey(localFile.Key) || localFile.Value > remoteModified) {
                    await _provider.UploadFileAsync(localPath, pair.RemotePath);
                }
            }

            // Sync from remote to local
            foreach (var remoteFile in remoteFiles) {
                var localPath = Path.Combine(pair.LocalPath, remoteFile.Key);
                var localModified = localFiles.ContainsKey(remoteFile.Key)
                    ? localFiles[remoteFile.Key]
                    : DateTime.MinValue;

                if (!localFiles.ContainsKey(remoteFile.Key) || remoteFile.Value > localModified) {
                    await _provider.DownloadFileAsync($"{pair.RemotePath}/{remoteFile.Key}", localPath);
                }
            }
        }
    }

    // Program entry point
    class Program {
        static async Task Main(string[] args) {
            try {
                // Load configuration
                var configText = File.ReadAllText("config.json");
                var options = new JsonSerializerOptions {
                    PropertyNameCaseInsensitive = true
                };
                var config = JsonSerializer.Deserialize<Config>(configText, options);

                // Initialize cloud provider
                ICloudStorageProvider provider = config.CloudProvider.ToLower() switch {
                    "dropbox" => new DropboxStorageProvider(config.Credentials),
                    _ => throw new NotSupportedException($"Cloud provider '{config.CloudProvider}' is not supported.")
                };

                // Create sync manager and run
                var syncManager = new SyncManager(provider);
                foreach (var pair in config.SyncPairs) {
                    Console.WriteLine($"Syncing {pair.LocalPath} with {pair.RemotePath}");
                    await syncManager.SyncAsync(pair);
                }

                Console.WriteLine("Sync completed successfully.");
            } catch (Exception ex) {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }
}