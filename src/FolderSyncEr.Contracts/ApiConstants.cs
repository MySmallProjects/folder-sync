namespace FolderSyncEr.Contracts;

public static class ApiHeaders
{
    public const string ContentSha256 = "X-Content-SHA256";
    public const string LastModifiedUtc = "X-Last-Modified-Utc";
    public const string BaseVersion = "X-Base-Version";
    public const string IfMatch = "If-Match";
    public const string IfNoneMatch = "If-None-Match";
    public const string ETag = "ETag";
}

public static class ApiRoutes
{
    public const string Status = "api/status";

    public const string Login = "api/auth/login";
    public const string Refresh = "api/auth/refresh";
    public const string Logout = "api/auth/logout";
    public const string Me = "api/auth/me";

    public const string Files = "api/files";
    public const string FileMeta = "api/files/meta";
    public const string FileById = "api/files/{id}";
    public const string FileContent = "api/files/{id}/content";
    public const string Directories = "api/directories";
    public const string Move = "api/files/move";
    public const string Storage = "api/storage";

    public const string Logs = "api/logs";
    public const string ClientLogs = "api/logs/client";

    public const string Devices = "api/devices";
    public const string DeviceById = "api/devices/{id}";

    public const string Settings = "api/settings";
}

