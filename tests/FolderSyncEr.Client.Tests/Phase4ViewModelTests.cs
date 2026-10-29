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
        public List<DeviceDto> MockDevices { get; } = new();
        public byte[] MockZipBytes { get; set; } = new byte[] { 0x50, 0x4B, 0x03, 0x04 };

        public Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeviceDto>>(MockDevices.ToList());

        public Task<bool> RevokeDeviceAsync(string deviceId, CancellationToken ct = default)
        {
            var removed = MockDevices.RemoveAll(d => d.Id == deviceId);
            return Task.FromResult(removed > 0);
        }

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

        public List<DeletedItemDto> MockDeletedItems { get; } = new();
        public List<(string Id, string? TargetPath)> RestoreCalls { get; } = new();

        public Task<IReadOnlyList<DeletedItemDto>> ListDeletedItemsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeletedItemDto>>(MockDeletedItems.ToList());

        public Task<TrashActionResult<RestoreResponse>> RestoreDeletedItemAsync(string id, string? targetPath = null, CancellationToken ct = default)
        {
            RestoreCalls.Add((id, targetPath));
            var item = MockDeletedItems.FirstOrDefault(i => i.Id == id);
            if (item == null)
                return Task.FromResult(new TrashActionResult<RestoreResponse>(false, false, true, null, "Not found"));

            MockDeletedItems.Remove(item);
            return Task.FromResult(new TrashActionResult<RestoreResponse>(true, false, false,
                new RestoreResponse(targetPath ?? item.Path, 1 + item.ContainedItemCount), null));
        }

        public Task<TrashActionResult<PurgeResponse>> PurgeDeletedItemAsync(string id, CancellationToken ct = default)
        {
            var item = MockDeletedItems.FirstOrDefault(i => i.Id == id);
            if (item == null)
                return Task.FromResult(new TrashActionResult<PurgeResponse>(false, false, true, null, "Not found"));

            MockDeletedItems.Remove(item);
            return Task.FromResult(new TrashActionResult<PurgeResponse>(true, false, false,
                new PurgeResponse(1 + item.ContainedItemCount, item.Size + item.ContainedSize), null));
        }

        public Task<PurgeResponse> EmptyTrashAsync(CancellationToken ct = default)
        {
            var count = MockDeletedItems.Count;
            var size = MockDeletedItems.Sum(i => i.Size + i.ContainedSize);
            MockDeletedItems.Clear();
            return Task.FromResult(new PurgeResponse(count, size));
        }
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
    public async Task SettingsViewModel_FolderChange_PersistsToSecretStoreAndUpdatesFolderSelectionViewModel()
    {
        var api = new FakeFileSyncApi();
        var secretStore = new TestSecretStore();
        var folderVm = new FolderSelectionViewModel(secretStore);

        var tempDir = Path.Combine(Path.GetTempPath(), "InitialSyncFolder_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var targetNewFolder = Path.Combine(Path.GetTempPath(), "NewSyncFolder_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(targetNewFolder);

        try
        {
            await folderVm.SaveAndSetFolderPathAsync(tempDir);
            Assert.Equal(tempDir, folderVm.SelectedFolderPath);
            Assert.Equal(tempDir, await secretStore.GetSecretAsync("SyncFolderPath"));

            var settingsVm = new SettingsViewModel(
                api,
                secretStore,
                getSyncFolder: () => folderVm.SelectedFolderPath ?? string.Empty,
                setSyncFolder: async folder => await folderVm.SaveAndSetFolderPathAsync(folder),
                setConflictPolicy: _ => { }
            );

            Assert.Equal(tempDir, settingsVm.CurrentSyncFolder);

            // Change folder via Settings
            settingsVm.NewSyncFolderInput = targetNewFolder;
            settingsVm.RequestChangeFolder();
            Assert.True(settingsVm.ShowFolderChangeConfirmation);

            // Confirm folder change asynchronously
            await settingsVm.ConfirmChangeFolderAsync();

            Assert.False(settingsVm.ShowFolderChangeConfirmation);
            Assert.Equal(targetNewFolder, settingsVm.CurrentSyncFolder);
            Assert.Equal(targetNewFolder, folderVm.SelectedFolderPath);
            Assert.Equal(targetNewFolder, await secretStore.GetSecretAsync("SyncFolderPath"));

            // Verify a new session/app restart correctly reloads the persisted folder from secret store
            var newFolderVm = new FolderSelectionViewModel(secretStore);
            await newFolderVm.InitializeFolderAsync();
            Assert.Equal(targetNewFolder, newFolderVm.SelectedFolderPath);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            if (Directory.Exists(targetNewFolder)) Directory.Delete(targetNewFolder, recursive: true);
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

    [Fact]
    public async Task ConnectionCheckViewModel_SaveServerUrl_AppliesAndPersistsUrlOnlyOnExplicitSave()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "test_conn_settings_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var api = new FakeFileSyncApi { ServerUrl = "http://localhost:5000" };
            var secretStore = new TestSecretStore();
            var manager = new ClientSettingsManager(tempFile);
            var vm = new ConnectionCheckViewModel(api, secretStore, manager);

            // Typing a new URL in the textbox should NOT immediately overwrite api.ServerUrl, secret store, or settings.json
            vm.ServerUrl = "http://localhost:7890";
            Assert.Equal("http://localhost:5000", api.ServerUrl);
            Assert.Null(await secretStore.GetSecretAsync("ConfiguredServerUrl"));

            // Invalid URL check
            vm.ServerUrl = "not-a-valid-url";
            await vm.SaveServerUrlAsync();
            Assert.True(vm.HasErrorMessage);
            Assert.Contains("Invalid server URL", vm.ErrorMessage);
            Assert.Equal("http://localhost:5000", api.ServerUrl);
            Assert.Null(await secretStore.GetSecretAsync("ConfiguredServerUrl"));

            // Explicitly clicking Save URL applies and persists to both secret store and settings.json
            vm.ServerUrl = "http://localhost:7890/";
            await vm.SaveServerUrlAsync();

            Assert.False(vm.HasErrorMessage);
            Assert.True(vm.HasSaveMessage);
            Assert.Equal("http://localhost:7890", api.ServerUrl);
            Assert.Equal("http://localhost:7890", await secretStore.GetSecretAsync("ConfiguredServerUrl"));

            var savedSettings = await manager.LoadSettingsAsync();
            Assert.Equal("http://localhost:7890", savedSettings.ServerUrl);

            // Subsequent launch restores it
            var newVm = new ConnectionCheckViewModel(api, secretStore, manager);
            await newVm.InitializeAsync();
            Assert.Equal("http://localhost:7890", newVm.ServerUrl);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task SettingsViewModel_SaveServerUrl_AppliesAndPersistsUrl()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "test_settings_saveurl_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var api = new FakeFileSyncApi { ServerUrl = "http://localhost:5000" };
            var secretStore = new TestSecretStore();
            var manager = new ClientSettingsManager(tempFile);
            var vm = new SettingsViewModel(api, secretStore, manager, () => "/dummy", _ => Task.CompletedTask, _ => { });

            // Changing property alone does not persist
            vm.ServerUrl = "https://myserver.example.com:8443";
            Assert.Equal("http://localhost:5000", api.ServerUrl);
            Assert.Null(await secretStore.GetSecretAsync("ConfiguredServerUrl"));

            // Explicit save applies and persists to both secret store and settings.json
            await vm.SaveServerUrlAsync();
            Assert.True(vm.HasServerUrlStatusMessage);
            Assert.Contains("saved", vm.ServerUrlStatusMessage);
            Assert.Equal("https://myserver.example.com:8443", api.ServerUrl);
            Assert.Equal("https://myserver.example.com:8443", await secretStore.GetSecretAsync("ConfiguredServerUrl"));

            var savedSettings = await manager.LoadSettingsAsync();
            Assert.Equal("https://myserver.example.com:8443", savedSettings.ServerUrl);

            // Version and compile datetime are populated
            Assert.False(string.IsNullOrWhiteSpace(vm.AppVersion));
            Assert.False(string.IsNullOrWhiteSpace(vm.AppCompileDateTime));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ClientSettingsManager_LoadAndSave_PersistsJsonFileCorrectly()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "test_settings_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var manager = new ClientSettingsManager(tempFile);
            var settings = new ClientSettings
            {
                ServerUrl = "http://mycustomserver:5000",
                SyncFolderPath = "/custom/sync/path",
                ConflictPolicy = "NewestWins",
                SyncIntervalSeconds = 120,
                CloseToTray = false
            };

            await manager.SaveSettingsAsync(settings);
            Assert.True(File.Exists(tempFile));

            var loaded = await manager.LoadSettingsAsync();
            Assert.Equal("http://mycustomserver:5000", loaded.ServerUrl);
            Assert.Equal("/custom/sync/path", loaded.SyncFolderPath);
            Assert.Equal("NewestWins", loaded.ConflictPolicy);
            Assert.Equal(120, loaded.SyncIntervalSeconds);
            Assert.False(loaded.CloseToTray);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task SettingsViewModel_SettingsFileLink_ExposesPathAndPersistsOnChange()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "test_vm_settings_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var api = new FakeFileSyncApi { ServerUrl = "http://localhost:5000" };
            var secretStore = new TestSecretStore();
            var manager = new ClientSettingsManager(tempFile);

            var vm = new SettingsViewModel(
                api,
                secretStore,
                manager,
                getSyncFolder: () => "/initial/folder",
                setSyncFolder: _ => Task.CompletedTask,
                setConflictPolicy: _ => { }
            );

            Assert.Equal(tempFile, vm.SettingsFilePath);
            await vm.PersistCurrentSettingsAsync();
            Assert.True(File.Exists(tempFile));

            // Verify OpenSettingsFileCommand
            await vm.OpenSettingsFileAsync();
            Assert.True(vm.HasOpenSettingsStatus);
            Assert.False(string.IsNullOrWhiteSpace(vm.OpenSettingsStatus));

            // Verify file content has the initialized properties
            var loaded = await manager.LoadSettingsAsync();
            Assert.Equal("http://localhost:5000", loaded.ServerUrl);
            Assert.Equal("/initial/folder", loaded.SyncFolderPath);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task LoginViewModel_UsesServerUrlFromSettings_WhenLoggingIn()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "test_login_url_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var manager = new ClientSettingsManager(tempFile);
            await manager.SaveSettingsAsync(new ClientSettings
            {
                ServerUrl = "http://localhost:8080"
            });

            var api = new FakeFileSyncApi { ServerUrl = "http://localhost:5000" };
            var secretStore = new TestSecretStore();
            var tokenManager = new AuthTokenManager(secretStore, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthTokenManager>.Instance);

            var loginVm = new LoginViewModel(api, tokenManager, manager);
            Assert.Equal("http://localhost:5000", api.ServerUrl);

            // On initialization or login, the configured URL is applied
            await loginVm.InitializeSessionAsync();
            Assert.Equal("http://localhost:8080", api.ServerUrl);

            // Verify LoginAsync uses the updated URL
            loginVm.Username = "admin";
            loginVm.Password = "password";
            await loginVm.LoginAsync();

            Assert.Equal("http://localhost:8080", api.ServerUrl);
            Assert.True(loginVm.IsLoggedIn);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task MainWindowViewModel_Initialization_PreservesExistingSettingsWithoutOverwriting()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "test_main_settings_" + Guid.NewGuid().ToString("N") + ".json");
        var tempSyncFolder = Path.Combine(Path.GetTempPath(), "test_sync_folder_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempSyncFolder);

        try
        {
            var manager = new ClientSettingsManager(tempFile);
            var initialSettings = new ClientSettings
            {
                ServerUrl = "http://localhost:9090",
                SyncFolderPath = tempSyncFolder,
                ConflictPolicy = "NewestWins",
                SyncIntervalSeconds = 120,
                CloseToTray = false
            };
            await manager.SaveSettingsAsync(initialSettings);

            var api = new FakeFileSyncApi { ServerUrl = "http://localhost:5000" };
            var secretStore = new TestSecretStore();
            var tokenManager = new AuthTokenManager(secretStore, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthTokenManager>.Instance);
            var connVm = new ConnectionCheckViewModel(api, secretStore, manager);
            var loginVm = new LoginViewModel(api, tokenManager, manager);
            var folderVm = new FolderSelectionViewModel(secretStore);

            // Construct MainWindowViewModel (previously constructor triggered PersistCurrentSettingsAsync and wiped the file)
            var mainVm = new MainWindowViewModel(secretStore, api, connVm, loginVm, folderVm, manager);

            // File should remain completely unchanged after construction
            var postConstructorSettings = await manager.LoadSettingsAsync();
            Assert.Equal("http://localhost:9090", postConstructorSettings.ServerUrl);
            Assert.Equal(tempSyncFolder, postConstructorSettings.SyncFolderPath);
            Assert.Equal("NewestWins", postConstructorSettings.ConflictPolicy);
            Assert.Equal(120, postConstructorSettings.SyncIntervalSeconds);
            Assert.False(postConstructorSettings.CloseToTray);

            // Run InitializeAsync
            await mainVm.InitializeAsync();

            // Verify settings were loaded properly into ViewModel
            Assert.Equal("http://localhost:9090", api.ServerUrl);
            Assert.Equal("http://localhost:9090", mainVm.Settings.ServerUrl);
            Assert.Equal("http://localhost:9090", connVm.ServerUrl);
            Assert.Equal(tempSyncFolder, mainVm.Settings.CurrentSyncFolder);
            Assert.Equal(120, mainVm.Settings.SyncIntervalSeconds);
            Assert.False(mainVm.Settings.CloseToTray);
            Assert.Equal(ConflictPolicy.NewestWins, mainVm.Settings.SelectedConflictPolicy);

            // Verify file was not overwritten with defaults during initialization
            var postInitSettings = await manager.LoadSettingsAsync();
            Assert.Equal("http://localhost:9090", postInitSettings.ServerUrl);
            Assert.Equal(tempSyncFolder, postInitSettings.SyncFolderPath);
            Assert.Equal("NewestWins", postInitSettings.ConflictPolicy);
            Assert.Equal(120, postInitSettings.SyncIntervalSeconds);
            Assert.False(postInitSettings.CloseToTray);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (Directory.Exists(tempSyncFolder)) Directory.Delete(tempSyncFolder, recursive: true);
        }
    }

    [Fact]
    public async Task DeletedFilesViewModel_RestoresAsCopy_WhenOriginalPathExistsLocally()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "FolderSyncEr_Trash_UI_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var api = new FakeFileSyncApi();
            api.MockDeletedItems.Add(new DeletedItemDto("del-1", "notes/report.txt", false, 512, 2, DateTime.UtcNow, DateTime.UtcNow, 0, 0));
            api.MockDeletedItems.Add(new DeletedItemDto("del-2", "notes/clean.txt", false, 256, 2, DateTime.UtcNow, DateTime.UtcNow, 0, 0));

            // Create a local file at "notes/report.txt" so restore must pick a copy name
            Directory.CreateDirectory(Path.Combine(tempDir, "notes"));
            await File.WriteAllTextAsync(Path.Combine(tempDir, "notes", "report.txt"), "new local report");

            int syncTriggered = 0;
            var vm = new DeletedFilesViewModel(api, () => tempDir, () =>
            {
                syncTriggered++;
                return Task.CompletedTask;
            });

            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(2, vm.Items.Count);

            // 1. Restore "notes/report.txt" -> should restore as "notes/report (restored yyyy-MM-dd).txt"
            var reportItem = vm.Items.First(i => i.Id == "del-1");
            await reportItem.RestoreCommand.ExecuteAsync(null);

            Assert.Single(api.RestoreCalls);
            Assert.Equal("del-1", api.RestoreCalls[0].Id);
            Assert.NotNull(api.RestoreCalls[0].TargetPath);
            Assert.StartsWith("notes/report (restored ", api.RestoreCalls[0].TargetPath);
            Assert.EndsWith(").txt", api.RestoreCalls[0].TargetPath);
            Assert.Equal(1, syncTriggered);

            // 2. Restore "notes/clean.txt" (no local collision) -> should restore at original path (TargetPath == null)
            var cleanItem = vm.Items.First(i => i.Id == "del-2");
            await cleanItem.RestoreCommand.ExecuteAsync(null);

            Assert.Equal(2, api.RestoreCalls.Count);
            Assert.Equal("del-2", api.RestoreCalls[1].Id);
            Assert.Null(api.RestoreCalls[1].TargetPath);
            Assert.Equal(2, syncTriggered);
            Assert.Empty(vm.Items);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DeletedFilesViewModel_PurgeAndEmptyTrash_RequireConfirmation()
    {
        var api = new FakeFileSyncApi();
        api.MockDeletedItems.Add(new DeletedItemDto("del-1", "a.txt", false, 100, 2, DateTime.UtcNow, DateTime.UtcNow, 0, 0));
        api.MockDeletedItems.Add(new DeletedItemDto("del-2", "b.txt", false, 200, 2, DateTime.UtcNow, DateTime.UtcNow, 0, 0));

        var vm = new DeletedFilesViewModel(api, () => null, () => Task.CompletedTask);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Items.Count);

        // Request per-item purge -> shows confirmation without deleting yet
        vm.Items[0].PurgeCommand.Execute(null);
        Assert.True(vm.IsConfirmVisible);
        Assert.Equal(2, api.MockDeletedItems.Count);

        // Cancel confirmation -> nothing deleted
        vm.ConfirmNoCommand.Execute(null);
        Assert.False(vm.IsConfirmVisible);
        Assert.Equal(2, api.MockDeletedItems.Count);

        // Request per-item purge again and confirm -> deletes 1 item
        vm.Items[0].PurgeCommand.Execute(null);
        await vm.ConfirmYesCommand.ExecuteAsync(null);
        Assert.False(vm.IsConfirmVisible);
        Assert.Single(vm.Items);

        // Request Empty Trash and confirm -> deletes all remaining items
        vm.EmptyTrashCommand.Execute(null);
        Assert.True(vm.IsConfirmVisible);
        await vm.ConfirmYesCommand.ExecuteAsync(null);
        Assert.Empty(vm.Items);
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public async Task MainWindowViewModel_TrayIconState_ReflectsConnectionAndSyncStatus()
    {
        var tempSyncFolder = Path.Combine(Path.GetTempPath(), $"FolderSyncEr_Tray_{Guid.NewGuid():N}");
        var tempSettingsFile = Path.Combine(Path.GetTempPath(), $"FolderSyncEr_TraySettings_{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(tempSyncFolder);

        try
        {
            var manager = new ClientSettingsManager(tempSettingsFile);
            var api = new FakeFileSyncApi();
            var secretStore = new TestSecretStore();
            var tokenManager = new AuthTokenManager(secretStore, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthTokenManager>.Instance);
            var connVm = new ConnectionCheckViewModel(api, secretStore, manager);
            var loginVm = new LoginViewModel(api, tokenManager, manager);
            var folderVm = new FolderSelectionViewModel(secretStore);

            using var mainVm = new MainWindowViewModel(secretStore, api, connVm, loginVm, folderVm, manager);

            // 1. Disconnected initially -> TrayIconState.Disconnected
            Assert.False(connVm.IsConnected);
            Assert.Equal(TrayIconState.Disconnected, mainVm.CurrentTrayIconState);

            // 2. Connected, but not logged in / no folder -> TrayIconState.Active
            connVm.IsConnected = true;
            Assert.Equal(TrayIconState.Active, mainVm.CurrentTrayIconState);

            // 3. Logged in & folder selected, local file matches remote file -> TrayIconState.Synced
            var localFile = Path.Combine(tempSyncFolder, "doc.txt");
            await File.WriteAllTextAsync(localFile, "hello");
            var modTime = File.GetLastWriteTimeUtc(localFile);
            api.MockFiles.Add(new FileDto("1", "doc.txt", false, 5, "hash", 1, modTime, modTime, "dev", false, 1));

            await folderVm.SaveAndSetFolderPathAsync(tempSyncFolder);
            loginVm.IsLoggedIn = true;
            await mainVm.FileList.RefreshCommand.ExecuteAsync(null);

            Assert.True(mainVm.FileList.AreAllFilesInSync);
            Assert.Equal(TrayIconState.Synced, mainVm.CurrentTrayIconState);

            // 4. Local-only unsynced file added -> TrayIconState.Active
            await File.WriteAllTextAsync(Path.Combine(tempSyncFolder, "unsynced.txt"), "new content");
            await mainVm.FileList.RefreshCommand.ExecuteAsync(null);

            Assert.False(mainVm.FileList.AreAllFilesInSync);
            Assert.Equal(TrayIconState.Active, mainVm.CurrentTrayIconState);

            // 5. Connection lost -> TrayIconState.Disconnected
            connVm.IsConnected = false;
            Assert.Equal(TrayIconState.Disconnected, mainVm.CurrentTrayIconState);
        }
        finally
        {
            if (File.Exists(tempSettingsFile)) File.Delete(tempSettingsFile);
            if (Directory.Exists(tempSyncFolder)) Directory.Delete(tempSyncFolder, recursive: true);
        }
    }

    [Fact]
    public async Task LoginViewModel_ListsLoggedInDevices_AndSupportsRevokingOtherDevices()
    {
        var api = new FakeFileSyncApi();
        var secretStore = new TestSecretStore();
        var tokenManager = new AuthTokenManager(secretStore, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthTokenManager>.Instance);

        await tokenManager.SaveSessionAsync(new AuthSession
        {
            ServerUrl = "http://localhost:5000",
            DeviceId = "dev-current-123",
            Username = "admin",
            Role = "Admin",
            AccessToken = "token",
            AccessTokenExpiresUtc = DateTime.UtcNow.AddMinutes(15),
            RefreshToken = "refresh",
            RefreshTokenExpiresUtc = DateTime.UtcNow.AddDays(7)
        });

        var now = DateTime.UtcNow;
        api.MockDevices.Add(new DeviceDto("dev-other-999", 1, "Work Desktop", now.AddDays(-2), now.AddMinutes(-5), false));
        api.MockDevices.Add(new DeviceDto("dev-current-123", 1, "My Ubuntu Laptop", now.AddDays(-5), now, false));

        var loginVm = new LoginViewModel(api, tokenManager);
        await loginVm.InitializeSessionAsync();

        Assert.True(loginVm.IsLoggedIn);
        Assert.Equal("dev-current-123", loginVm.DeviceId);
        Assert.Contains("My Ubuntu Laptop (dev-current-123)", loginVm.CurrentDeviceDisplay);
        Assert.Equal(2, loginVm.LoggedInDevices.Count);

        // Current device should be listed first and marked IsCurrentDevice = true
        var first = loginVm.LoggedInDevices[0];
        Assert.Equal("dev-current-123", first.Id);
        Assert.True(first.IsCurrentDevice);
        Assert.False(first.CanRevoke);

        // Second device is another logged-in device and can be revoked
        var second = loginVm.LoggedInDevices[1];
        Assert.Equal("dev-other-999", second.Id);
        Assert.False(second.IsCurrentDevice);
        Assert.True(second.CanRevoke);

        await second.RevokeCommand.ExecuteAsync(null);
        Assert.Single(loginVm.LoggedInDevices);
        Assert.Equal("dev-current-123", loginVm.LoggedInDevices[0].Id);
    }

    [Fact]
    public async Task AuthTokenManager_And_LoginViewModel_HandleOfflineAndRejectedTokenRefreshCleanly()
    {
        var tempSettingsFile = Path.Combine(Path.GetTempPath(), $"FolderSyncEr_RefreshSettings_{Guid.NewGuid():N}.json");
        try
        {
            var manager = new ClientSettingsManager(tempSettingsFile);
            await manager.SaveSettingsAsync(new ClientSettings { ServerUrl = "http://localhost:5006" });

            var api = new FakeFileSyncApi();
            var secretStore = new TestSecretStore();
            var tokenManager = new AuthTokenManager(secretStore, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthTokenManager>.Instance);

            // Session originally saved with stale port 5293 and an expired access token
            await tokenManager.SaveSessionAsync(new AuthSession
            {
                ServerUrl = "http://localhost:5293",
                DeviceId = "dev-123",
                Username = "admin",
                Role = "Admin",
                AccessToken = "expired-access",
                AccessTokenExpiresUtc = DateTime.UtcNow.AddMinutes(-10),
                RefreshToken = "valid-refresh",
                RefreshTokenExpiresUtc = DateTime.UtcNow.AddDays(7)
            });

            var loginVm = new LoginViewModel(api, tokenManager, manager);
            await loginVm.InitializeSessionAsync(fetchDevicesFromServer: false);

            // 1. Stored session ServerUrl should be updated to settings.json URL (http://localhost:5006)
            var updatedSession = await tokenManager.GetSessionAsync();
            Assert.NotNull(updatedSession);
            Assert.Equal("http://localhost:5006", updatedSession!.ServerUrl);
            Assert.True(loginVm.IsLoggedIn);

            // 2. When server is unreachable (Connection refused), refresh returns null without wiping session
            using var offlineClient = new HttpClient(new ThrowingHttpMessageHandler(new System.Net.Http.HttpRequestException("Connection refused")));
            var tokenWhileOffline = await tokenManager.GetValidAccessTokenAsync(offlineClient);
            Assert.Null(tokenWhileOffline);
            Assert.NotNull(await tokenManager.GetSessionAsync());
            Assert.True(loginVm.IsLoggedIn);

            // 3. When server rejects refresh token (401 Unauthorized, e.g. after server reset), session is cleared and LoginViewModel signs out
            using var unauthorizedClient = new HttpClient(new StatusCodeHttpMessageHandler(System.Net.HttpStatusCode.Unauthorized));
            var tokenAfterReject = await tokenManager.GetValidAccessTokenAsync(unauthorizedClient);
            Assert.Null(tokenAfterReject);
            Assert.Null(await tokenManager.GetSessionAsync());
            Assert.False(loginVm.IsLoggedIn);
            Assert.True(loginVm.HasErrorMessage);
        }
        finally
        {
            if (File.Exists(tempSettingsFile)) File.Delete(tempSettingsFile);
        }
    }

    private sealed class ThrowingHttpMessageHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly Exception _exception;
        public ThrowingHttpMessageHandler(Exception exception) => _exception = exception;
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<System.Net.Http.HttpResponseMessage>(_exception);
    }

    private sealed class StatusCodeHttpMessageHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly System.Net.HttpStatusCode _statusCode;
        public StatusCodeHttpMessageHandler(System.Net.HttpStatusCode statusCode) => _statusCode = statusCode;
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new System.Net.Http.HttpResponseMessage(_statusCode));
    }
}
