namespace FolderSyncEr.Server.Services;

public record TempUploadResult(
    string TempFilePath,
    long Size,
    string ComputedSha256
);

public interface IBlobStorageService
{
    string GetStorageRoot();
    Task<TempUploadResult> SaveStreamToTempAsync(int userId, Stream inputStream, long maxSizeBytes, CancellationToken cancellationToken);
    Task<string> CommitBlobAsync(int userId, string fileId, long version, string tempFilePath, CancellationToken cancellationToken);
    Stream? OpenRead(string blobRelativePath);
    Task DeleteBlobAsync(string blobRelativePath, CancellationToken cancellationToken);
    (long TotalSpace, long UsedSpace) GetDiskSpaceInfo();
    void CleanupTempFiles();
}

