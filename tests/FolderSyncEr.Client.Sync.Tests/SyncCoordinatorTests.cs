using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FolderSyncEr.Client.Api;
using FolderSyncEr.Client.Sync.Common;
using FolderSyncEr.Client.Sync.Conflicts;
using FolderSyncEr.Client.Sync.Coordinator;
using FolderSyncEr.Contracts;
using Xunit;

namespace FolderSyncEr.Client.Sync.Tests;

public class SyncCoordinatorTests : IDisposable
{
    private readonly string _testDir;
    private readonly FakeCoordinatorApi _fakeApi;
    private readonly SyncCoordinator _coordinator;

    public SyncCoordinatorTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"coordinator_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _fakeApi = new FakeCoordinatorApi();
        _coordinator = new SyncCoordinator(_fakeApi, _testDir, "test-device", ConflictPolicy.KeepBoth);
    }

    [Fact]
    public async Task RunSyncAsync_FailsGracefully_WhenServerUnreachable()
    {
        _fakeApi.IsServerConnected = false;

        var result = await _coordinator.RunSyncAsync();

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("Server unreachable", result.ErrorMessage);
    }

    [Fact]
    public async Task RunSyncAsync_CompletesSuccessfully_WithEmptyFolder()
    {
        _fakeApi.IsServerConnected = true;

        var result = await _coordinator.RunSyncAsync();

        Assert.True(result.Success, result.ErrorMessage ?? "Success was false without message");
        Assert.Equal(0, result.ErrorsCount);
    }

    [Fact]
    public async Task RunSyncAsync_UploadsNewLocalFile_WithoutConflict()
    {
        _fakeApi.IsServerConnected = true;

        var localFile = Path.Combine(_testDir, "hello.txt");
        await File.WriteAllTextAsync(localFile, "Hello sync world");

        int conflictCount = 0;
        _coordinator.ConflictDetected += (s, e) => conflictCount++;

        var result = await _coordinator.RunSyncAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(0, conflictCount);
        Assert.Equal(0, result.ConflictsResolved);

        var conflictFiles = Directory.GetFiles(_testDir, "*conflict*");
        Assert.Empty(conflictFiles);
    }

    [Fact]
    public async Task RunSyncAsync_TwoConsecutiveSyncs_NoConflict()
    {
        _fakeApi.IsServerConnected = true;

        var localFile = Path.Combine(_testDir, "test.txt");
        await File.WriteAllTextAsync(localFile, "Initial content");

        int conflictCount = 0;
        _coordinator.ConflictDetected += (s, e) => conflictCount++;

        // First sync: uploads the file
        var result1 = await _coordinator.RunSyncAsync();
        Assert.True(result1.Success, result1.ErrorMessage);

        // Second sync: should be an in-sync no-op run, NO conflicts!
        var result2 = await _coordinator.RunSyncAsync();
        Assert.True(result2.Success, result2.ErrorMessage);
        Assert.Equal(0, conflictCount);
        Assert.Equal(0, result2.ConflictsResolved);

        var conflictFiles = Directory.GetFiles(_testDir, "*conflict*");
        Assert.Empty(conflictFiles);
    }

    [Fact]
    public async Task RunSyncAsync_DownloadsRestoredFile_AfterLocalDeleteAndServerRestore()
    {
        _fakeApi.IsServerConnected = true;

        var localFile = Path.Combine(_testDir, "restore-me.txt");
        await File.WriteAllTextAsync(localFile, "Initial content");

        // 1. Initial sync uploads the file (v1)
        var r1 = await _coordinator.RunSyncAsync();
        Assert.True(r1.Success, r1.ErrorMessage);
        Assert.Single(_fakeApi.ServerFiles.Where(f => !f.IsDeleted));

        // 2. Delete locally and sync -> server tombstones it (v2)
        File.Delete(localFile);
        var r2 = await _coordinator.RunSyncAsync();
        Assert.True(r2.Success, r2.ErrorMessage);
        Assert.Empty(_fakeApi.ServerFiles.Where(f => !f.IsDeleted));

        // 3. Simulate server restore from Trash -> IsDeleted = false, Version = 3
        var tombstoned = _fakeApi.ServerFiles.First();
        _fakeApi.ServerFiles[0] = tombstoned with { IsDeleted = false, Version = tombstoned.Version + 1, ChangeSeq = tombstoned.ChangeSeq + 1 };

        // 4. Sync again -> file should be downloaded back into the local sync folder!
        var r3 = await _coordinator.RunSyncAsync();
        Assert.True(r3.Success, r3.ErrorMessage);
        Assert.True(File.Exists(localFile), "Restored file should be downloaded back to the local sync folder");
        Assert.Equal("Initial content", await File.ReadAllTextAsync(localFile));

        // 5. Simulate another device deleting the file on the server -> local sync moves it to local trash
        var current = _fakeApi.ServerFiles.First();
        _fakeApi.ServerFiles[0] = current with { IsDeleted = true, Version = current.Version + 1, ChangeSeq = current.ChangeSeq + 1 };

        var r4 = await _coordinator.RunSyncAsync();
        Assert.True(r4.Success, r4.ErrorMessage);
        Assert.False(File.Exists(localFile), "Server-deleted file should be removed from the local sync folder");
    }

    [Fact]
    public async Task RunSyncAsync_RemoteFolderRename_DoesNotRecreateOldFolder()
    {
        _fakeApi.IsServerConnected = true;

        var oldDir = Path.Combine(_testDir, "FolderA");
        Directory.CreateDirectory(oldDir);
        var oldFile = Path.Combine(oldDir, "note.txt");
        await File.WriteAllTextAsync(oldFile, "Initial content");

        // 1. Initial sync uploads FolderA and FolderA/note.txt
        var r1 = await _coordinator.RunSyncAsync();
        Assert.True(r1.Success, r1.ErrorMessage);

        // 2. Simulate website renaming FolderA -> FolderB on the server
        for (int i = 0; i < _fakeApi.ServerFiles.Count; i++)
        {
            var f = _fakeApi.ServerFiles[i];
            if (f.Path.Equals("FolderA", StringComparison.OrdinalIgnoreCase))
            {
                _fakeApi.ServerFiles[i] = f with { Path = "FolderB", Version = f.Version + 1, ChangeSeq = f.ChangeSeq + 1 };
            }
            else if (f.Path.StartsWith("FolderA/", StringComparison.OrdinalIgnoreCase))
            {
                _fakeApi.ServerFiles[i] = f with
                {
                    Path = "FolderB/" + f.Path.Substring("FolderA/".Length),
                    Version = f.Version + 1,
                    ChangeSeq = f.ChangeSeq + 1
                };
            }
        }

        // 3. First sync after website rename
        var r2 = await _coordinator.RunSyncAsync();
        Assert.True(r2.Success, r2.ErrorMessage);

        // 4. Second sync after website rename
        var r3 = await _coordinator.RunSyncAsync();
        Assert.True(r3.Success, r3.ErrorMessage);

        var newDir = Path.Combine(_testDir, "FolderB");
        var newFile = Path.Combine(newDir, "note.txt");
        Assert.True(Directory.Exists(newDir), "FolderB should exist locally");
        Assert.True(File.Exists(newFile), "FolderB/note.txt should exist locally");
        Assert.False(Directory.Exists(oldDir), "FolderA should NOT exist locally after rename");
        Assert.DoesNotContain(_fakeApi.ServerFiles, f => !f.IsDeleted && f.Path.StartsWith("FolderA", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BackgroundSync_CooldownAndMaxWait_TriggersAfterQuietPeriod()
    {
        _fakeApi.IsServerConnected = true;

        var localFile = Path.Combine(_testDir, "cooldown_test.txt");
        await File.WriteAllTextAsync(localFile, "Initial content");

        var syncTcs = new TaskCompletionSource<bool>();
        int syncCount = 0;
        _coordinator.SyncCompleted += (s, e) =>
        {
            syncCount++;
            syncTcs.TrySetResult(true);
        };

        // Start background sync with a 150ms cooldown and 500ms max wait for test speed
        _coordinator.StartBackgroundSync(
            periodicInterval: TimeSpan.FromMinutes(10),
            cooldown: TimeSpan.FromMilliseconds(150),
            maxWait: TimeSpan.FromMilliseconds(500));

        // Rapidly write to the file 3 times within 60ms intervals (resetting cooldown each time)
        await File.WriteAllTextAsync(localFile, "Write 1");
        _coordinator.TriggerFileSystemChange("cooldown_test.txt", WatcherChangeTypes.Changed);
        await Task.Delay(60);
        Assert.Equal(0, syncCount); // Should NOT have synced yet (cooldown resetting)

        await File.WriteAllTextAsync(localFile, "Write 2");
        _coordinator.TriggerFileSystemChange("cooldown_test.txt", WatcherChangeTypes.Changed);
        await Task.Delay(60);
        Assert.Equal(0, syncCount); // Still waiting for quiet period

        await File.WriteAllTextAsync(localFile, "Write 3");
        _coordinator.TriggerFileSystemChange("cooldown_test.txt", WatcherChangeTypes.Changed);

        // Wait up to 1500ms for quiet period to elapse and sync to complete
        var completed = await Task.WhenAny(syncTcs.Task, Task.Delay(1500)) == syncTcs.Task;

        Assert.True(completed, "Sync should have triggered once quiet cooldown elapsed");
        Assert.True(syncCount >= 1);
        var serverFile = _fakeApi.ServerFiles.FirstOrDefault(f => f.Path == "cooldown_test.txt");
        Assert.NotNull(serverFile);
    }

    [Fact]
    public async Task RunSyncAsync_RemoteImplicitFolderRename_DoesNotRecreateOldFolder()
    {
        _fakeApi.IsServerConnected = true;

        // Simulate a folder uploaded straight from the website (no explicit directory FileDto for "ImplicitFolder", only "ImplicitFolder/doc.txt")
        _fakeApi.ServerFiles.Add(new FileDto(
            Id: "file-1",
            Path: "ImplicitFolder/doc.txt",
            IsDirectory: false,
            Size: "Initial content".Length,
            Sha256: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("Initial content"))).ToLowerInvariant(),
            Version: 1,
            LastModifiedUtc: DateTime.UtcNow,
            UploadedAtUtc: DateTime.UtcNow,
            UploadedByDeviceId: "web",
            IsDeleted: false,
            ChangeSeq: 1
        ));

        // 1. Initial sync downloads ImplicitFolder/doc.txt locally
        var r1 = await _coordinator.RunSyncAsync();
        Assert.True(r1.Success, r1.ErrorMessage);
        Assert.True(Directory.Exists(Path.Combine(_testDir, "ImplicitFolder")));
        Assert.True(File.Exists(Path.Combine(_testDir, "ImplicitFolder", "doc.txt")));

        // 2. Website renames ImplicitFolder -> RenamedFolder (FileService materializes RenamedFolder dir and updates child path)
        _fakeApi.ServerFiles[0] = _fakeApi.ServerFiles[0] with { Path = "RenamedFolder/doc.txt", Version = 2, ChangeSeq = 2 };
        _fakeApi.ServerFiles.Add(new FileDto("dir-2", "RenamedFolder", true, 0, null, 1, DateTime.UtcNow, DateTime.UtcNow, "web", false, 3));

        // 3. First sync after website rename
        var r2 = await _coordinator.RunSyncAsync();
        Assert.True(r2.Success, r2.ErrorMessage);

        // 4. Second sync after website rename
        var r3 = await _coordinator.RunSyncAsync();
        Assert.True(r3.Success, r3.ErrorMessage);

        Assert.True(Directory.Exists(Path.Combine(_testDir, "RenamedFolder")), "RenamedFolder should exist locally");
        Assert.True(File.Exists(Path.Combine(_testDir, "RenamedFolder", "doc.txt")), "RenamedFolder/doc.txt should exist locally");
        Assert.False(Directory.Exists(Path.Combine(_testDir, "ImplicitFolder")), "ImplicitFolder should NOT exist locally after rename");
        Assert.DoesNotContain(_fakeApi.ServerFiles, f => !f.IsDeleted && f.Path.StartsWith("ImplicitFolder", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch { }
    }

    private class FakeCoordinatorApi : IFileSyncApi
    {
        public string ServerUrl { get; set; } = "http://localhost:5000";
        public bool IsServerConnected { get; set; } = true;

        public Task<ApiConnectionStatus> CheckConnectionAsync(CancellationToken ct = default) =>
            Task.FromResult(new ApiConnectionStatus(IsServerConnected, IsServerConnected ? "Healthy" : "Offline", "1.0", DateTime.UtcNow, 5, IsServerConnected ? null : "Network down"));

        public Task<LoginResponse> LoginAsync(string username, string password, string deviceName, CancellationToken ct = default) => throw new NotImplementedException();
        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<UserDto?> GetMeAsync(CancellationToken ct = default) => Task.FromResult<UserDto?>(null);

        public List<FileDto> ServerFiles { get; } = new();

        public Task<FileListResponse> ListFilesAsync(long? sinceSeq = null, DateTime? since = null, bool includeDeleted = false, string? cursor = null, int limit = 1000, CancellationToken ct = default)
        {
            var items = includeDeleted ? ServerFiles.ToList() : ServerFiles.Where(f => !f.IsDeleted).ToList();
            return Task.FromResult(new FileListResponse(items, null, ServerFiles.Count, DateTime.UtcNow));
        }

        public Task<FileDto?> GetFileMetaByPathAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(ServerFiles.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)));

        public Task<FileDto?> GetFileMetaByIdAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ServerFiles.FirstOrDefault(f => f.Id == id));

        public Task<Stream> DownloadFileStreamAsync(string id, (long from, long? to)? range = null, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("Initial content")));

        public Task<UploadResult> UploadFileStreamAsync(string path, Stream contentStream, string? sha256Hex = null, string? ifMatchSha256 = null, string? ifNoneMatch = null, long? baseVersion = null, DateTime? lastModifiedUtc = null, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            contentStream.CopyTo(ms);
            var bytes = ms.ToArray();
            var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

            var existing = ServerFiles.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            var version = (existing?.Version ?? 0) + 1;
            var dto = new FileDto(
                Id: existing?.Id ?? Guid.NewGuid().ToString("N"),
                Path: path,
                IsDirectory: false,
                Size: bytes.Length,
                Sha256: sha256,
                Version: version,
                LastModifiedUtc: lastModifiedUtc ?? DateTime.UtcNow,
                UploadedAtUtc: DateTime.UtcNow,
                UploadedByDeviceId: "dev",
                IsDeleted: false,
                ChangeSeq: version
            );

            if (existing != null) ServerFiles.Remove(existing);
            ServerFiles.Add(dto);

            return Task.FromResult(new UploadResult(true, false, false, dto, null, null));
        }

        public Task<FileDto> CreateDirectoryAsync(string path, CancellationToken ct = default)
        {
            var existing = ServerFiles.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            var version = (existing?.Version ?? 0) + 1;
            var dto = new FileDto(existing?.Id ?? Guid.NewGuid().ToString("N"), path, true, 0, null, version, DateTime.UtcNow, DateTime.UtcNow, "dev", false, version);
            if (existing != null) ServerFiles.Remove(existing);
            ServerFiles.Add(dto);
            return Task.FromResult(dto);
        }

        public Task<bool> DeleteFileAsync(string id, string? ifMatchSha256 = null, long? baseVersion = null, CancellationToken ct = default)
        {
            var idx = ServerFiles.FindIndex(f => f.Id == id);
            if (idx >= 0)
            {
                var cur = ServerFiles[idx];
                ServerFiles[idx] = cur with { IsDeleted = true, Version = cur.Version + 1, ChangeSeq = cur.ChangeSeq + 1 };
            }
            return Task.FromResult(true);
        }

        public Task<FileDto?> MoveFileAsync(string fromPath, string toPath, string? ifMatchSha256 = null, CancellationToken ct = default) =>
            Task.FromResult<FileDto?>(null);

        public Task<StorageInfoDto> GetStorageInfoAsync(CancellationToken ct = default) =>
            Task.FromResult(new StorageInfoDto(10000, 1000, 50000));

        public Task<Stream> ExportFolderZipAsync(CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task<IReadOnlyList<DeletedItemDto>> ListDeletedItemsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeletedItemDto>>(Array.Empty<DeletedItemDto>());

        public Task<TrashActionResult<RestoreResponse>> RestoreDeletedItemAsync(string id, string? targetPath = null, CancellationToken ct = default) =>
            Task.FromResult(new TrashActionResult<RestoreResponse>(true, false, false, new RestoreResponse(targetPath ?? string.Empty, 1), null));

        public Task<TrashActionResult<PurgeResponse>> PurgeDeletedItemAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(new TrashActionResult<PurgeResponse>(true, false, false, new PurgeResponse(1, 0), null));

        public Task<PurgeResponse> EmptyTrashAsync(CancellationToken ct = default) =>
            Task.FromResult(new PurgeResponse(0, 0));

        public Task<PagedResult<ActivityLogDto>> GetLogsAsync(int page = 1, int pageSize = 50, string? action = null, string? result = null, string? path = null, DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }
}
