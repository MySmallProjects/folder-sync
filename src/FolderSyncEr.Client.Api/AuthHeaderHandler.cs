using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderSyncEr.Client.Api;

public class AuthHeaderHandler : DelegatingHandler
{
    private readonly AuthTokenManager _tokenManager;
    private readonly ILogger<AuthHeaderHandler> _logger;

    public AuthHeaderHandler(AuthTokenManager tokenManager, ILogger<AuthHeaderHandler>? logger = null)
    {
        _tokenManager = tokenManager;
        _logger = logger ?? NullLogger<AuthHeaderHandler>.Instance;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var method = request.Method.Method;
        var url = request.RequestUri?.ToString() ?? string.Empty;

        // Don't attach auth header to public endpoints
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        if (path.EndsWith("/api/status", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/api/auth/refresh", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("API Request: {Method} {Url}", method, url);
            try
            {
                var publicResp = await base.SendAsync(request, cancellationToken);
                _logger.LogInformation("API Response: {Method} {Url} -> HTTP {StatusCode}", method, url, (int)publicResp.StatusCode);
                return publicResp;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("API Error: {Method} {Url} -> {Error}", method, url, ex.Message);
                throw;
            }
        }

        var serverBaseUrl = request.RequestUri?.GetLeftPart(UriPartial.Authority);
        using var refreshClient = InnerHandler != null
            ? new HttpClient(InnerHandler, disposeHandler: false)
            : new HttpClient();

        var token = await _tokenManager.GetValidAccessTokenAsync(
            refreshClient,
            cancellationToken,
            forceRefresh: false,
            serverUrlOverride: serverBaseUrl);

        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else if (_tokenManager.CurrentSession != null)
        {
            // A session exists on disk, but token refresh could not reach the server.
            // Fail fast with HttpRequestException rather than making a second unauthenticated socket call.
            _logger.LogWarning("API Aborted (server unreachable during token refresh): {Method} {Url}", method, url);
            throw new HttpRequestException($"Server at {serverBaseUrl} is unreachable (unable to refresh access token).");
        }

        _logger.LogInformation("API Request: {Method} {Url}", method, url);
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
            _logger.LogInformation("API Response: {Method} {Url} -> HTTP {StatusCode}", method, url, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("API Error: {Method} {Url} -> {Error}", method, url, ex.Message);
            throw;
        }

        // If 401, force a single-flight refresh against the server and replay if a new token was obtained
        if (response.StatusCode == HttpStatusCode.Unauthorized && !string.IsNullOrEmpty(token))
        {
            var refreshedToken = await _tokenManager.GetValidAccessTokenAsync(
                refreshClient,
                cancellationToken,
                forceRefresh: true,
                serverUrlOverride: serverBaseUrl);

            if (!string.IsNullOrEmpty(refreshedToken) && refreshedToken != token)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshedToken);
                _logger.LogInformation("API Replay after token refresh: {Method} {Url}", method, url);
                var replayResponse = await base.SendAsync(request, cancellationToken);
                _logger.LogInformation("API Replay Response: {Method} {Url} -> HTTP {StatusCode}", method, url, (int)replayResponse.StatusCode);
                return replayResponse;
            }
        }

        return response;
    }
}
