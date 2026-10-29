namespace FolderSyncEr.Server.Services;

public record TempUploadResult(
    string TempFilePath,
    long Size,
    string ComputedSha256
);

public interface IBlobStorageService
{
    string GetStorageRoot();
    string GetUserFolderName(string username);
    string GetBlobRelativePath(string username, string fileId, long version);
    Task<TempUploadResult> SaveStreamToTempAsync(string username, Stream inputStream, long maxSizeBytes, CancellationToken cancellationToken);
    Task<TempUploadResult> SaveStreamToTempAsync(int userId, Stream inputStream, long maxSizeBytes, CancellationToken cancellationToken);
    Task<string> CommitBlobAsync(string username, string fileId, long version, string tempFilePath, CancellationToken cancellationToken);
    Task<string> CommitBlobAsync(int userId, string fileId, long version, string tempFilePath, CancellationToken cancellationToken);
    /// <summary>Copies an existing blob to the storage slot for <paramref name="version"/> and returns its relative path, or null if the source is missing.</summary>
    Task<string?> CopyBlobAsync(string username, string fileId, long version, string sourceBlobRelativePath, CancellationToken cancellationToken);
    Task<string?> CopyBlobAsync(int userId, string fileId, long version, string sourceBlobRelativePath, CancellationToken cancellationToken);
    Stream? OpenRead(string blobRelativePath);
    Task DeleteBlobAsync(string blobRelativePath, CancellationToken cancellationToken);
    (long TotalSpace, long UsedSpace) GetDiskSpaceInfo();
    void CleanupTempFiles();
}

