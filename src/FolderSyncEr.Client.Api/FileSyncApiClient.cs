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

    private static string? TryExtractProblemDetail(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("detail", out var detailProp) &&
                    detailProp.ValueKind == JsonValueKind.String)
                {
                    return detailProp.GetString();
                }

                if (doc.RootElement.TryGetProperty("title", out var titleProp) &&
                    titleProp.ValueKind == JsonValueKind.String)
                {
                    return titleProp.GetString();
                }
            }
        }
        catch
        {
            // Ignore malformed JSON
        }

        return null;
    }

    public async Task<ApiConnectionStatus> CheckConnectionAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var uri = BuildUri(ApiRoutes.Status);
        try
        {
            using var response = await _httpClient.GetAsync(uri, ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errMsg = $"Server returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                _logger.LogWarning("Server connection failed ({Url}): {Error}", uri, errMsg);
                return new ApiConnectionStatus(false, "Unreachable", "Unknown", null, sw.ElapsedMilliseconds, errMsg);
            }

            var status = await response.Content.ReadFromJsonAsync<StatusResponse>(cancellationToken: ct);
            if (status == null)
            {
                _logger.LogWarning("Server connection failed ({Url}): Failed to deserialize status response.", uri);
                return new ApiConnectionStatus(false, "InvalidResponse", "Unknown", null, sw.ElapsedMilliseconds, "Failed to deserialize status response.");
            }

            if (status.DbReachable)
            {
                _logger.LogInformation("Server connection connected: {Url} (Status: {Status}, Version: {Version}, Latency: {LatencyMs}ms)",
                    uri, status.Status, status.Version, sw.ElapsedMilliseconds);
            }
            else
            {
                _logger.LogWarning("Server connection failed ({Url}): Database unreachable on server", uri);
            }

            return new ApiConnectionStatus(
                IsConnected: status.DbReachable,
                Status: status.Status,
                Version: status.Version,
                ServerTimeUtc: status.ServerTimeUtc,
                LatencyMs: sw.ElapsedMilliseconds,
                ErrorMessage: status.DbReachable ? null : "Database unreachable on server",
                CompiledUtc: status.CompiledUtc
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning("Server connection failed ({Url}): {Error}", uri, ex.Message);
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
            var rawBody = await response.Content.ReadAsStringAsync(ct);
            var detail = TryExtractProblemDetail(rawBody);
            var message = !string.IsNullOrWhiteSpace(detail)
                ? $"Login failed: {detail}"
                : $"Login failed (HTTP {response.StatusCode}): {rawBody}";
            throw new HttpRequestException(message, null, response.StatusCode);
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

    public async Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(CancellationToken ct = default)
    {
        var uri = BuildUri(ApiRoutes.Devices);
        using var response = await _httpClient.GetAsync(uri, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<DeviceDto>>(cancellationToken: ct)) ?? new List<DeviceDto>();
    }

    public async Task<bool> RevokeDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        var uri = BuildUri($"{ApiRoutes.Devices}/{Uri.EscapeDataString(deviceId)}");
        using var response = await _httpClient.DeleteAsync(uri, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
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

        _logger.LogInformation("Uploading file to server: '{Path}' (URL: {Url})", path, uri);
        using var response = await _httpClient.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.OK)
        {
            var fileDto = await response.Content.ReadFromJsonAsync<FileDto>(cancellationToken: ct);
            _logger.LogInformation("File uploaded to server: '{Path}' (v{Version}, {Size} bytes, URL: {Url})",
                fileDto?.Path ?? path, fileDto?.Version ?? 1, fileDto?.Size ?? 0, uri);
            return new UploadResult(true, false, false, fileDto, null, null);
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var conflict = await response.Content.ReadFromJsonAsync<ConflictDto>(cancellationToken: ct);
            _logger.LogWarning("File upload conflict (409) for '{Path}' (URL: {Url})", path, uri);
            return new UploadResult(false, true, false, null, conflict, "409 Conflict");
        }

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("File upload rejected (422 Hash Mismatch) for '{Path}' (URL: {Url}): {Detail}", path, uri, detail);
            return new UploadResult(false, false, true, null, null, $"422 Hash Mismatch: {detail}");
        }

        var err = await response.Content.ReadAsStringAsync(ct);
        _logger.LogWarning("File upload failed (HTTP {StatusCode}) for '{Path}' (URL: {Url}): {Error}", (int)response.StatusCode, path, uri, err);
        return new UploadResult(false, false, false, null, null, $"HTTP {(int)response.StatusCode}: {err}");
    }

    public async Task<FileDto> CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        var uri = BuildUri(ApiRoutes.Directories);
        using var response = await _httpClient.PostAsJsonAsync(uri, new CreateDirectoryRequest(path), ct);
        response.EnsureSuccessStatusCode();
        var dto = (await response.Content.ReadFromJsonAsync<FileDto>(cancellationToken: ct))!;
        _logger.LogInformation("Folder created on server: '{Path}' (URL: {Url})", dto.Path, uri);
        return dto;
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
        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Item deleted on server: id='{Id}' (URL: {Url})", id, uri);
        }
        return response.IsSuccessStatusCode;
    }

    public async Task<FileDto?> MoveFileAsync(string fromPath, string toPath, string? ifMatchSha256 = null, CancellationToken ct = default)
    {
        var uri = BuildUri(ApiRoutes.Move);
        var req = new MoveFileRequest(fromPath, toPath, ifMatchSha256);
        using var response = await _httpClient.PostAsJsonAsync(uri, req, ct);
        if (!response.IsSuccessStatusCode) return null;
        var dto = await response.Content.ReadFromJsonAsync<FileDto>(cancellationToken: ct);
        _logger.LogInformation("Item moved/renamed on server: '{FromPath}' -> '{ToPath}' (URL: {Url})", fromPath, toPath, uri);
        return dto;
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

    public async Task<IReadOnlyList<DeletedItemDto>> ListDeletedItemsAsync(CancellationToken ct = default)
    {
        var uri = BuildUri(ApiRoutes.Trash);
        using var response = await _httpClient.GetAsync(uri, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<DeletedItemDto>>(cancellationToken: ct)) ?? new List<DeletedItemDto>();
    }

    public async Task<TrashActionResult<RestoreResponse>> RestoreDeletedItemAsync(string id, string? targetPath = null, CancellationToken ct = default)
    {
        var uri = BuildUri($"{ApiRoutes.Trash}/{Uri.EscapeDataString(id)}/restore");
        using var response = await _httpClient.PostAsJsonAsync(uri, new RestoreRequest(targetPath), ct);
        var result = await ToTrashActionResultAsync<RestoreResponse>(response, ct);
        if (result.IsSuccess && result.Value != null)
        {
            _logger.LogInformation("File/folder restored from trash: '{Path}' (id='{Id}', restoredCount={Count}, URL: {Url})",
                result.Value.Path, id, result.Value.RestoredCount, uri);
        }
        else
        {
            _logger.LogWarning("Failed to restore deleted item id='{Id}' (URL: {Url}): {Error}", id, uri, result.ErrorMessage);
        }
        return result;
    }

    public async Task<TrashActionResult<PurgeResponse>> PurgeDeletedItemAsync(string id, CancellationToken ct = default)
    {
        var uri = BuildUri($"{ApiRoutes.Trash}/{Uri.EscapeDataString(id)}");
        using var response = await _httpClient.DeleteAsync(uri, ct);
        return await ToTrashActionResultAsync<PurgeResponse>(response, ct);
    }

    public async Task<PurgeResponse> EmptyTrashAsync(CancellationToken ct = default)
    {
        var uri = BuildUri(ApiRoutes.Trash);
        using var response = await _httpClient.DeleteAsync(uri, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PurgeResponse>(cancellationToken: ct))!;
    }

    /// <summary>Minimal RFC 7807 problem document shape for reading server error details.</summary>
    private sealed record ProblemBody(string? Title, string? Detail);

    private static async Task<TrashActionResult<T>> ToTrashActionResultAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
            return new TrashActionResult<T>(true, false, false, value, null);
        }

        string? detail = null;
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(cancellationToken: ct);
            detail = problem?.Detail ?? problem?.Title;
        }
        catch
        {
            // Body was not a problem document
        }

        return new TrashActionResult<T>(
            false,
            response.StatusCode == System.Net.HttpStatusCode.Conflict,
            response.StatusCode == System.Net.HttpStatusCode.NotFound,
            default,
            detail ?? $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}");
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

