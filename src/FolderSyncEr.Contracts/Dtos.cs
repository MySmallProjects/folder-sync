namespace FolderSyncEr.Contracts;

public record StatusResponse(
    string Status,
    string Version,
    DateTime ServerTimeUtc,
    bool DbReachable,
    DateTime? CompiledUtc = null
);

public record LoginRequest(
    string Username,
    string Password,
    string DeviceId,
    string DeviceName
);

public record LoginResponse(
    string AccessToken,
    DateTime AccessTokenExpiresUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresUtc,
    UserDto User
);

public record RefreshRequest(
    string RefreshToken,
    string DeviceId
);

public record UserDto(
    int Id,
    string Username,
    string Role,
    DateTime CreatedUtc,
    DateTime? LastLoginUtc
);

public record AdminUserDto(
    int Id,
    string Username,
    string Role,
    DateTime CreatedUtc,
    DateTime? LastLoginUtc,
    bool IsDisabled,
    bool IsDeleted,
    DateTime? DeletedUtc
);

public record CreateUserRequest(
    string Username,
    string Password
);

public record UpdateUserStatusRequest(
    bool? IsDisabled,
    bool? IsDeleted
);

public record ChangeUserPasswordRequest(
    string NewPassword
);

public record DeviceDto(
    string Id,
    int UserId,
    string Name,
    DateTime CreatedUtc,
    DateTime LastSeenUtc,
    bool IsRevoked
);

public record FileDto(
    string Id,
    string Path,
    bool IsDirectory,
    long Size,
    string? Sha256,
    long Version,
    DateTime LastModifiedUtc,
    DateTime UploadedAtUtc,
    string? UploadedByDeviceId,
    bool IsDeleted,
    long ChangeSeq
);

public record FileListResponse(
    IReadOnlyList<FileDto> Items,
    string? NextCursor,
    long SnapshotSeq,
    DateTime ServerTimeUtc
);

public record FileVersionInfoDto(
    long? Version,
    string? Sha256,
    long Size,
    DateTime? LastModifiedUtc,
    DateTime? UploadedAtUtc,
    string? UploadedByDeviceId,
    bool? IsDeleted
);

public record ConflictDto(
    string Path,
    FileVersionInfoDto Client,
    FileVersionInfoDto Server
);

public record CreateDirectoryRequest(
    string Path
);

public record MoveFileRequest(
    string FromPath,
    string ToPath,
    string? IfMatchSha256
);

public record PatchFileRequest(
    DateTime LastModifiedUtc
);

/// <summary>
/// A top-level deleted (tombstoned) file or folder shown in the Deleted Files list.
/// Items whose parent folder is also deleted are folded into that folder
/// (see <see cref="ContainedItemCount"/> / <see cref="ContainedSize"/>).
/// </summary>
public record DeletedItemDto(
    string Id,
    string Path,
    bool IsDirectory,
    long Size,
    long Version,
    DateTime LastModifiedUtc,
    DateTime DeletedAtUtc,
    int ContainedItemCount,
    long ContainedSize
);

/// <summary>
/// Restore request. When <see cref="TargetPath"/> is set the item is restored under that
/// path instead of its original one (used for "restore as copy" when the original path is taken).
/// </summary>
public record RestoreRequest(
    string? TargetPath
);

public record RestoreResponse(
    string Path,
    int RestoredCount
);

public record PurgeResponse(
    int PurgedCount,
    long FreedBytes
);

public record StorageInfoDto(
    long TotalSpace,
    long UsedSpace,
    long? Quota
);

public record ActivityLogDto(
    long Id,
    DateTime TimestampUtc,
    int? UserId,
    string? DeviceId,
    string Action,
    string? Path,
    string Result,
    string? Message,
    string? CorrelationId
);

public record ClientActivityLogRequest(
    DateTime TimestampUtc,
    string Action,
    string? Path,
    string Result,
    string? Message
);

public record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount
);

public record ApiSettingDto(
    string Key,
    string Value,
    DateTime UpdatedUtc
);

public record UpdateSettingRequest(
    string Value
);

