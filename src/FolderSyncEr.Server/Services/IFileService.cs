using FolderSyncEr.Contracts;
using FolderSyncEr.Server.Data;

namespace FolderSyncEr.Server.Services;

public record UploadFileCommand(
    int UserId,
    string RawPath,
    Stream InputStream,
    string? ProvidedSha256,
    string? IfMatchSha256,
    string? IfNoneMatch,
    long? BaseVersion,
    DateTime? LastModifiedUtc,
    string? DeviceId
);

public record UploadFileResult(
    bool IsSuccess,
    bool IsConflict,
    bool IsHashMismatch,
    bool IsBadRequest,
    string? ErrorMessage,
    FileDto? File,
    ConflictDto? Conflict
);

public record DeleteFileResult(
    bool IsSuccess,
    bool IsNotFound,
    bool IsConflict,
    string? ErrorMessage,
    ConflictDto? Conflict
);

public interface IFileService
{
    Task<FileListResponse> ListFilesAsync(int userId, long? sinceSeq, DateTime? sinceUtc, bool includeDeleted, string? cursor, int limit, CancellationToken cancellationToken);
    Task<FileRecord?> GetFileByIdAsync(int userId, string fileId, CancellationToken cancellationToken);
    Task<FileRecord?> GetFileByPathAsync(int userId, string path, CancellationToken cancellationToken);
    Task<UploadFileResult> UploadFileAsync(UploadFileCommand command, CancellationToken cancellationToken);
    Task<FileRecord> CreateDirectoryAsync(int userId, string rawPath, string? deviceId, CancellationToken cancellationToken);
    Task<DeleteFileResult> DeleteFileAsync(int userId, string fileId, string? ifMatchSha256, long? baseVersion, string? deviceId, CancellationToken cancellationToken);
    Task<FileRecord?> MoveFileAsync(int userId, string fromPath, string toPath, string? ifMatchSha256, string? deviceId, CancellationToken cancellationToken);
    Task<FileRecord?> PatchLastModifiedAsync(int userId, string fileId, DateTime lastModifiedUtc, CancellationToken cancellationToken);
    Task<StorageInfoDto> GetStorageInfoAsync(int userId, CancellationToken cancellationToken);
}

