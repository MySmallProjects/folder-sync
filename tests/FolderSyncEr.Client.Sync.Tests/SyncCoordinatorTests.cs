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

        public Task<FileListResponse> ListFilesAsync(long? sinceSeq = null, DateTime? since = null, bool includeDeleted = false, string? cursor = null, int limit = 1000, CancellationToken ct = default) =>
            Task.FromResult(new FileListResponse(ServerFiles.ToList(), null, ServerFiles.Count, DateTime.UtcNow));

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

        public Task<FileDto> CreateDirectoryAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(new FileDto("dir-id", path, true, 0, null, 1, DateTime.UtcNow, DateTime.UtcNow, "dev", false, 1));

        public Task<bool> DeleteFileAsync(string id, string? ifMatchSha256 = null, long? baseVersion = null, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<FileDto?> MoveFileAsync(string fromPath, string toPath, string? ifMatchSha256 = null, CancellationToken ct = default) =>
            Task.FromResult<FileDto?>(null);

        public Task<StorageInfoDto> GetStorageInfoAsync(CancellationToken ct = default) =>
            Task.FromResult(new StorageInfoDto(10000, 1000, 50000));

        public Task<Stream> ExportFolderZipAsync(CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task<PagedResult<ActivityLogDto>> GetLogsAsync(int page = 1, int pageSize = 50, string? action = null, string? result = null, string? path = null, DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }
}
