using FolderSyncEr.Contracts;
using FolderSyncEr.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace FolderSyncEr.Server.Services;

public interface ISettingsService
{
    Task<IReadOnlyList<ApiSettingDto>> GetAllSettingsAsync(CancellationToken cancellationToken);
    Task<ApiSettingDto?> GetSettingAsync(string key, CancellationToken cancellationToken);
    Task<bool> UpdateSettingAsync(string key, string value, CancellationToken cancellationToken);
    Task InitializeDefaultSettingsAsync(CancellationToken cancellationToken);
}

public class SettingsService : ISettingsService
{
    private readonly ServerDbContext _db;
    private readonly IConfiguration _config;

    private static readonly HashSet<string> ReadOnlyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "StorageRootPath"
    };

    public SettingsService(ServerDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    public async Task<IReadOnlyList<ApiSettingDto>> GetAllSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await _db.ApiSettings
            .AsNoTracking()
            .OrderBy(s => s.Key)
            .Select(s => new ApiSettingDto(s.Key, s.Value, s.UpdatedUtc))
            .ToListAsync(cancellationToken);

        return settings;
    }

    public async Task<ApiSettingDto?> GetSettingAsync(string key, CancellationToken cancellationToken)
    {
        var setting = await _db.ApiSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key.ToLower() == key.ToLower(), cancellationToken);

        return setting == null ? null : new ApiSettingDto(setting.Key, setting.Value, setting.UpdatedUtc);
    }

    public async Task<bool> UpdateSettingAsync(string key, string value, CancellationToken cancellationToken)
    {
        if (ReadOnlyKeys.Contains(key))
        {
            throw new InvalidOperationException($"Setting '{key}' is read-only via API.");
        }

        var setting = await _db.ApiSettings.FirstOrDefaultAsync(s => s.Key.ToLower() == key.ToLower(), cancellationToken);
        if (setting == null)
        {
            setting = new ApiSetting
            {
                Key = key,
                Value = value,
                UpdatedUtc = DateTime.UtcNow
            };
            _db.ApiSettings.Add(setting);
        }
        else
        {
            setting.Value = value;
            setting.UpdatedUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task InitializeDefaultSettingsAsync(CancellationToken cancellationToken)
    {
        var defaults = new Dictionary<string, string>
        {
            ["MaxUploadBytes"] = _config["Storage:MaxUploadBytes"] ?? (4L * 1024 * 1024 * 1024).ToString(),
            ["StorageRootPath"] = _config["Storage:RootPath"] ?? "storage",
            ["LogRetentionDays"] = "90",
            ["TombstoneRetentionDays"] = "180",
            ["VersionRetentionCount"] = "5"
        };

        foreach (var (k, v) in defaults)
        {
            if (!await _db.ApiSettings.AnyAsync(s => s.Key == k, cancellationToken))
            {
                _db.ApiSettings.Add(new ApiSetting
                {
                    Key = k,
                    Value = v,
                    UpdatedUtc = DateTime.UtcNow
                });
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}

