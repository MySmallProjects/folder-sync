using FolderSyncEr.Contracts;

namespace FolderSyncEr.Client.Api;

public record ApiConnectionStatus(
    bool IsConnected,
    string Status,
    string Version,
    DateTime? ServerTimeUtc,
    long LatencyMs,
    string? ErrorMessage,
    DateTime? CompiledUtc = null
);

public record UploadResult(
    bool IsSuccess,
    bool IsConflict,
    bool IsHashMismatch,
    FileDto? File,
    ConflictDto? Conflict,
    string? ErrorMessage
);

public interface IFileSyncApi
{
    string ServerUrl { get; set; }

    Task<ApiConnectionStatus> CheckConnectionAsync(CancellationToken ct = default);
    Task<LoginResponse> LoginAsync(string username, string password, string deviceName, CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);
    Task<UserDto?> GetMeAsync(CancellationToken ct = default);

    Task<FileListResponse> ListFilesAsync(
        long? sinceSeq = null,
        DateTime? since = null,
        bool includeDeleted = false,
        string? cursor = null,
        int limit = 1000,
        CancellationToken ct = default);

    Task<FileDto?> GetFileMetaByPathAsync(string path, CancellationToken ct = default);
    Task<FileDto?> GetFileMetaByIdAsync(string id, CancellationToken ct = default);
    Task<Stream> DownloadFileStreamAsync(string id, (long from, long? to)? range = null, CancellationToken ct = default);

    Task<UploadResult> UploadFileStreamAsync(
        string path,
        Stream contentStream,
        string? sha256Hex = null,
        string? ifMatchSha256 = null,
        string? ifNoneMatch = null,
        long? baseVersion = null,
        DateTime? lastModifiedUtc = null,
        CancellationToken ct = default);

    Task<FileDto> CreateDirectoryAsync(string path, CancellationToken ct = default);
    Task<bool> DeleteFileAsync(string id, string? ifMatchSha256 = null, long? baseVersion = null, CancellationToken ct = default);
    Task<FileDto?> MoveFileAsync(string fromPath, string toPath, string? ifMatchSha256 = null, CancellationToken ct = default);
    Task<StorageInfoDto> GetStorageInfoAsync(CancellationToken ct = default);
    Task<Stream> ExportFolderZipAsync(CancellationToken ct = default);
    Task<PagedResult<ActivityLogDto>> GetLogsAsync(
        int page = 1,
        int pageSize = 50,
        string? action = null,
        string? result = null,
        string? path = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken ct = default);
}

