using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FolderSyncEr.Client.Api;
using FolderSyncEr.Client.App.ViewModels;
using FolderSyncEr.Client.Platform;
using FolderSyncEr.Client.Sync.Conflicts;
using FolderSyncEr.Contracts;
using Xunit;

namespace FolderSyncEr.Client.Tests;

public class Phase4ViewModelTests
{
    private class TestSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _dict = new();
        public SecretStoreType StoreType => SecretStoreType.EncryptedFileFallback;
        public string Description => "Test Secret Store";
        public bool IsInsecureFallback => false;

        public Task<string?> GetSecretAsync(string key, CancellationToken ct = default)
        {
            _dict.TryGetValue(key, out var val);
            return Task.FromResult<string?>(val);
        }

        public Task SetSecretAsync(string key, string secret, CancellationToken ct = default)
        {
            _dict[key] = secret;
            return Task.CompletedTask;
        }

        public Task DeleteSecretAsync(string key, CancellationToken ct = default)
        {
            _dict.Remove(key);
            return Task.CompletedTask;
        }
    }

    private class FakeFileSyncApi : IFileSyncApi
    {
        public string ServerUrl { get; set; } = "http://localhost:5000";
        public List<FileDto> MockFiles { get; } = new();
        public List<ActivityLogDto> MockLogs { get; } = new();
        public byte[] MockZipBytes { get; set; } = new byte[] { 0x50, 0x4B, 0x03, 0x04 };

        public Task<ApiConnectionStatus> CheckConnectionAsync(CancellationToken ct = default) =>
            Task.FromResult(new ApiConnectionStatus(true, "Healthy", "1.0", DateTime.UtcNow, 5, null));

        public Task<LoginResponse> LoginAsync(string username, string password, string deviceName, CancellationToken ct = default) =>
            Task.FromResult(new LoginResponse("access", DateTime.UtcNow.AddHours(1), "refresh", DateTime.UtcNow.AddDays(7),
                new UserDto(1, username, "User", DateTime.UtcNow, null)));

        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<UserDto?> GetMeAsync(CancellationToken ct = default) => Task.FromResult<UserDto?>(null);

        public Task<FileListResponse> ListFilesAsync(long? sinceSeq = null, DateTime? since = null, bool includeDeleted = false, string? cursor = null, int limit = 1000, CancellationToken ct = default)
        {
            var filtered = MockFiles.Where(f => includeDeleted || !f.IsDeleted).ToList();
            return Task.FromResult(new FileListResponse(filtered, null, filtered.Count, DateTime.UtcNow));
        }

        public Task<FileDto?> GetFileMetaByPathAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(MockFiles.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)));

        public Task<FileDto?> GetFileMetaByIdAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(MockFiles.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));

        public Task<Stream> DownloadFileStreamAsync(string id, (long from, long? to)? range = null, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(new byte[] { 1, 2, 3 }));

        public Task<UploadResult> UploadFileStreamAsync(string path, Stream contentStream, string? sha256Hex = null, string? ifMatchSha256 = null, string? ifNoneMatch = null, long? baseVersion = null, DateTime? lastModifiedUtc = null, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<FileDto> CreateDirectoryAsync(string path, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<bool> DeleteFileAsync(string id, string? ifMatchSha256 = null, long? baseVersion = null, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<FileDto?> MoveFileAsync(string fromPath, string toPath, string? ifMatchSha256 = null, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<StorageInfoDto> GetStorageInfoAsync(CancellationToken ct = default) =>
            Task.FromResult(new StorageInfoDto(1000, 200, 5000));

        public Task<FileDto> PatchFileLastModifiedAsync(string id, DateTime lastModifiedUtc, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<PagedResult<ActivityLogDto>> GetLogsAsync(
            int page = 1,
            int pageSize = 50,
            string? action = null,
            string? result = null,
            string? path = null,
            DateTime? fromUtc = null,
            DateTime? toUtc = null,
            CancellationToken ct = default)
        {
            var query = MockLogs.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(action) && !action.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(l => l.Action.Equals(action, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(result) && !result.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(l => l.Result.Equals(result, StringComparison.OrdinalIgnoreCase));
            }

            var items = query.ToList();
            return Task.FromResult(new PagedResult<ActivityLogDto>(items, page, pageSize, items.Count));
        }

        public Task<Stream> ExportFolderZipAsync(CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(MockZipBytes));
    }

    [Fact]
    public async Task FileListViewModel_MergesLocalAndRemote_AndComputesStatusCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "FolderSyncEr_UI_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var syncTime = DateTime.UtcNow.AddMinutes(-5);

            // 1. In-sync file
            var file1 = Path.Combine(tempDir, "synced.txt");
            await File.WriteAllTextAsync(file1, "Hello World");
            File.SetLastWriteTimeUtc(file1, syncTime);
            var file1Len = new FileInfo(file1).Length;

            // 2. Local only file
            var file2 = Path.Combine(tempDir, "local_only.txt");
            await File.WriteAllTextAsync(file2, "Local only content");

            var api = new FakeFileSyncApi();
            // Add file1 to remote matching size & mtime
            api.MockFiles.Add(new FileDto("id1", "synced.txt", false, file1Len, "sha1", 1, syncTime, DateTime.UtcNow, "dev1", false, 1));
            // Add remote-only file
            api.MockFiles.Add(new FileDto("id3", "remote_only.txt", false, 1234, "sha3", 1, DateTime.UtcNow, DateTime.UtcNow, "dev2", false, 2));

            var vm = new FileListViewModel(api, () => tempDir);
            await vm.RefreshAsync();

            Assert.Equal(3, vm.Files.Count);

            var syncedItem = vm.Files.First(f => f.Path == "synced.txt");
            Assert.Equal("In sync", syncedItem.Status);

            var localOnlyItem = vm.Files.First(f => f.Path == "local_only.txt");
            Assert.Equal("Local newer", localOnlyItem.Status);

            var remoteOnlyItem = vm.Files.First(f => f.Path == "remote_only.txt");
            Assert.Equal("Server newer", remoteOnlyItem.Status);

            // Test filtering
            vm.SelectedStatusFilter = "In sync";
            Assert.Single(vm.Files);
            Assert.Equal("synced.txt", vm.Files[0].Path);

            vm.SelectedStatusFilter = "All";
            vm.SearchQuery = "remote";
            Assert.Single(vm.Files);
            Assert.Equal("remote_only.txt", vm.Files[0].Path);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LogsViewModel_LoadsAndFiltersLogs()
    {
        var api = new FakeFileSyncApi();
        api.MockLogs.Add(new ActivityLogDto(1, DateTime.UtcNow, 1, "dev1", "Upload", "file1.txt", "Success", "Uploaded v1", "corr1"));
        api.MockLogs.Add(new ActivityLogDto(2, DateTime.UtcNow, 1, "dev1", "Upload", "file2.txt", "Conflict", "409 Conflict", "corr2"));
        api.MockLogs.Add(new ActivityLogDto(3, DateTime.UtcNow, 1, "dev1", "Error", "file3.txt", "Failure", "Disk error", "corr3"));

        using var vm = new LogsViewModel(api);
        vm.AutoRefreshEnabled = false; // Disable background timer for deterministic test

        await vm.RefreshAsync();
        Assert.Equal(3, vm.Logs.Count);

        vm.SelectedActionFilter = "Error";
        await vm.RefreshAsync();
        Assert.Single(vm.Logs);
        Assert.Equal("Error", vm.Logs[0].Action);

        vm.SelectedActionFilter = "All";
        vm.SelectedResultFilter = "Conflict";
        await vm.RefreshAsync();
        Assert.Single(vm.Logs);
        Assert.Equal("Conflict", vm.Logs[0].Result);
    }

    [Fact]
    public async Task ConflictPromptViewModel_ResolvesUserSelection()
    {
        var vm = new ConflictPromptViewModel();

        var promptInfo = new ConflictPromptInfo(
            Path: "notes/work.txt",
            LocalSize: 500,
            LocalModifiedUtc: DateTime.UtcNow.AddMinutes(-10),
            LocalHash: "abc12345",
            RemoteSize: 600,
            RemoteModifiedUtc: DateTime.UtcNow,
            RemoteHash: "def67890",
            DeviceName: "Laptop-Ubuntu"
        );

        using var cts = new CancellationTokenSource();
        var resolutionTask = vm.RequestResolutionAsync(promptInfo, cts.Token);

        Assert.True(vm.IsVisible);
        Assert.Equal("notes/work.txt", vm.FilePath);
        Assert.Contains("Laptop-Ubuntu", vm.DeviceInfoText);

        // User clicks "Keep Server"
        vm.KeepServerCommand.Execute(null);

        var result = await resolutionTask;
        Assert.Equal(ConflictResolutionChoice.KeepServer, result);
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public void SettingsViewModel_FolderChangeConfirmationFlow()
    {
        var api = new FakeFileSyncApi();
        var secretStore = new TestSecretStore();
        string currentFolder = "/home/user/FolderA";
        string? updatedFolder = null;

        var vm = new SettingsViewModel(
            api,
            secretStore,
            getSyncFolder: () => currentFolder,
            setSyncFolder: folder => updatedFolder = folder,
            setConflictPolicy: policy => { }
        );

        var targetNewFolder = Path.Combine(Path.GetTempPath(), "NewSyncFolder_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(targetNewFolder);

        try
        {
            vm.NewSyncFolderInput = targetNewFolder;
            vm.RequestChangeFolder();

            Assert.True(vm.ShowFolderChangeConfirmation);
            Assert.Null(updatedFolder);

            // Confirm
            vm.ConfirmChangeFolder();
            Assert.False(vm.ShowFolderChangeConfirmation);
            Assert.Equal(targetNewFolder, updatedFolder);
            Assert.Equal(targetNewFolder, vm.CurrentSyncFolder);
        }
        finally
        {
            Directory.Delete(targetNewFolder, recursive: true);
        }
    }

    [Fact]
    public async Task SettingsViewModel_DownloadAdminArchive_Success()
    {
        var api = new FakeFileSyncApi();
        var secretStore = new TestSecretStore();
        var vm = new SettingsViewModel(api, secretStore, () => "/dummy", _ => { }, _ => { });

        await vm.DownloadArchiveAsync(Path.GetTempPath());
        Assert.False(vm.IsDownloadingArchive);
        Assert.Contains("Server archive saved to", vm.StatusMessage);
    }
}
