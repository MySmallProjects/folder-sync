using FolderSyncEr.Contracts;
using FolderSyncEr.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace FolderSyncEr.Server.Services;

public enum TrashOpStatus
{
    Success,
    NotFound,
    Conflict,
    BadRequest,
    ContentMissing
}

public record TrashOpResult<T>(TrashOpStatus Status, T? Value, string? Error = null)
{
    public static TrashOpResult<T> Ok(T value) => new(TrashOpStatus.Success, value);
    public static TrashOpResult<T> Fail(TrashOpStatus status, string error) => new(status, default, error);
}

public interface ITrashService
{
    Task<IReadOnlyList<DeletedItemDto>> ListDeletedAsync(int userId, CancellationToken cancellationToken);
    Task<TrashOpResult<RestoreResponse>> RestoreAsync(int userId, string fileId, string? targetPath, string? deviceId, CancellationToken cancellationToken);
    Task<TrashOpResult<PurgeResponse>> PurgeAsync(int userId, string fileId, string? deviceId, CancellationToken cancellationToken);
    Task<PurgeResponse> EmptyTrashAsync(int userId, string? deviceId, CancellationToken cancellationToken);
}

/// <summary>
/// Restore / permanent-delete operations on tombstoned (soft-deleted) files and folders.
///
/// Deleting a file only tombstones the <see cref="FileRecord"/> (IsDeleted = true, Version++),
/// while the content blobs of the retained <see cref="FileVersionRecord"/>s stay on disk.
/// Restoring therefore copies the latest retained blob into a new version slot and clears the
/// tombstone with a fresh ChangeSeq, so every client picks the item up through normal delta sync.
/// </summary>
public class TrashService : ITrashService
{
    /// <summary>
    /// Folder contents count as "deleted together with the folder" when their tombstone time is
    /// within this window of the folder's own tombstone. Sync clients delete a folder's children
    /// one by one, so their timestamps differ slightly from the folder's.
    /// </summary>
    public static readonly TimeSpan SameDeletionWindow = TimeSpan.FromMinutes(5);

    private readonly ServerDbContext _db;
    private readonly IBlobStorageService _blobStorage;
    private readonly IConfiguration _config;
    private readonly ILogger<TrashService> _logger;

    public TrashService(ServerDbContext db, IBlobStorageService blobStorage, IConfiguration config, ILogger<TrashService> logger)
    {
        _db = db;
        _blobStorage = blobStorage;
        _config = config;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DeletedItemDto>> ListDeletedAsync(int userId, CancellationToken cancellationToken)
    {
        var deleted = await _db.Files.AsNoTracking()
            .Where(f => f.UserId == userId && f.IsDeleted)
            .ToListAsync(cancellationToken);

        var deletedDirKeys = deleted.Where(f => f.IsDirectory).Select(f => f.PathKey).ToHashSet(StringComparer.Ordinal);

        // Fold items into their top-most deleted ancestor folder so a deleted folder shows as one row
        var topLevel = new Dictionary<string, (FileRecord Record, int Count, long Size)>(StringComparer.Ordinal);
        var nested = new List<(FileRecord Record, string TopKey)>();

        foreach (var item in deleted)
        {
            var topAncestor = FindTopDeletedAncestor(item.PathKey, deletedDirKeys);
            if (topAncestor == null)
            {
                topLevel[item.PathKey] = (item, 0, 0);
            }
            else
            {
                nested.Add((item, topAncestor));
            }
        }

        foreach (var (record, topKey) in nested)
        {
            if (topLevel.TryGetValue(topKey, out var entry))
            {
                topLevel[topKey] = (entry.Record, entry.Count + 1, entry.Size + (record.IsDirectory ? 0 : record.Size));
            }
        }

        return topLevel.Values
            .OrderByDescending(e => e.Record.DeletedAtUtc)
            .Select(e => new DeletedItemDto(
                e.Record.Id,
                e.Record.Path,
                e.Record.IsDirectory,
                e.Record.Size,
                e.Record.Version,
                e.Record.LastModifiedUtc,
                e.Record.DeletedAtUtc ?? e.Record.UploadedAtUtc,
                e.Count,
                e.Size))
            .ToList();
    }

    public async Task<TrashOpResult<RestoreResponse>> RestoreAsync(int userId, string fileId, string? targetPath, string? deviceId, CancellationToken cancellationToken)
    {
        var root = await _db.Files.FirstOrDefaultAsync(f => f.UserId == userId && f.Id == fileId, cancellationToken);
        if (root == null || !root.IsDeleted)
        {
            return TrashOpResult<RestoreResponse>.Fail(TrashOpStatus.NotFound, "Deleted item not found.");
        }

        var newRootPath = root.Path;
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            if (!PathRules.TryValidatePath(targetPath, out newRootPath, out var pathError))
            {
                return TrashOpResult<RestoreResponse>.Fail(TrashOpStatus.BadRequest, pathError);
            }
        }

        // Collect the folder contents that were deleted together with the folder
        var items = new List<FileRecord> { root };
        if (root.IsDirectory)
        {
            var deletedAt = root.DeletedAtUtc ?? DateTime.UtcNow;
            var from = deletedAt - SameDeletionWindow;
            var to = deletedAt + SameDeletionWindow;
            var prefix = root.PathKey + "/";

            var candidates = await _db.Files
                .Where(f => f.UserId == userId && f.IsDeleted && f.Id != root.Id)
                .ToListAsync(cancellationToken);

            items.AddRange(candidates.Where(f =>
                f.PathKey.StartsWith(prefix, StringComparison.Ordinal) &&
                f.DeletedAtUtc >= from && f.DeletedAtUtc <= to));
        }

        // Compute target paths and make sure none of them is occupied by another record
        var plan = new List<(FileRecord Record, string NewPath, string NewKey)>();
        foreach (var item in items)
        {
            var suffix = item.Path.Length > root.Path.Length ? item.Path[root.Path.Length..] : string.Empty;
            var newPath = newRootPath + suffix;
            var newKey = PathRules.ToLookupKey(newPath);
            plan.Add((item, newPath, newKey));
        }

        var movingKeys = plan.Where(p => p.NewKey != p.Record.PathKey).Select(p => p.NewKey).ToList();
        if (movingKeys.Count > 0)
        {
            var occupied = await _db.Files.AsNoTracking()
                .Where(f => f.UserId == userId && movingKeys.Contains(f.PathKey))
                .Select(f => f.Path)
                .FirstOrDefaultAsync(cancellationToken);
            if (occupied != null)
            {
                return TrashOpResult<RestoreResponse>.Fail(TrashOpStatus.Conflict, $"'{occupied}' already exists on the server.");
            }
        }

        // Deleted parent folders must come back too, otherwise the item has nowhere to live
        var ancestorsToRevive = new List<FileRecord>();
        foreach (var ancestorKey in GetAncestorKeys(PathRules.ToLookupKey(newRootPath)))
        {
            var ancestor = await _db.Files.FirstOrDefaultAsync(f => f.UserId == userId && f.PathKey == ancestorKey, cancellationToken);
            if (ancestor == null) continue;

            if (!ancestor.IsDirectory)
            {
                return TrashOpResult<RestoreResponse>.Fail(TrashOpStatus.Conflict, $"'{ancestor.Path}' is a file, so the item cannot be restored beneath it.");
            }

            if (ancestor.IsDeleted)
            {
                ancestorsToRevive.Add(ancestor);
            }
        }

        // Make sure the root file's content is still available before touching anything
        if (!root.IsDirectory)
        {
            var hasContent = await _db.FileVersions.AnyAsync(v => v.FileId == root.Id, cancellationToken);
            if (!hasContent)
            {
                return TrashOpResult<RestoreResponse>.Fail(TrashOpStatus.ContentMissing, "The file content is no longer available on the server.");
            }
        }

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        // Parents first, then shallow-to-deep, so clients receive folders before their contents
        foreach (var ancestor in ancestorsToRevive.OrderBy(a => a.PathKey.Length))
        {
            ancestor.IsDeleted = false;
            ancestor.DeletedAtUtc = null;
            ancestor.Version++;
            ancestor.ChangeSeq = await IncrementUserCounterAsync(userId, cancellationToken);
        }

        var restoredCount = 0;
        foreach (var (record, newPath, newKey) in plan.OrderBy(p => p.NewKey.Length))
        {
            var newVersion = record.Version + 1;

            if (!record.IsDirectory)
            {
                var latest = await _db.FileVersions
                    .Where(v => v.FileId == record.Id)
                    .OrderByDescending(v => v.Version)
                    .FirstOrDefaultAsync(cancellationToken);

                var copiedBlob = latest == null
                    ? null
                    : await _blobStorage.CopyBlobAsync(userId, record.Id, newVersion, latest.BlobRelativePath, cancellationToken);

                if (latest == null || copiedBlob == null)
                {
                    _logger.LogWarning("Skipping restore of '{Path}': content blob missing", record.Path);
                    continue;
                }

                _db.FileVersions.Add(new FileVersionRecord
                {
                    FileId = record.Id,
                    Version = newVersion,
                    Sha256 = latest.Sha256,
                    Size = latest.Size,
                    BlobRelativePath = copiedBlob,
                    CreatedUtc = DateTime.UtcNow,
                    DeviceId = deviceId
                });

                record.Sha256 = latest.Sha256;
                record.Size = latest.Size;
                await PruneOldVersionsAsync(record.Id, newVersion, cancellationToken);
            }

            record.Path = newPath;
            record.PathKey = newKey;
            record.Version = newVersion;
            record.IsDeleted = false;
            record.DeletedAtUtc = null;
            record.ChangeSeq = await IncrementUserCounterAsync(userId, cancellationToken);
            restoredCount++;
        }

        _db.ActivityLogs.Add(new ActivityLog
        {
            TimestampUtc = DateTime.UtcNow,
            UserId = userId,
            DeviceId = deviceId,
            Action = "Restore",
            Path = newRootPath,
            Result = "Success",
            Message = newRootPath == root.Path
                ? $"Restored {restoredCount} item(s) from trash"
                : $"Restored {restoredCount} item(s) from trash as copy (original: {root.Path})"
        });

        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return TrashOpResult<RestoreResponse>.Ok(new RestoreResponse(newRootPath, restoredCount));
    }

    public async Task<TrashOpResult<PurgeResponse>> PurgeAsync(int userId, string fileId, string? deviceId, CancellationToken cancellationToken)
    {
        var root = await _db.Files.FirstOrDefaultAsync(f => f.UserId == userId && f.Id == fileId, cancellationToken);
        if (root == null || !root.IsDeleted)
        {
            return TrashOpResult<PurgeResponse>.Fail(TrashOpStatus.NotFound, "Deleted item not found.");
        }

        var items = new List<FileRecord> { root };
        if (root.IsDirectory)
        {
            // Everything deleted beneath the folder is folded into it in the list, so purge it all
            var prefix = root.PathKey + "/";
            var deletedOthers = await _db.Files
                .Where(f => f.UserId == userId && f.IsDeleted && f.Id != root.Id)
                .ToListAsync(cancellationToken);
            items.AddRange(deletedOthers.Where(f => f.PathKey.StartsWith(prefix, StringComparison.Ordinal)));
        }

        var result = await PurgeRecordsAsync(userId, items, deviceId, root.Path, cancellationToken);
        return TrashOpResult<PurgeResponse>.Ok(result);
    }

    public async Task<PurgeResponse> EmptyTrashAsync(int userId, string? deviceId, CancellationToken cancellationToken)
    {
        var items = await _db.Files
            .Where(f => f.UserId == userId && f.IsDeleted)
            .ToListAsync(cancellationToken);

        if (items.Count == 0)
        {
            return new PurgeResponse(0, 0);
        }

        return await PurgeRecordsAsync(userId, items, deviceId, null, cancellationToken);
    }

    private async Task<PurgeResponse> PurgeRecordsAsync(int userId, List<FileRecord> items, string? deviceId, string? logPath, CancellationToken cancellationToken)
    {
        var ids = items.Select(i => i.Id).ToList();
        var versions = await _db.FileVersions.Where(v => ids.Contains(v.FileId)).ToListAsync(cancellationToken);
        var freedBytes = versions.Sum(v => v.Size);

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        _db.FileVersions.RemoveRange(versions);
        _db.Files.RemoveRange(items);

        _db.ActivityLogs.Add(new ActivityLog
        {
            TimestampUtc = DateTime.UtcNow,
            UserId = userId,
            DeviceId = deviceId,
            Action = "Purge",
            Path = logPath,
            Result = "Success",
            Message = logPath == null
                ? $"Emptied trash: permanently deleted {items.Count} item(s), freed {freedBytes} bytes"
                : $"Permanently deleted {items.Count} item(s), freed {freedBytes} bytes"
        });

        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        // Remove blobs only after the DB commit so a failed commit never loses content
        foreach (var version in versions)
        {
            await _blobStorage.DeleteBlobAsync(version.BlobRelativePath, cancellationToken);
        }
        foreach (var item in items.Where(i => !i.IsDirectory))
        {
            TryDeleteEmptyBlobDirectory(userId, item.Id);
        }

        return new PurgeResponse(items.Count, freedBytes);
    }

    private void TryDeleteEmptyBlobDirectory(int userId, string fileId)
    {
        try
        {
            var prefix = fileId.Length >= 2 ? fileId[..2] : "00";
            var dir = Path.Combine(_blobStorage.GetStorageRoot(), userId.ToString(), prefix, fileId);
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remove blob directory for {FileId}", fileId);
        }
    }

    private async Task PruneOldVersionsAsync(string fileId, long newVersion, CancellationToken cancellationToken)
    {
        // The new version record is not saved yet, so keep (retention - 1) existing ones
        var retentionCount = _config.GetValue<int>("Storage:VersionRetentionCount", 5);
        var oldVersions = await _db.FileVersions
            .Where(v => v.FileId == fileId && v.Version < newVersion)
            .OrderByDescending(v => v.Version)
            .Skip(Math.Max(retentionCount - 1, 0))
            .ToListAsync(cancellationToken);

        foreach (var oldVer in oldVersions)
        {
            _db.FileVersions.Remove(oldVer);
            await _blobStorage.DeleteBlobAsync(oldVer.BlobRelativePath, cancellationToken);
        }
    }

    private static string? FindTopDeletedAncestor(string pathKey, HashSet<string> deletedDirKeys)
    {
        // Walk from the root downwards so the top-most deleted folder wins
        var segments = pathKey.Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            var ancestor = string.Join('/', segments, 0, i);
            if (deletedDirKeys.Contains(ancestor))
            {
                return ancestor;
            }
        }
        return null;
    }

    private static IEnumerable<string> GetAncestorKeys(string pathKey)
    {
        var segments = pathKey.Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            yield return string.Join('/', segments, 0, i);
        }
    }

    private async Task<long> IncrementUserCounterAsync(int userId, CancellationToken cancellationToken)
    {
        var counter = await _db.UserCounters.FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);
        if (counter == null)
        {
            counter = new UserCounter { UserId = userId, LastChangeSeq = 1 };
            _db.UserCounters.Add(counter);
        }
        else
        {
            counter.LastChangeSeq++;
        }
        await _db.SaveChangesAsync(cancellationToken);
        return counter.LastChangeSeq;
    }
}

