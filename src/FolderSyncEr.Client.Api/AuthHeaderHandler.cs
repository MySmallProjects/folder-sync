using System.Net;
using System.Net.Http.Headers;

namespace FolderSyncEr.Client.Api;

public class AuthHeaderHandler : DelegatingHandler
{
    private readonly AuthTokenManager _tokenManager;

    public AuthHeaderHandler(AuthTokenManager tokenManager)
    {
        _tokenManager = tokenManager;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Don't attach auth header to public endpoints
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        if (path.EndsWith("/api/status", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/api/auth/refresh", StringComparison.OrdinalIgnoreCase))
        {
            return await base.SendAsync(request, cancellationToken);
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
            throw new HttpRequestException($"Server at {serverBaseUrl} is unreachable (unable to refresh access token).");
        }

        var response = await base.SendAsync(request, cancellationToken);

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
                return await base.SendAsync(request, cancellationToken);
            }
        }

        return response;
    }
}
