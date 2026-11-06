using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderSyncEr.Client.Api;
using FolderSyncEr.Client.App.ViewModels;
using FolderSyncEr.Client.Platform;
using FolderSyncEr.Contracts;
using Xunit;

namespace FolderSyncEr.Client.Tests;

public class BackoffAndConnectivityTests
{
    private class FakeNetworkConnectivityService : INetworkConnectivityService
    {
        public bool IsNetworkAvailable { get; set; } = true;

        public event EventHandler<bool>? ConnectivityChanged;
        public event EventHandler? AddressOrRouteChanged;

        public bool CheckNetworkAvailable() => IsNetworkAvailable;

        public void SetNetworkAvailable(bool available)
        {
            IsNetworkAvailable = available;
            ConnectivityChanged?.Invoke(this, available);
        }

        public void TriggerAddressChanged()
        {
            AddressOrRouteChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() { }
    }

    [Fact]
    public void BackoffRetryPolicy_ZeroJitter_FollowsExactTruncatedExponentialSchedule()
    {
        var policy = new BackoffRetryPolicy(
            baseInterval: TimeSpan.FromMinutes(1),
            maxInterval: TimeSpan.FromMinutes(60),
            multiplier: 2.0,
            jitterRatio: 0.0);

        Assert.Equal(TimeSpan.FromMinutes(1), policy.CalculateDelay(1));
        Assert.Equal(TimeSpan.FromMinutes(2), policy.CalculateDelay(2));
        Assert.Equal(TimeSpan.FromMinutes(4), policy.CalculateDelay(3));
        Assert.Equal(TimeSpan.FromMinutes(8), policy.CalculateDelay(4));
        Assert.Equal(TimeSpan.FromMinutes(16), policy.CalculateDelay(5));
        Assert.Equal(TimeSpan.FromMinutes(32), policy.CalculateDelay(6));
        Assert.Equal(TimeSpan.FromMinutes(60), policy.CalculateDelay(7)); // Capped at 60m instead of 64m
        Assert.Equal(TimeSpan.FromMinutes(60), policy.CalculateDelay(8)); // Capped at 60m
        Assert.Equal(TimeSpan.FromMinutes(60), policy.CalculateDelay(20)); // Capped at 60m
    }

    [Fact]
    public void BackoffRetryPolicy_TenPercentJitter_StaysWithinTenPercentBounds()
    {
        var policy = new BackoffRetryPolicy(
            baseInterval: TimeSpan.FromMinutes(1),
            maxInterval: TimeSpan.FromMinutes(60),
            multiplier: 2.0,
            jitterRatio: 0.10);

        for (int i = 0; i < 50; i++)
        {
            var delay1 = policy.CalculateDelay(1);
            Assert.InRange(delay1.TotalSeconds, 54.0, 66.0);

            var delay2 = policy.CalculateDelay(2);
            Assert.InRange(delay2.TotalSeconds, 108.0, 132.0);

            var delay6 = policy.CalculateDelay(6);
            Assert.InRange(delay6.TotalSeconds, 32 * 60 * 0.90, 32 * 60 * 1.10);

            var delay7 = policy.CalculateDelay(7);
            Assert.InRange(delay7.TotalSeconds, 60 * 60 * 0.90, 60 * 60 * 1.10);
        }
    }

    [Fact]
    public void BackoffRetryPolicy_RecordFailureAndReset_UpdatesStateCorrectly()
    {
        var policy = new BackoffRetryPolicy(
            baseInterval: TimeSpan.FromMinutes(1),
            maxInterval: TimeSpan.FromMinutes(60),
            multiplier: 2.0,
            jitterRatio: 0.0);

        Assert.False(policy.IsInBackoff);
        Assert.Equal(0, policy.ConsecutiveFailures);
        Assert.Null(policy.NextRetryUtc);

        var fixedNow = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        var delay1 = policy.RecordFailure(fixedNow);

        Assert.True(policy.IsInBackoff);
        Assert.Equal(1, policy.ConsecutiveFailures);
        Assert.Equal(TimeSpan.FromMinutes(1), delay1);
        Assert.Equal(fixedNow.AddMinutes(1), policy.NextRetryUtc);

        var remaining = policy.GetRemainingTime(fixedNow.AddSeconds(20));
        Assert.Equal(TimeSpan.FromSeconds(40), remaining);

        policy.Reset();
        Assert.False(policy.IsInBackoff);
        Assert.Equal(0, policy.ConsecutiveFailures);
        Assert.Null(policy.NextRetryUtc);
        Assert.Null(policy.GetRemainingTime(fixedNow));
    }

    [Fact]
    public void BackoffRetryPolicy_FormattingHelpers_FormatProperly()
    {
        Assert.Equal("00:45", BackoffRetryPolicy.FormatCountdown(TimeSpan.FromSeconds(45)));
        Assert.Equal("05:30", BackoffRetryPolicy.FormatCountdown(TimeSpan.FromMinutes(5.5)));
        Assert.Equal("01:05:30", BackoffRetryPolicy.FormatCountdown(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(5.5)));

        Assert.Equal("45s", BackoffRetryPolicy.FormatInterval(TimeSpan.FromSeconds(45)));
        Assert.Equal("1m", BackoffRetryPolicy.FormatInterval(TimeSpan.FromMinutes(1)));
        Assert.Equal("5m", BackoffRetryPolicy.FormatInterval(TimeSpan.FromMinutes(5)));
        Assert.Equal("1h", BackoffRetryPolicy.FormatInterval(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void MainWindowViewModel_NetworkOffline_DisplaysOfflineStatusAndRedIndicator()
    {
        var fakeNet = new FakeNetworkConnectivityService { IsNetworkAvailable = false };
        var fakeApi = new TestApi();
        var store = new InMemorySecretStore();
        var connVm = new ConnectionCheckViewModel(fakeApi, store);
        var tokenMgr = new AuthTokenManager(store, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthTokenManager>.Instance);
        var loginVm = new LoginViewModel(fakeApi, tokenMgr);
        var folderVm = new FolderSelectionViewModel(store);
        var backoff = new BackoffRetryPolicy(jitterRatio: 0.0);

        using var vm = new MainWindowViewModel(
            store, fakeApi, connVm, loginVm, folderVm,
            networkConnectivity: fakeNet,
            backoffPolicy: backoff);

        connVm.IsConnected = false;
        vm.UpdateStatusAndCountdownDisplay();

        Assert.True(vm.ShowRetryIndicator);
        Assert.Equal("Network Offline", vm.RetryStatusText);
        Assert.Contains("No active network adapters detected", vm.RetryToolTip);
    }

    [Fact]
    public void MainWindowViewModel_ServerDisconnected_ShowsCountdown()
    {
        var fakeNet = new FakeNetworkConnectivityService { IsNetworkAvailable = true };
        var fakeApi = new TestApi { IsReachable = false };
        var store = new InMemorySecretStore();
        var connVm = new ConnectionCheckViewModel(fakeApi, store);
        var tokenMgr = new AuthTokenManager(store, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthTokenManager>.Instance);
        var loginVm = new LoginViewModel(fakeApi, tokenMgr);
        var folderVm = new FolderSelectionViewModel(store);
        var backoff = new BackoffRetryPolicy(baseInterval: TimeSpan.FromMinutes(1), jitterRatio: 0.0);

        using var vm = new MainWindowViewModel(
            store, fakeApi, connVm, loginVm, folderVm,
            networkConnectivity: fakeNet,
            backoffPolicy: backoff);

        connVm.IsConnected = false;
        vm.UpdateStatusAndCountdownDisplay();

        Assert.True(vm.ShowRetryIndicator);
        Assert.StartsWith("Next retry in", vm.RetryStatusText);
        Assert.Contains("Attempt #1", vm.RetryToolTip);
    }

    [Fact]
    public void MainWindowViewModel_NetworkRestored_ResetsBackoff()
    {
        var fakeNet = new FakeNetworkConnectivityService { IsNetworkAvailable = false };
        var fakeApi = new TestApi { IsReachable = true };
        var store = new InMemorySecretStore();
        var connVm = new ConnectionCheckViewModel(fakeApi, store);
        var tokenMgr = new AuthTokenManager(store, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthTokenManager>.Instance);
        var loginVm = new LoginViewModel(fakeApi, tokenMgr);
        var folderVm = new FolderSelectionViewModel(store);
        var backoff = new BackoffRetryPolicy(jitterRatio: 0.0);

        using var vm = new MainWindowViewModel(
            store, fakeApi, connVm, loginVm, folderVm,
            networkConnectivity: fakeNet,
            backoffPolicy: backoff);

        backoff.RecordFailure();
        backoff.RecordFailure();
        Assert.Equal(2, backoff.ConsecutiveFailures);

        // When network connectivity is restored and server is online
        fakeNet.SetNetworkAvailable(true);

        Assert.Equal(0, backoff.ConsecutiveFailures);
        Assert.False(backoff.IsInBackoff);
    }

    [Fact]
    public async Task ServerUnreachable_SetsInternalFlag_DistinctFromNetworkOffline()
    {
        var testApi = new TestApi { IsReachable = false };
        var connectionVm = new ConnectionCheckViewModel(testApi);

        // Check connection when server is unreachable
        await connectionVm.CheckConnectionAsync();
        Assert.False(connectionVm.IsConnected);
        Assert.True(connectionVm.IsServerUnreachable);

        // Now server comes back online
        testApi.IsReachable = true;
        await connectionVm.CheckConnectionAsync();
        Assert.True(connectionVm.IsConnected);
        Assert.False(connectionVm.IsServerUnreachable);
    }

    private class InMemorySecretStore : ISecretStore
    {
        public SecretStoreType StoreType => SecretStoreType.EncryptedFileFallback;
        public string Description => "Test Store";
        public bool IsInsecureFallback => false;
        public Task<string?> GetSecretAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetSecretAsync(string key, string secret, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteSecretAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }

    private class TestApi : IFileSyncApi
    {
        public string ServerUrl { get; set; } = "http://localhost:5000";
        public bool IsReachable { get; set; }

        public Task<ApiConnectionStatus> CheckConnectionAsync(CancellationToken ct = default) =>
            Task.FromResult(IsReachable
                ? new ApiConnectionStatus(true, "Healthy", "1.0", DateTime.UtcNow, 5, null)
                : new ApiConnectionStatus(false, "Unreachable", null, null, 0, "Failed"));
        public Task<LoginResponse> LoginAsync(string username, string password, string deviceName, CancellationToken ct = default) =>
            Task.FromResult(new LoginResponse("token", DateTime.UtcNow.AddHours(1), "refresh", DateTime.UtcNow.AddDays(7), new UserDto(1, username, "User", DateTime.UtcNow, null)));
        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<UserDto?> GetMeAsync(CancellationToken ct = default) => Task.FromResult<UserDto?>(null);
        public Task<FileListResponse> ListFilesAsync(long? sinceSeq = null, DateTime? since = null, bool includeDeleted = false, string? cursor = null, int limit = 1000, CancellationToken ct = default) =>
            Task.FromResult(new FileListResponse(new List<FileDto>(), null, 0, DateTime.UtcNow));
        public Task<FileDto?> GetFileMetaByPathAsync(string path, CancellationToken ct = default) => Task.FromResult<FileDto?>(null);
        public Task<FileDto?> GetFileMetaByIdAsync(string id, CancellationToken ct = default) => Task.FromResult<FileDto?>(null);
        public Task<Stream> DownloadFileStreamAsync(string id, (long from, long? to)? range = null, CancellationToken ct = default) => Task.FromResult<Stream>(new MemoryStream());
        public Task<UploadResult> UploadFileStreamAsync(string path, Stream contentStream, string? sha256Hex = null, string? ifMatchSha256 = null, string? ifNoneMatch = null, long? baseVersion = null, DateTime? lastModifiedUtc = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<FileDto> CreateDirectoryAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> DeleteFileAsync(string id, string? ifMatchSha256 = null, long? baseVersion = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<FileDto?> MoveFileAsync(string fromPath, string toPath, string? ifMatchSha256 = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<FileDto> PatchFileLastModifiedAsync(string id, DateTime lastModifiedUtc, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<StorageInfoDto> GetStorageInfoAsync(CancellationToken ct = default) => Task.FromResult(new StorageInfoDto(1000, 200, 5000));
        public Task<Stream> ExportFolderZipAsync(CancellationToken ct = default) => Task.FromResult<Stream>(new MemoryStream());
        public Task<IReadOnlyList<DeletedItemDto>> ListDeletedItemsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DeletedItemDto>>(Array.Empty<DeletedItemDto>());
        public Task<TrashActionResult<RestoreResponse>> RestoreDeletedItemAsync(string id, string? targetPath = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TrashActionResult<PurgeResponse>> PurgeDeletedItemAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PurgeResponse> EmptyTrashAsync(CancellationToken ct = default) => Task.FromResult(new PurgeResponse(0, 0));
        public Task<PagedResult<ActivityLogDto>> GetLogsAsync(int page = 1, int pageSize = 50, string? action = null, string? result = null, string? path = null, DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken ct = default) =>
            Task.FromResult(new PagedResult<ActivityLogDto>(new List<ActivityLogDto>(), 1, 50, 0));
        public Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DeviceDto>>(Array.Empty<DeviceDto>());
        public Task<bool> RevokeDeviceAsync(string deviceId, CancellationToken ct = default) => Task.FromResult(true);
    }
}
