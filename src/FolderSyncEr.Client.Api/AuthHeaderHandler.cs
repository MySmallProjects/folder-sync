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

        using var refreshClient = new HttpClient();
        var token = await _tokenManager.GetValidAccessTokenAsync(refreshClient, cancellationToken);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await base.SendAsync(request, cancellationToken);

        // If 401, try to refresh once and replay
        if (response.StatusCode == HttpStatusCode.Unauthorized && !string.IsNullOrEmpty(token))
        {
            var refreshedToken = await _tokenManager.GetValidAccessTokenAsync(refreshClient, cancellationToken);
            if (!string.IsNullOrEmpty(refreshedToken) && refreshedToken != token)
            {
                // Clone request for replay if content is replayable
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshedToken);
                return await base.SendAsync(request, cancellationToken);
            }
        }

        return response;
    }
}

