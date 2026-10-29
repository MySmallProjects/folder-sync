using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FolderSyncEr.Client.Sync.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Oire.SharpSync.Core;
using Oire.SharpSync.Storage;

namespace FolderSyncEr.Client.Sync.Storage;

/// <summary>
/// Decorates local file storage with:
/// 1. Atomic writes (temp-then-move) to prevent partial files (Mitigating Risk R3).
/// 2. Trash bin retention (.foldersyncer/trash/) instead of hard deletes (Mitigating Risk R4).
/// 3. In-memory hash caching (path + size + mtime) to avoid re-reading unchanged files (Mitigating Risk R9).
/// 4. Failure registration on locked or inaccessible files (Mitigating Risk R1).
/// </summary>
public sealed class SafeLocalStorage : ISyncStorage
{
    private readonly string _rootPath;
    private readonly ISyncStorage _inner;
    private readonly ILogger<SafeLocalStorage> _logger;
    private readonly RunFailureRegistry? _failureRegistry;
    private readonly ConcurrentDictionary<string, HashCacheEntry> _hashCache = new(StringComparer.OrdinalIgnoreCase);

    private record HashCacheEntry(long Size, DateTime LastModifiedUtc, string Base64Hash);

    public SafeLocalStorage(string rootPath, ILogger<SafeLocalStorage>? logger = null, RunFailureRegistry? failureRegistry = null)
    {
        _rootPath = Path.GetFullPath(rootPath ?? throw new ArgumentNullException(nameof(rootPath)));
        _logger = logger ?? NullLogger<SafeLocalStorage>.Instance;
        _failureRegistry = failureRegistry;
        _inner = new LocalFileStorage(_rootPath, _logger);
        _inner.ProgressChanged += (s, e) => ProgressChanged?.Invoke(this, e);
    }

    public SafeLocalStorage(ISyncStorage innerStorage, string rootPath, ILogger<SafeLocalStorage>? logger = null, RunFailureRegistry? failureRegistry = null)
    {
        _rootPath = Path.GetFullPath(rootPath ?? throw new ArgumentNullException(nameof(rootPath)));
        _inner = innerStorage ?? throw new ArgumentNullException(nameof(innerStorage));
        _logger = logger ?? NullLogger<SafeLocalStorage>.Instance;
        _failureRegistry = failureRegistry;
        _inner.ProgressChanged += (s, e) => ProgressChanged?.Invoke(this, e);
    }

    public StorageType StorageType => _inner.StorageType;

    public string RootPath => _rootPath;

    public event EventHandler<StorageProgressEventArgs>? ProgressChanged;

    public Task<IEnumerable<SyncItem>> ListItemsAsync(string path, CancellationToken cancellationToken)
    {
        return _inner.ListItemsAsync(path, cancellationToken);
    }

    public Task<SyncItem?> GetItemAsync(string path, CancellationToken cancellationToken = default)
    {
        return _inner.GetItemAsync(path, cancellationToken);
    }

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
    {
        return _inner.ExistsAsync(path, cancellationToken);
    }

    public Task<Stream> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.Combine(_rootPath, path.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            // Open with FileShare.ReadWrite to allow reading even when another process has the file open
            var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
            return Task.FromResult<Stream>(stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read local file '{Path}': {Message}", path, ex.Message);
            _failureRegistry?.RecordFailure(path, ex);
            throw;
        }
    }

    public async Task WriteFileAsync(string path, Stream content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fullDestPath = Path.Combine(_rootPath, path.Replace('/', Path.DirectorySeparatorChar));
        var destDir = Path.GetDirectoryName(fullDestPath)!;
        if (!Directory.Exists(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        // Write atomically via a temp file inside .foldersyncer/tmp/
        var tempDir = Path.Combine(_rootPath, ".foldersyncer", "tmp");
        if (!Directory.Exists(tempDir))
        {
            Directory.CreateDirectory(tempDir);
        }

        var tempFilePath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.tmp");

        try
        {
            using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var totalBytes = content.CanSeek ? content.Length : -1;
                var progressStream = new ProgressTrackingStream(
                    fileStream,
                    path,
                    totalBytes,
                    StorageOperation.Download,
                    args => ProgressChanged?.Invoke(this, args));

                await content.CopyToAsync(progressStream, 81920, cancellationToken);
                await progressStream.FlushAsync(cancellationToken);
            }

            // Atomic move/replace
            var existedBefore = File.Exists(fullDestPath);
            if (existedBefore)
            {
                File.Move(tempFilePath, fullDestPath, overwrite: true);
                _logger.LogInformation("Local file updated from server: '{Path}'", path);
            }
            else
            {
                File.Move(tempFilePath, fullDestPath);
                _logger.LogInformation("Local file created from server: '{Path}'", path);
            }

            // Invalidate hash cache for this path
            _hashCache.TryRemove(path, out _);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write local file '{Path}': {Message}", path, ex.Message);
            _failureRegistry?.RecordFailure(path, ex);
            if (File.Exists(tempFilePath))
            {
                try { File.Delete(tempFilePath); } catch { /* ignore cleanup errors */ }
            }
            throw;
        }
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.Combine(_rootPath, path.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (File.Exists(fullPath))
            {
                // Move to trash folder instead of permanent delete
                MoveToTrash(fullPath, path);
                _hashCache.TryRemove(path, out _);
            }
            else if (Directory.Exists(fullPath))
            {
                // For directories, also safely move to trash
                MoveToTrash(fullPath, path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to safely delete local path '{Path}': {Message}", path, ex.Message);
            _failureRegistry?.RecordFailure(path, ex);
            throw;
        }

        return Task.CompletedTask;
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        await _inner.CreateDirectoryAsync(path, cancellationToken);
        _logger.LogInformation("Local folder created: '{Path}'", path);
    }

    public async Task MoveAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        _hashCache.TryRemove(sourcePath, out _);
        _hashCache.TryRemove(targetPath, out _);
        await _inner.MoveAsync(sourcePath, targetPath, cancellationToken);
        _logger.LogInformation("Local file/folder moved: '{SourcePath}' -> '{TargetPath}'", sourcePath, targetPath);
    }

    public async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.Combine(_rootPath, path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            return string.Empty;
        }

        var fileInfo = new FileInfo(fullPath);
        var mtimeUtc = fileInfo.LastWriteTimeUtc;
        var size = fileInfo.Length;

        if (_hashCache.TryGetValue(path, out var entry))
        {
            if (entry.Size == size && entry.LastModifiedUtc == mtimeUtc)
            {
                return entry.Base64Hash;
            }
        }

        // Compute hash and cache it
        try
        {
            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true))
            using (var sha256 = SHA256.Create())
            {
                var hashBytes = await sha256.ComputeHashAsync(stream, cancellationToken);
                var base64 = Convert.ToBase64String(hashBytes);
                _hashCache[path] = new HashCacheEntry(size, mtimeUtc, base64);
                return base64;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute hash for local file '{Path}': {Message}", path, ex.Message);
            _failureRegistry?.RecordFailure(path, ex);
            throw;
        }
    }

    public Task<StorageInfo> GetStorageInfoAsync(CancellationToken cancellationToken)
    {
        return _inner.GetStorageInfoAsync(cancellationToken);
    }

    public Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        return _inner.TestConnectionAsync(cancellationToken);
    }

    public Task SetLastModifiedAsync(string path, DateTime lastModified, CancellationToken cancellationToken)
    {
        _hashCache.TryRemove(path, out _);
        return _inner.SetLastModifiedAsync(path, lastModified, cancellationToken);
    }

    public Task SetPermissionsAsync(string path, string permissions, CancellationToken cancellationToken)
    {
        return _inner.SetPermissionsAsync(path, permissions, cancellationToken);
    }

    public Task<IReadOnlyList<ChangeInfo>> GetRemoteChangesAsync(DateTime since, CancellationToken cancellationToken)
    {
        return _inner.GetRemoteChangesAsync(since, cancellationToken);
    }

    private void MoveToTrash(string sourceFullPath, string relativePath)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var trashDir = Path.Combine(_rootPath, ".foldersyncer", "trash", timestamp);
        var targetPath = Path.Combine(trashDir, relativePath.Replace('/', Path.DirectorySeparatorChar));

        var parent = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (File.Exists(sourceFullPath))
        {
            File.Move(sourceFullPath, targetPath, overwrite: true);
            _logger.LogInformation("Moved local file to trash: '{Path}' -> '{TrashPath}'", relativePath, targetPath);

            // Clean up any ancestor directories inside the sync root that became empty
            // (e.g., when an implicit remote directory's last file was moved/deleted on the server).
            PruneEmptyParentDirectories(Path.GetDirectoryName(sourceFullPath));
        }
        else if (Directory.Exists(sourceFullPath))
        {
            if (Directory.Exists(targetPath))
            {
                // Target directory in trash was already created when moving child files in this sync run.
                // Merge any remaining contents into the trash directory and remove the source directory.
                MergeDirectoryIntoTrash(sourceFullPath, targetPath);
                Directory.Delete(sourceFullPath, recursive: true);
            }
            else
            {
                Directory.Move(sourceFullPath, targetPath);
            }
            _logger.LogInformation("Moved local directory to trash: '{Path}' -> '{TrashPath}'", relativePath, targetPath);
            PruneEmptyParentDirectories(Path.GetDirectoryName(sourceFullPath));
        }
    }

    private void PruneEmptyParentDirectories(string? directoryPath)
    {
        var rootNormalized = Path.TrimEndingDirectorySeparator(_rootPath);
        var current = directoryPath;

        while (!string.IsNullOrEmpty(current))
        {
            var currentNormalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
            if (string.Equals(currentNormalized, rootNormalized, StringComparison.OrdinalIgnoreCase) ||
                !currentNormalized.StartsWith(rootNormalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (!Directory.Exists(currentNormalized))
            {
                current = Path.GetDirectoryName(currentNormalized);
                continue;
            }

            if (Directory.EnumerateFileSystemEntries(currentNormalized).Any())
            {
                break;
            }

            try
            {
                Directory.Delete(currentNormalized, recursive: false);
                var rel = Path.GetRelativePath(_rootPath, currentNormalized).Replace('\\', '/');
                _logger.LogInformation("Removed empty local directory after child removal: '{Path}'", rel);
            }
            catch
            {
                break;
            }

            current = Path.GetDirectoryName(currentNormalized);
        }
    }

    private static void MergeDirectoryIntoTrash(string sourceDir, string targetDir)
    {
        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Move(file, destFile, overwrite: true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));
            if (Directory.Exists(destSubDir))
            {
                MergeDirectoryIntoTrash(subDir, destSubDir);
                Directory.Delete(subDir, recursive: true);
            }
            else
            {
                Directory.Move(subDir, destSubDir);
            }
        }
    }
}
