using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FolderSyncEr.Client.Api;
using FolderSyncEr.Client.Sync.Common;
using FolderSyncEr.Client.Sync.Registry;
using FolderSyncEr.Client.Sync.Storage;
using FolderSyncEr.Contracts;
using Xunit;

namespace FolderSyncEr.Client.Sync.Tests;

public class RestRemoteStorageTests
{
    [Fact]
    public async Task ListItemsAsync_ServesStrictlyFromSnapshot_NoNetworkCalls()
    {
        var fakeApi = new FakeFileSyncApi();
        var snapshot = new RemoteSnapshot();
        var failureRegistry = new RunFailureRegistry();
        var storage = new RestRemoteStorage(fakeApi, snapshot, failureRegistry);

        snapshot.AddOrUpdate(new FileDto("id-1", "folder/file.txt", false, 10, "sha", 1, DateTime.UtcNow, DateTime.UtcNow, "dev", false, 1));

        var items = await storage.ListItemsAsync("folder");

        Assert.Single(items);
        Assert.Equal(0, fakeApi.ListFilesCallCount); // Verified: no network call!
    }

    [Fact]
    public async Task ComputeHashAsync_ReturnsBase64FromSnapshot_WithoutDownloading()
    {
        var fakeApi = new FakeFileSyncApi();
        var snapshot = new RemoteSnapshot();
        var failureRegistry = new RunFailureRegistry();
        var storage = new RestRemoteStorage(fakeApi, snapshot, failureRegistry);

        var hexSha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        snapshot.AddOrUpdate(new FileDto("id-1", "test.txt", false, 100, hexSha, 1, DateTime.UtcNow, DateTime.UtcNow, "dev", false, 1));

        var hash = await storage.ComputeHashAsync("test.txt");

        Assert.Equal(HashConverter.HexToBase64(hexSha), hash);
        Assert.Equal(0, fakeApi.DownloadCallCount); // Verified: zero downloads!
    }

    [Fact]
    public async Task WriteFileAsync_UploadsStream_AndUpdatesSnapshot()
    {
        var fakeApi = new FakeFileSyncApi();
        var snapshot = new RemoteSnapshot();
        var failureRegistry = new RunFailureRegistry();
        var storage = new RestRemoteStorage(fakeApi, snapshot, failureRegistry);

        var content = "Sample upload content";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(content));

        await storage.WriteFileAsync("upload.txt", ms);

        Assert.Equal(1, fakeApi.UploadCallCount);
        Assert.True(snapshot.TryGetFileDto("upload.txt", out var dto));
        Assert.NotNull(dto);
        Assert.Equal("upload.txt", dto.Path);
    }

    [Fact]
    public async Task WriteFileAsync_OnConflict_RecordsFailureInRegistry()
    {
        var fakeApi = new FakeFileSyncApi { ShouldSimulateConflict = true };
        var snapshot = new RemoteSnapshot();
        var failureRegistry = new RunFailureRegistry();
        var storage = new RestRemoteStorage(fakeApi, snapshot, failureRegistry);

        using var ms = new MemoryStream(new byte[10]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.WriteFileAsync("conflict.txt", ms));

        Assert.True(failureRegistry.HasFailed("conflict.txt"));
    }

    private class FakeFileSyncApi : IFileSyncApi
    {
        public string ServerUrl { get; set; } = "http://localhost:5000";
        public int ListFilesCallCount { get; private set; }
        public int DownloadCallCount { get; private set; }
        public int UploadCallCount { get; private set; }
        public bool ShouldSimulateConflict { get; set; }

        public Task<ApiConnectionStatus> CheckConnectionAsync(CancellationToken ct = default) =>
            Task.FromResult(new ApiConnectionStatus(true, "Healthy", "1.0", DateTime.UtcNow, 5, null));

        public Task<LoginResponse> LoginAsync(string username, string password, string deviceName, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<UserDto?> GetMeAsync(CancellationToken ct = default) => Task.FromResult<UserDto?>(null);

        public Task<FileListResponse> ListFilesAsync(long? sinceSeq = null, DateTime? since = null, bool includeDeleted = false, string? cursor = null, int limit = 1000, CancellationToken ct = default)
        {
            ListFilesCallCount++;
            return Task.FromResult(new FileListResponse(Array.Empty<FileDto>(), null, 0, DateTime.UtcNow));
        }

        public Task<FileDto?> GetFileMetaByPathAsync(string path, CancellationToken ct = default) => Task.FromResult<FileDto?>(null);
        public Task<FileDto?> GetFileMetaByIdAsync(string id, CancellationToken ct = default) => Task.FromResult<FileDto?>(null);

        public Task<Stream> DownloadFileStreamAsync(string id, (long from, long? to)? range = null, CancellationToken ct = default)
        {
            DownloadCallCount++;
            return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("downloaded")));
        }

        public Task<UploadResult> UploadFileStreamAsync(string path, Stream contentStream, string? sha256Hex = null, string? ifMatchSha256 = null, string? ifNoneMatch = null, long? baseVersion = null, DateTime? lastModifiedUtc = null, CancellationToken ct = default)
        {
            UploadCallCount++;
            if (ShouldSimulateConflict)
            {
                return Task.FromResult(new UploadResult(false, true, false, null, null, "409 Precondition Failed"));
            }

            var dto = new FileDto("id-uploaded", path, false, 100, "abc", 1, DateTime.UtcNow, DateTime.UtcNow, "dev", false, 1);
            return Task.FromResult(new UploadResult(true, false, false, dto, null, null));
        }

        public Task<FileDto> CreateDirectoryAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(new FileDto("dir-id", path, true, 0, null, 1, DateTime.UtcNow, DateTime.UtcNow, "dev", false, 1));

        public Task<bool> DeleteFileAsync(string id, string? ifMatchSha256 = null, long? baseVersion = null, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<FileDto?> MoveFileAsync(string fromPath, string toPath, string? ifMatchSha256 = null, CancellationToken ct = default) =>
            Task.FromResult<FileDto?>(null);

        public Task<StorageInfoDto> GetStorageInfoAsync(CancellationToken ct = default) =>
            Task.FromResult(new StorageInfoDto(1000, 200, 5000));

        public Task<Stream> ExportFolderZipAsync(CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task<PagedResult<ActivityLogDto>> GetLogsAsync(int page = 1, int pageSize = 50, string? action = null, string? result = null, string? path = null, DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }
}
