using FolderSyncEr.Client.Api;
using FolderSyncEr.Client.Platform;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderSyncEr.Client.Tests;

public class AuthTokenManagerTests : IDisposable
{
    private readonly string _tempFile;
    private readonly ISecretStore _store;
    private readonly AuthTokenManager _manager;

    public AuthTokenManagerTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"auth_test_{Guid.NewGuid():N}.dat");
        _store = new EncryptedFileFallbackSecretStore(_tempFile);
        _manager = new AuthTokenManager(_store, NullLogger<AuthTokenManager>.Instance);
    }

    [Fact]
    public async Task GetOrCreateDeviceId_GeneratesAndPersistsSameId()
    {
        var id1 = await _manager.GetOrCreateDeviceIdAsync();
        Assert.False(string.IsNullOrWhiteSpace(id1));

        var id2 = await _manager.GetOrCreateDeviceIdAsync();
        Assert.Equal(id1, id2);
    }

    [Fact]
    public async Task SaveAndGetSession_PersistsAuthSession()
    {
        var session = new AuthSession
        {
            ServerUrl = "http://localhost:5000",
            DeviceId = "dev-test-1",
            Username = "user1",
            Role = "User",
            AccessToken = "jwt-access-token",
            AccessTokenExpiresUtc = DateTime.UtcNow.AddMinutes(15),
            RefreshToken = "refresh-token-123",
            RefreshTokenExpiresUtc = DateTime.UtcNow.AddDays(30)
        };

        await _manager.SaveSessionAsync(session);

        var loaded = await _manager.GetSessionAsync();
        Assert.NotNull(loaded);
        Assert.Equal(session.Username, loaded.Username);
        Assert.Equal(session.AccessToken, loaded.AccessToken);
        Assert.True(loaded.IsAccessTokenValid);
        Assert.True(loaded.IsRefreshTokenValid);

        await _manager.ClearSessionAsync();
        var cleared = await _manager.GetSessionAsync();
        Assert.Null(cleared);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            try { File.Delete(_tempFile); } catch { }
        }
    }
}

