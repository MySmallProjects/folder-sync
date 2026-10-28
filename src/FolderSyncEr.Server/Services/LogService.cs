using FolderSyncEr.Contracts;
using FolderSyncEr.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace FolderSyncEr.Server.Services;

public interface ILogService
{
    Task LogAsync(int? userId, string? deviceId, string action, string? path, string result, string? message, string? correlationId, CancellationToken cancellationToken);
    Task<PagedResult<ActivityLogDto>> GetLogsAsync(int? callerUserId, bool isAdmin, int? targetUserId, string? deviceId, string? action, string? result, string? path, DateTime? fromUtc, DateTime? toUtc, int page, int pageSize, CancellationToken cancellationToken);
}

public class LogService : ILogService
{
    private readonly ServerDbContext _db;
    private readonly ILogger<LogService> _logger;

    public LogService(ServerDbContext db, ILogger<LogService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task LogAsync(int? userId, string? deviceId, string action, string? path, string result, string? message, string? correlationId, CancellationToken cancellationToken)
    {
        _db.ActivityLogs.Add(new ActivityLog
        {
            TimestampUtc = DateTime.UtcNow,
            UserId = userId,
            DeviceId = deviceId,
            Action = action,
            Path = path,
            Result = result,
            Message = message,
            CorrelationId = correlationId
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PagedResult<ActivityLogDto>> GetLogsAsync(
        int? callerUserId,
        bool isAdmin,
        int? targetUserId,
        string? deviceId,
        string? action,
        string? result,
        string? path,
        DateTime? fromUtc,
        DateTime? toUtc,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        var query = _db.ActivityLogs.AsNoTracking();

        if (!isAdmin || !targetUserId.HasValue)
        {
            query = query.Where(l => l.UserId == callerUserId);
        }
        else
        {
            query = query.Where(l => l.UserId == targetUserId.Value);
        }

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            query = query.Where(l => l.DeviceId == deviceId);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(l => l.Action.ToLower() == action.ToLower());
        }

        if (!string.IsNullOrWhiteSpace(result))
        {
            query = query.Where(l => l.Result.ToLower() == result.ToLower());
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            query = query.Where(l => l.Path != null && l.Path.ToLower().Contains(path.ToLower()));
        }

        if (fromUtc.HasValue)
        {
            query = query.Where(l => l.TimestampUtc >= fromUtc.Value);
        }

        if (toUtc.HasValue)
        {
            query = query.Where(l => l.TimestampUtc <= toUtc.Value);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(l => l.TimestampUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new ActivityLogDto(
                l.Id,
                l.TimestampUtc,
                l.UserId,
                l.DeviceId,
                l.Action,
                l.Path,
                l.Result,
                l.Message,
                l.CorrelationId
            ))
            .ToListAsync(cancellationToken);

        return new PagedResult<ActivityLogDto>(items, page, pageSize, total);
    }
}

