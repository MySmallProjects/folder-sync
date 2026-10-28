using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FolderSyncEr.Contracts;
using Microsoft.Extensions.Logging;

namespace FolderSyncEr.Client.Api;

public class FileSyncApiClient : IFileSyncApi
{
    private readonly HttpClient _httpClient;
    private readonly AuthTokenManager _tokenManager;
    private readonly ILogger<FileSyncApiClient> _logger;
    private string _serverUrl = "http://localhost:5000";

    public string ServerUrl
    {
        get => _serverUrl;
        set => _serverUrl = value.TrimEnd('/');
    }

    public FileSyncApiClient(HttpClient httpClient, AuthTokenManager tokenManager, ILogger<FileSyncApiClient> logger)
    {
        _httpClient = httpClient;
        _tokenManager = tokenManager;
        _logger = logger;
    }

    private Uri BuildUri(string relativePath)
    {
        return new Uri($"{ServerUrl}/{relativePath.TrimStart('/')}");
    }

    public async Task<ApiConnectionStatus> CheckConnectionAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var uri = BuildUri(ApiRoutes.Status);
            using var response = await _httpClient.GetAsync(uri, ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new ApiConnectionStatus(false, "Unreachable", "Unknown", null, sw.ElapsedMilliseconds,
                    $"Server returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var status = await response.Content.ReadFromJsonAsync<StatusResponse>(cancellationToken: ct);
            if (status == null)
            {
                return new ApiConnectionStatus(false, "InvalidResponse", "Unknown", null, sw.ElapsedMilliseconds, "Failed to deserialize status response.");
            }

            return new ApiConnectionStatus(
                IsConnected: status.DbReachable,
                Status: status.Status,
                Version: status.Version,
                ServerTimeUtc: status.ServerTimeUtc,
                LatencyMs: sw.ElapsedMilliseconds,
                ErrorMessage: status.DbReachable ? null : "Database unreachable on server"
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ApiConnectionStatus(false, "Offline", "Unknown", null, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    public async Task<LoginResponse> LoginAsync(string username, string password, string deviceName, CancellationToken ct = default)
    {
        var deviceId = await _tokenManager.GetOrCreateDeviceIdAsync(ct);
        var request = new LoginRequest(username, password, deviceId, deviceName);
        var uri = BuildUri(ApiRoutes.Login);

        using var response = await _httpClient.PostAsJsonAsync(uri, request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Login failed (HTTP {response.StatusCode}): {error}", null, response.StatusCode);
        }

        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty login response from server.");

        var session = new AuthSession
        {
            ServerUrl = ServerUrl,
            DeviceId = deviceId,
            Username = login.User.Username,
            Role = login.User.Role,
            AccessToken = login.AccessToken,
            AccessTokenExpiresUtc = login.AccessTokenExpiresUtc,
            RefreshToken = login.RefreshToken,
            RefreshTokenExpiresUtc = login.RefreshTokenExpiresUtc
        };

        await _tokenManager.SaveSessionAsync(session, ct);
        return login;
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        try
        {
            var uri = BuildUri(ApiRoutes.Logout);
            await _httpClient.PostAsync(uri, null, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to call logout on server.");
        }
        finally
        {
            await _tokenManager.ClearSessionAsync(ct);
        }
    }

    public async Task<UserDto?> GetMeAsync(CancellationToken ct = default)
    {
        var uri = BuildUri(ApiRoutes.Me);
        using var response = await _httpClient.GetAsync(uri, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UserDto>(cancellationToken: ct);
    }

    public async Task<FileListResponse> ListFilesAsync(
        long? sinceSeq = null,
        DateTime? since = null,
        bool includeDeleted = false,
        string? cursor = null,
        int limit = 1000,
        CancellationToken ct = default)
    {
        var queryParams = new List<string>();
        if (sinceSeq.HasValue) queryParams.Add($"sinceSeq={sinceSeq.Value}");
        if (since.HasValue) queryParams.Add($"since={Uri.EscapeDataString(since.Value.ToString("o"))}");
        if (includeDeleted) queryParams.Add("includeDeleted=true");
        if (!string.IsNullOrEmpty(cursor)) queryParams.Add($"cursor={Uri.EscapeDataString(cursor)}");
        queryParams.Add($"limit={limit}");

        var queryString = string.Join("&", queryParams);
        var uri = BuildUri($"{ApiRoutes.Files}?{queryString}");

        using var response = await _httpClient.GetAsync(uri, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<FileListResponse>(cancellationToken: ct)
            ?? new FileListResponse([], null, 0, DateTime.UtcNow);
    }

    public async Task<FileDto?> GetFileMetaByPathAsync(string path, CancellationToken ct = default)
    {
        var uri = BuildUri($"{ApiRoutes.FileMeta}?path={Uri.EscapeDataString(path)}");
        using var response = await _httpClient.GetAsync(uri, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FileDto>(cancellationToken: ct);
    }

    public async Task<FileDto?> GetFileMetaByIdAsync(string id, CancellationToken ct = default)
    {
        var uri = BuildUri($"api/files/{Uri.EscapeDataString(id)}");
        using var response = await _httpClient.GetAsync(uri, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FileDto>(cancellationToken: ct);
    }

    public async Task<Stream> DownloadFileStreamAsync(string id, (long from, long? to)? range = null, CancellationToken ct = default)
    {
        var uri = BuildUri($"api/files/{Uri.EscapeDataString(id)}/content");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        if (range.HasValue)
        {
            request.Headers.Range = new RangeHeaderValue(range.Value.from, range.Value.to);
        }

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStreamAsync(ct);
    }

    public async Task<UploadResult> UploadFileStreamAsync(
        string path,
        Stream contentStream,
        string? sha256Hex = null,
        string? ifMatchSha256 = null,
        string? ifNoneMatch = null,
        long? baseVersion = null,
        DateTime? lastModifiedUtc = null,
        CancellationToken ct = default)
    {
        var uri = BuildUri($"{ApiRoutes.Files}?path={Uri.EscapeDataString(path)}");
        using var request = new HttpRequestMessage(HttpMethod.Put, uri);
        request.Content = new StreamContent(contentStream);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        if (!string.IsNullOrEmpty(sha256Hex))
        {
            request.Headers.Add(ApiHeaders.ContentSha256, sha256Hex);
        }

        if (!string.IsNullOrEmpty(ifMatchSha256))
        {
            request.Headers.Add(ApiHeaders.IfMatch, $"\"{ifMatchSha256.Trim('\"')}\"");
        }

        if (!string.IsNullOrEmpty(ifNoneMatch))
        {
            request.Headers.Add(ApiHeaders.IfNoneMatch, ifNoneMatch);
        }

        if (baseVersion.HasValue)
        {
            request.Headers.Add(ApiHeaders.BaseVersion, baseVersion.Value.ToString());
        }

        if (lastModifiedUtc.HasValue)
        {
            request.Headers.Add(ApiHeaders.LastModifiedUtc, lastModifiedUtc.Value.ToString("o"));
        }

        using var response = await _httpClient.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.OK)
        {
            var fileDto = await response.Content.ReadFromJsonAsync<FileDto>(cancellationToken: ct);
            return new UploadResult(true, false, false, fileDto, null, null);
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var conflict = await response.Content.ReadFromJsonAsync<ConflictDto>(cancellationToken: ct);
            return new UploadResult(false, true, false, null, conflict, "409 Conflict");
        }

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            return new UploadResult(false, false, true, null, null, $"422 Hash Mismatch: {detail}");
        }

        var err = await response.Content.ReadAsStringAsync(ct);
        return new UploadResult(false, false, false, null, null, $"HTTP {(int)response.StatusCode}: {err}");
    }

    public async Task<FileDto> CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        var uri = BuildUri(ApiRoutes.Directories);
        using var response = await _httpClient.PostAsJsonAsync(uri, new CreateDirectoryRequest(path), ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FileDto>(cancellationToken: ct))!;
    }

    public async Task<bool> DeleteFileAsync(string id, string? ifMatchSha256 = null, long? baseVersion = null, CancellationToken ct = default)
    {
        var uri = BuildUri($"api/files/{Uri.EscapeDataString(id)}");
        using var request = new HttpRequestMessage(HttpMethod.Delete, uri);

        if (!string.IsNullOrEmpty(ifMatchSha256))
        {
            request.Headers.Add(ApiHeaders.IfMatch, $"\"{ifMatchSha256.Trim('\"')}\"");
        }

        if (baseVersion.HasValue)
        {
            request.Headers.Add(ApiHeaders.BaseVersion, baseVersion.Value.ToString());
        }

        using var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<FileDto?> MoveFileAsync(string fromPath, string toPath, string? ifMatchSha256 = null, CancellationToken ct = default)
    {
        var uri = BuildUri(ApiRoutes.Move);
        var req = new MoveFileRequest(fromPath, toPath, ifMatchSha256);
        using var response = await _httpClient.PostAsJsonAsync(uri, req, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<FileDto>(cancellationToken: ct);
    }

    public async Task<StorageInfoDto> GetStorageInfoAsync(CancellationToken ct = default)
    {
        var uri = BuildUri(ApiRoutes.Storage);
        using var response = await _httpClient.GetAsync(uri, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StorageInfoDto>(cancellationToken: ct))!;
    }

    public async Task<Stream> ExportFolderZipAsync(CancellationToken ct = default)
    {
        var uri = BuildUri("api/files/export");
        var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }

    public async Task<PagedResult<ActivityLogDto>> GetLogsAsync(
        int page = 1,
        int pageSize = 50,
        string? action = null,
        string? result = null,
        string? path = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken ct = default)
    {
        var queryParams = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (!string.IsNullOrEmpty(action)) queryParams.Add($"action={Uri.EscapeDataString(action)}");
        if (!string.IsNullOrEmpty(result)) queryParams.Add($"result={Uri.EscapeDataString(result)}");
        if (!string.IsNullOrEmpty(path)) queryParams.Add($"path={Uri.EscapeDataString(path)}");
        if (fromUtc.HasValue) queryParams.Add($"fromUtc={Uri.EscapeDataString(fromUtc.Value.ToString("o"))}");
        if (toUtc.HasValue) queryParams.Add($"toUtc={Uri.EscapeDataString(toUtc.Value.ToString("o"))}");

        var uri = BuildUri($"{ApiRoutes.Logs}?{string.Join("&", queryParams)}");
        using var response = await _httpClient.GetAsync(uri, ct);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<PagedResult<ActivityLogDto>>(cancellationToken: ct))!;
    }
}

