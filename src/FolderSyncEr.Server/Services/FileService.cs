using FolderSyncEr.Contracts;
using FolderSyncEr.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace FolderSyncEr.Server.Services;

public class FileService : IFileService
{
    private readonly ServerDbContext _db;
    private readonly IBlobStorageService _blobStorage;
    private readonly IConfiguration _config;
    private readonly ILogger<FileService> _logger;

    public FileService(
        ServerDbContext db,
        IBlobStorageService blobStorage,
        IConfiguration config,
        ILogger<FileService> logger)
    {
        _db = db;
        _blobStorage = blobStorage;
        _config = config;
        _logger = logger;
    }

    public async Task<FileListResponse> ListFilesAsync(
        int userId,
        long? sinceSeq,
        DateTime? sinceUtc,
        bool includeDeleted,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 1000);

        var query = _db.Files.AsNoTracking().Where(f => f.UserId == userId);

        if (!includeDeleted)
        {
            query = query.Where(f => !f.IsDeleted);
        }

        if (sinceSeq.HasValue)
        {
            query = query.Where(f => f.ChangeSeq > sinceSeq.Value);
        }
        else if (sinceUtc.HasValue)
        {
            query = query.Where(f => f.UploadedAtUtc > sinceUtc.Value);
        }

        if (!string.IsNullOrWhiteSpace(cursor) && long.TryParse(cursor, out var cursorSeq))
        {
            query = query.Where(f => f.ChangeSeq > cursorSeq);
        }

        var currentCounter = await _db.UserCounters
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => (long?)c.LastChangeSeq)
            .FirstOrDefaultAsync(cancellationToken) ?? 0;

        var items = await query
            .OrderBy(f => f.ChangeSeq)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (items.Count > limit)
        {
            var nextItem = items[limit];
            nextCursor = nextItem.ChangeSeq.ToString();
            items = items.Take(limit).ToList();
        }

        var dtos = items.Select(ToFileDto).ToList();

        return new FileListResponse(
            dtos,
            nextCursor,
            currentCounter,
            DateTime.UtcNow
        );
    }

    public async Task<FileRecord?> GetFileByIdAsync(int userId, string fileId, CancellationToken cancellationToken)
    {
        return await _db.Files
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.UserId == userId && f.Id == fileId, cancellationToken);
    }

    public async Task<FileRecord?> GetFileByPathAsync(int userId, string path, CancellationToken cancellationToken)
    {
        if (!PathRules.TryValidatePath(path, out var normalized, out _))
        {
            return null;
        }

        var pathKey = PathRules.ToLookupKey(normalized);
        return await _db.Files
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.UserId == userId && f.PathKey == pathKey, cancellationToken);
    }

    public async Task<UploadFileResult> UploadFileAsync(UploadFileCommand command, CancellationToken cancellationToken)
    {
        if (!PathRules.TryValidatePath(command.RawPath, out var normalizedPath, out var pathError))
        {
            return new UploadFileResult(false, false, false, true, pathError, null, null);
        }

        var pathKey = PathRules.ToLookupKey(normalizedPath);
        var maxUploadBytes = _config.GetValue<long>("Storage:MaxUploadBytes", 4L * 1024 * 1024 * 1024);

        var username = await _db.Users.AsNoTracking()
            .Where(u => u.Id == command.UserId)
            .Select(u => u.Username)
            .FirstOrDefaultAsync(cancellationToken) ?? command.UserId.ToString();

        // Stream into temporary file and compute SHA-256
        TempUploadResult tempResult;
        try
        {
            tempResult = await _blobStorage.SaveStreamToTempAsync(username, command.InputStream, maxUploadBytes, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return new UploadFileResult(false, false, false, true, ex.Message, null, null);
        }

        // Validate client-provided hash if given
        if (!string.IsNullOrWhiteSpace(command.ProvidedSha256))
        {
            var cleanProvided = command.ProvidedSha256.Trim().ToLowerInvariant().Trim('"');
            if (cleanProvided != tempResult.ComputedSha256)
            {
                try { File.Delete(tempResult.TempFilePath); } catch { /* ignore */ }
                return new UploadFileResult(false, false, true, false,
                    $"Hash mismatch: expected {cleanProvided}, computed {tempResult.ComputedSha256}", null, null);
            }
        }

        // Start DB transaction for atomic commit and optimistic concurrency check
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        var existingFile = await _db.Files
            .FirstOrDefaultAsync(f => f.UserId == command.UserId && f.PathKey == pathKey, cancellationToken);

        // Precondition check: If-None-Match
        if (command.IfNoneMatch == "*")
        {
            if (existingFile != null && !existingFile.IsDeleted)
            {
                try { File.Delete(tempResult.TempFilePath); } catch { /* ignore */ }
                var conflictDto = CreateConflictDto(normalizedPath, command, tempResult, existingFile);
                return new UploadFileResult(false, true, false, false, "File already exists", null, conflictDto);
            }
        }

        // Precondition check: If-Match hash
        if (!string.IsNullOrWhiteSpace(command.IfMatchSha256))
        {
            var requiredHash = command.IfMatchSha256.Trim().ToLowerInvariant().Trim('"');
            if (existingFile == null || existingFile.Sha256?.ToLowerInvariant() != requiredHash)
            {
                try { File.Delete(tempResult.TempFilePath); } catch { /* ignore */ }
                var conflictDto = CreateConflictDto(normalizedPath, command, tempResult, existingFile);
                return new UploadFileResult(false, true, false, false, "Precondition failed: file content changed on server", null, conflictDto);
            }
        }

        // Precondition check: X-Base-Version
        if (command.BaseVersion.HasValue)
        {
            if (existingFile == null || existingFile.Version != command.BaseVersion.Value)
            {
                try { File.Delete(tempResult.TempFilePath); } catch { /* ignore */ }
                var conflictDto = CreateConflictDto(normalizedPath, command, tempResult, existingFile);
                return new UploadFileResult(false, true, false, false, "Precondition failed: file version mismatch", null, conflictDto);
            }
        }

        var nextSeq = await IncrementUserCounterAsync(command.UserId, cancellationToken);
        var fileId = existingFile?.Id ?? Guid.NewGuid().ToString("N");
        var newVersion = (existingFile?.Version ?? 0) + 1;

        // Move temp file to final storage destination
        var relativeBlobPath = await _blobStorage.CommitBlobAsync(
            username,
            fileId,
            newVersion,
            tempResult.TempFilePath,
            cancellationToken
        );

        if (existingFile == null)
        {
            existingFile = new FileRecord
            {
                Id = fileId,
                UserId = command.UserId,
                Path = normalizedPath,
                PathKey = pathKey,
                IsDirectory = false,
                Size = tempResult.Size,
                Sha256 = tempResult.ComputedSha256,
                Version = newVersion,
                LastModifiedUtc = command.LastModifiedUtc ?? DateTime.UtcNow,
                UploadedAtUtc = DateTime.UtcNow,
                UploadedByDeviceId = command.DeviceId,
                IsDeleted = false,
                DeletedAtUtc = null,
                ChangeSeq = nextSeq
            };
            _db.Files.Add(existingFile);
        }
        else
        {
            existingFile.Path = normalizedPath;
            existingFile.IsDirectory = false;
            existingFile.Size = tempResult.Size;
            existingFile.Sha256 = tempResult.ComputedSha256;
            existingFile.Version = newVersion;
            existingFile.LastModifiedUtc = command.LastModifiedUtc ?? DateTime.UtcNow;
            existingFile.UploadedAtUtc = DateTime.UtcNow;
            existingFile.UploadedByDeviceId = command.DeviceId;
            existingFile.IsDeleted = false;
            existingFile.DeletedAtUtc = null;
            existingFile.ChangeSeq = nextSeq;
        }

        // Record version for history
        var versionRecord = new FileVersionRecord
        {
            FileId = fileId,
            Version = newVersion,
            Sha256 = tempResult.ComputedSha256,
            Size = tempResult.Size,
            BlobRelativePath = relativeBlobPath,
            CreatedUtc = DateTime.UtcNow,
            DeviceId = command.DeviceId
        };
        _db.FileVersions.Add(versionRecord);

        // Prune older versions keeping last 5
        var retentionCount = _config.GetValue<int>("Storage:VersionRetentionCount", 5);
        var oldVersions = await _db.FileVersions
            .Where(v => v.FileId == fileId)
            .OrderByDescending(v => v.Version)
            .Skip(retentionCount)
            .ToListAsync(cancellationToken);

        foreach (var oldVer in oldVersions)
        {
            _db.FileVersions.Remove(oldVer);
            await _blobStorage.DeleteBlobAsync(oldVer.BlobRelativePath, cancellationToken);
        }

        // Activity log
        _db.ActivityLogs.Add(new ActivityLog
        {
            TimestampUtc = DateTime.UtcNow,
            UserId = command.UserId,
            DeviceId = command.DeviceId,
            Action = "Upload",
            Path = normalizedPath,
            Result = "Success",
            Message = $"Uploaded v{newVersion} ({tempResult.Size} bytes, sha256: {tempResult.ComputedSha256[..8]})"
        });

        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return new UploadFileResult(true, false, false, false, null, ToFileDto(existingFile), null);
    }

    public async Task<FileRecord> CreateDirectoryAsync(int userId, string rawPath, string? deviceId, CancellationToken cancellationToken)
    {
        if (!PathRules.TryValidatePath(rawPath, out var normalizedPath, out var pathError))
        {
            throw new ArgumentException(pathError, nameof(rawPath));
        }

        var pathKey = PathRules.ToLookupKey(normalizedPath);
        var existing = await _db.Files.FirstOrDefaultAsync(f => f.UserId == userId && f.PathKey == pathKey, cancellationToken);
        if (existing != null)
        {
            if (existing.IsDeleted)
            {
                existing.IsDeleted = false;
                existing.DeletedAtUtc = null;
                existing.ChangeSeq = await IncrementUserCounterAsync(userId, cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
            }
            return existing;
        }

        var nextSeq = await IncrementUserCounterAsync(userId, cancellationToken);
        var dirRecord = new FileRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            Path = normalizedPath,
            PathKey = pathKey,
            IsDirectory = true,
            Size = 0,
            Sha256 = null,
            Version = 1,
            LastModifiedUtc = DateTime.UtcNow,
            UploadedAtUtc = DateTime.UtcNow,
            UploadedByDeviceId = deviceId,
            IsDeleted = false,
            ChangeSeq = nextSeq
        };
        _db.Files.Add(dirRecord);

        _db.ActivityLogs.Add(new ActivityLog
        {
            TimestampUtc = DateTime.UtcNow,
            UserId = userId,
            DeviceId = deviceId,
            Action = "CreateDirectory",
            Path = normalizedPath,
            Result = "Success",
            Message = "Directory created"
        });

        await _db.SaveChangesAsync(cancellationToken);
        return dirRecord;
    }

    public async Task<DeleteFileResult> DeleteFileAsync(int userId, string fileId, string? ifMatchSha256, long? baseVersion, string? deviceId, CancellationToken cancellationToken)
    {
        var file = await _db.Files.FirstOrDefaultAsync(f => f.UserId == userId && f.Id == fileId, cancellationToken);
        if (file == null || file.IsDeleted)
        {
            return new DeleteFileResult(false, true, false, "File not found", null);
        }

        // Precondition checks
        if (!string.IsNullOrWhiteSpace(ifMatchSha256))
        {
            var reqHash = ifMatchSha256.Trim().ToLowerInvariant().Trim('"');
            if (file.Sha256?.ToLowerInvariant() != reqHash)
            {
                var conflictDto = new ConflictDto(
                    file.Path,
                    new FileVersionInfoDto(baseVersion, reqHash, 0, null, null, deviceId, false),
                    new FileVersionInfoDto(file.Version, file.Sha256, file.Size, file.LastModifiedUtc, file.UploadedAtUtc, file.UploadedByDeviceId, file.IsDeleted)
                );
                return new DeleteFileResult(false, false, true, "File changed on server", conflictDto);
            }
        }

        if (baseVersion.HasValue && file.Version != baseVersion.Value)
        {
            var conflictDto = new ConflictDto(
                file.Path,
                new FileVersionInfoDto(baseVersion.Value, ifMatchSha256, 0, null, null, deviceId, false),
                new FileVersionInfoDto(file.Version, file.Sha256, file.Size, file.LastModifiedUtc, file.UploadedAtUtc, file.UploadedByDeviceId, file.IsDeleted)
            );
            return new DeleteFileResult(false, false, true, "File version mismatch", conflictDto);
        }

        file.IsDeleted = true;
        file.DeletedAtUtc = DateTime.UtcNow;
        file.Version++;
        file.ChangeSeq = await IncrementUserCounterAsync(userId, cancellationToken);

        _db.ActivityLogs.Add(new ActivityLog
        {
            TimestampUtc = DateTime.UtcNow,
            UserId = userId,
            DeviceId = deviceId,
            Action = "Delete",
            Path = file.Path,
            Result = "Success",
            Message = $"Tombstoned v{file.Version}"
        });

        await _db.SaveChangesAsync(cancellationToken);
        return new DeleteFileResult(true, false, false, null, null);
    }

    public async Task<FileRecord?> MoveFileAsync(int userId, string fromPath, string toPath, string? ifMatchSha256, string? deviceId, CancellationToken cancellationToken)
    {
        if (!PathRules.TryValidatePath(fromPath, out var normalizedFrom, out _) ||
            !PathRules.TryValidatePath(toPath, out var normalizedTo, out _))
        {
            return null;
        }

        var fromKey = PathRules.ToLookupKey(normalizedFrom);
        var toKey = PathRules.ToLookupKey(normalizedTo);

        var file = await _db.Files.FirstOrDefaultAsync(f => f.UserId == userId && f.PathKey == fromKey && !f.IsDeleted, cancellationToken);
        if (file == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(ifMatchSha256) && file.Sha256 != ifMatchSha256.Trim().ToLowerInvariant().Trim('"'))
        {
            return null;
        }

        var destination = await _db.Files.FirstOrDefaultAsync(f => f.UserId == userId && f.PathKey == toKey && !f.IsDeleted, cancellationToken);
        if (destination != null)
        {
            return null; // Target already exists
        }

        file.Path = normalizedTo;
        file.PathKey = toKey;
        file.Version++;
        file.ChangeSeq = await IncrementUserCounterAsync(userId, cancellationToken);

        _db.ActivityLogs.Add(new ActivityLog
        {
            TimestampUtc = DateTime.UtcNow,
            UserId = userId,
            DeviceId = deviceId,
            Action = "Move",
            Path = $"{normalizedFrom} -> {normalizedTo}",
            Result = "Success",
            Message = $"Moved to {normalizedTo}"
        });

        await _db.SaveChangesAsync(cancellationToken);
        return file;
    }

    public async Task<FileRecord?> PatchLastModifiedAsync(int userId, string fileId, DateTime lastModifiedUtc, CancellationToken cancellationToken)
    {
        var file = await _db.Files.FirstOrDefaultAsync(f => f.UserId == userId && f.Id == fileId && !f.IsDeleted, cancellationToken);
        if (file == null) return null;

        file.LastModifiedUtc = lastModifiedUtc;
        file.ChangeSeq = await IncrementUserCounterAsync(userId, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return file;
    }

    public Task<StorageInfoDto> GetStorageInfoAsync(int userId, CancellationToken cancellationToken)
    {
        var (total, used) = _blobStorage.GetDiskSpaceInfo();
        var quota = _config.GetValue<long?>("Storage:UserQuotaBytes", null);
        return Task.FromResult(new StorageInfoDto(total, used, quota));
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

    private static ConflictDto CreateConflictDto(string path, UploadFileCommand cmd, TempUploadResult temp, FileRecord? server)
    {
        var clientInfo = new FileVersionInfoDto(
            cmd.BaseVersion,
            temp.ComputedSha256,
            temp.Size,
            cmd.LastModifiedUtc,
            DateTime.UtcNow,
            cmd.DeviceId,
            false
        );

        var serverInfo = server == null
            ? new FileVersionInfoDto(null, null, 0, null, null, null, true)
            : new FileVersionInfoDto(
                server.Version,
                server.Sha256,
                server.Size,
                server.LastModifiedUtc,
                server.UploadedAtUtc,
                server.UploadedByDeviceId,
                server.IsDeleted
            );

        return new ConflictDto(path, clientInfo, serverInfo);
    }

    private static FileDto ToFileDto(FileRecord f) => new(
        f.Id,
        f.Path,
        f.IsDirectory,
        f.Size,
        f.Sha256,
        f.Version,
        f.LastModifiedUtc,
        f.UploadedAtUtc,
        f.UploadedByDeviceId,
        f.IsDeleted,
        f.ChangeSeq
    );
}

