using System.Security.Cryptography;

namespace FolderSyncEr.Server.Services;

public class BlobStorageService : IBlobStorageService
{
    private readonly string _storageRoot;
    private readonly ILogger<BlobStorageService> _logger;

    public BlobStorageService(IConfiguration configuration, ILogger<BlobStorageService> logger)
    {
        _logger = logger;
        var configuredRoot = configuration["Storage:RootPath"];
        _storageRoot = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(AppContext.BaseDirectory, "storage")
            : Path.GetFullPath(configuredRoot);

        Directory.CreateDirectory(_storageRoot);
    }

    public string GetStorageRoot() => _storageRoot;

    public async Task<TempUploadResult> SaveStreamToTempAsync(int userId, Stream inputStream, long maxSizeBytes, CancellationToken cancellationToken)
    {
        var userTmpDir = Path.Combine(_storageRoot, userId.ToString(), ".tmp");
        Directory.CreateDirectory(userTmpDir);

        var tempFilePath = Path.Combine(userTmpDir, $"{Guid.NewGuid():N}.tmp");
        long totalBytes = 0;

        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using (var fileStream = new FileStream(tempFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int bytesRead;

                while ((bytesRead = await inputStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > maxSizeBytes)
                    {
                        throw new InvalidOperationException($"Upload exceeded maximum allowed size of {maxSizeBytes} bytes.");
                    }

                    sha256.AppendData(buffer, 0, bytesRead);
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                }
            }

            var hashBytes = sha256.GetHashAndReset();
            var computedSha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();

            return new TempUploadResult(tempFilePath, totalBytes, computedSha256);
        }
        catch
        {
            if (File.Exists(tempFilePath))
            {
                try { File.Delete(tempFilePath); } catch { /* ignore */ }
            }
            throw;
        }
    }

    public Task<string> CommitBlobAsync(int userId, string fileId, long version, string tempFilePath, CancellationToken cancellationToken)
    {
        var prefix = fileId.Length >= 2 ? fileId[..2] : "00";
        var relativeDir = Path.Combine(userId.ToString(), prefix, fileId);
        var targetDir = Path.Combine(_storageRoot, relativeDir);
        Directory.CreateDirectory(targetDir);

        var blobFileName = $"{version}.blob";
        var targetFilePath = Path.Combine(targetDir, blobFileName);
        var relativeBlobPath = Path.Combine(relativeDir, blobFileName);

        File.Move(tempFilePath, targetFilePath, overwrite: true);

        return Task.FromResult(relativeBlobPath);
    }

    public async Task<string?> CopyBlobAsync(int userId, string fileId, long version, string sourceBlobRelativePath, CancellationToken cancellationToken)
    {
        var sourceFullPath = Path.Combine(_storageRoot, sourceBlobRelativePath);
        if (!File.Exists(sourceFullPath))
        {
            return null;
        }

        var prefix = fileId.Length >= 2 ? fileId[..2] : "00";
        var relativeDir = Path.Combine(userId.ToString(), prefix, fileId);
        Directory.CreateDirectory(Path.Combine(_storageRoot, relativeDir));

        var relativeBlobPath = Path.Combine(relativeDir, $"{version}.blob");
        var targetFullPath = Path.Combine(_storageRoot, relativeBlobPath);

        // Copy via a temp name so a partially written blob is never served
        var tempTarget = targetFullPath + ".copying";
        await using (var src = new FileStream(sourceFullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        await using (var dst = new FileStream(tempTarget, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await src.CopyToAsync(dst, cancellationToken);
        }
        File.Move(tempTarget, targetFullPath, overwrite: true);

        return relativeBlobPath;
    }

    public Stream? OpenRead(string blobRelativePath)
    {
        var fullPath = Path.Combine(_storageRoot, blobRelativePath);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
    }

    public Task DeleteBlobAsync(string blobRelativePath, CancellationToken cancellationToken)
    {
        var fullPath = Path.Combine(_storageRoot, blobRelativePath);
        if (File.Exists(fullPath))
        {
            try
            {
                File.Delete(fullPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete blob file at {Path}", fullPath);
            }
        }
        return Task.CompletedTask;
    }

    public (long TotalSpace, long UsedSpace) GetDiskSpaceInfo()
    {
        try
        {
            var driveInfo = new DriveInfo(Path.GetPathRoot(_storageRoot) ?? "/");
            var total = driveInfo.TotalSize;
            var free = driveInfo.AvailableFreeSpace;
            return (total, Math.Max(0, total - free));
        }
        catch
        {
            return (1_000_000_000_000, 100_000_000);
        }
    }

    public void CleanupTempFiles()
    {
        try
        {
            if (!Directory.Exists(_storageRoot)) return;

            foreach (var userDir in Directory.EnumerateDirectories(_storageRoot))
            {
                var tmpDir = Path.Combine(userDir, ".tmp");
                if (Directory.Exists(tmpDir))
                {
                    foreach (var file in Directory.EnumerateFiles(tmpDir, "*.tmp"))
                    {
                        try
                        {
                            var fileInfo = new FileInfo(file);
                            // Delete temp files older than 1 hour
                            if (DateTime.UtcNow - fileInfo.CreationTimeUtc > TimeSpan.FromHours(1))
                            {
                                fileInfo.Delete();
                            }
                        }
                        catch { /* best effort */ }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sweep leftover temp files");
        }
    }
}

