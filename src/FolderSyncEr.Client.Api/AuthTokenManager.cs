using System.Net.Http.Json;
using System.Text.Json;
using FolderSyncEr.Client.Platform;
using FolderSyncEr.Contracts;
using Microsoft.Extensions.Logging;

namespace FolderSyncEr.Client.Api;

public class AuthSession
{
    public string ServerUrl { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public DateTime AccessTokenExpiresUtc { get; set; }
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime RefreshTokenExpiresUtc { get; set; }

    public bool IsAccessTokenValid =>
        !string.IsNullOrWhiteSpace(AccessToken) && DateTime.UtcNow < AccessTokenExpiresUtc.AddSeconds(-30);

    public bool IsRefreshTokenValid =>
        !string.IsNullOrWhiteSpace(RefreshToken) && DateTime.UtcNow < RefreshTokenExpiresUtc;
}

public class AuthTokenManager
{
    private const string SessionKey = "AuthSession";
    private const string DeviceIdKey = "DeviceId";

    private readonly ISecretStore _secretStore;
    private readonly ILogger<AuthTokenManager> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private AuthSession? _currentSession;
    private string? _deviceId;

    public AuthTokenManager(ISecretStore secretStore, ILogger<AuthTokenManager> logger)
    {
        _secretStore = secretStore;
        _logger = logger;
    }

    public async Task<string> GetOrCreateDeviceIdAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_deviceId))
        {
            return _deviceId;
        }

        var stored = await _secretStore.GetSecretAsync(DeviceIdKey, ct);
        if (!string.IsNullOrWhiteSpace(stored))
        {
            _deviceId = stored;
            return _deviceId;
        }

        _deviceId = Guid.NewGuid().ToString("N");
        await _secretStore.SetSecretAsync(DeviceIdKey, _deviceId, ct);
        return _deviceId;
    }

    public async Task<AuthSession?> GetSessionAsync(CancellationToken ct = default)
    {
        if (_currentSession != null)
        {
            return _currentSession;
        }

        var json = await _secretStore.GetSecretAsync(SessionKey, ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            _currentSession = JsonSerializer.Deserialize<AuthSession>(json);
            return _currentSession;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize stored auth session.");
            return null;
        }
    }

    public async Task SaveSessionAsync(AuthSession session, CancellationToken ct = default)
    {
        _currentSession = session;
        var json = JsonSerializer.Serialize(session);
        await _secretStore.SetSecretAsync(SessionKey, json, ct);
    }

    public async Task ClearSessionAsync(CancellationToken ct = default)
    {
        _currentSession = null;
        await _secretStore.DeleteSecretAsync(SessionKey, ct);
    }

    public async Task<string?> GetValidAccessTokenAsync(HttpClient httpClient, CancellationToken ct = default)
    {
        var session = await GetSessionAsync(ct);
        if (session == null)
        {
            return null;
        }

        if (session.IsAccessTokenValid)
        {
            return session.AccessToken;
        }

        if (!session.IsRefreshTokenValid)
        {
            _logger.LogInformation("Refresh token is expired. User must log in again.");
            await ClearSessionAsync(ct);
            return null;
        }

        // Single-flight refresh
        await _refreshLock.WaitAsync(ct);
        try
        {
            // Recheck inside lock
            if (session.IsAccessTokenValid)
            {
                return session.AccessToken;
            }

            _logger.LogInformation("Refreshing access token with server...");
            var refreshRequest = new RefreshRequest(session.RefreshToken, session.DeviceId);
            var response = await httpClient.PostAsJsonAsync($"{session.ServerUrl.TrimEnd('/')}/{ApiRoutes.Refresh}", refreshRequest, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Token refresh failed with status code {StatusCode}. Clearing session.", response.StatusCode);
                await ClearSessionAsync(ct);
                return null;
            }

            var loginResponse = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: ct);
            if (loginResponse == null)
            {
                await ClearSessionAsync(ct);
                return null;
            }

            session.AccessToken = loginResponse.AccessToken;
            session.AccessTokenExpiresUtc = loginResponse.AccessTokenExpiresUtc;
            session.RefreshToken = loginResponse.RefreshToken;
            session.RefreshTokenExpiresUtc = loginResponse.RefreshTokenExpiresUtc;

            await SaveSessionAsync(session, ct);
            return session.AccessToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during token refresh.");
            return null;
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}

