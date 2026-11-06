using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderSyncEr.Client.Platform;

public record ActivityRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Activity { get; init; } = string.Empty;
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public string? Category { get; init; }
}

public interface IClientActivityService : IDisposable
{
    event EventHandler<ActivityRecord>? ActivityAdded;
    IReadOnlyList<ActivityRecord> GetActivities();
    void LogActivity(string activity, string? category = null, DateTime? timestampUtc = null);
    Task SaveAsync();
}

public class ClientActivityService : IClientActivityService
{
    private const int MaxActivityRetention = 1000;
    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly List<ActivityRecord> _activities = new();
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private DateTime _lastLoggedUtc = DateTime.MinValue;
    private string _lastLoggedActivity = string.Empty;
    private Timer? _debounceSaveTimer;
    private bool _isDisposed;

    public event EventHandler<ActivityRecord>? ActivityAdded;

    public ClientActivityService(string? customFilePath = null)
    {
        _filePath = customFilePath ?? GetDefaultActivitiesFilePath();
        LoadFromDisk();
    }

    public static string GetDefaultActivitiesFilePath()
    {
        var configDir = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderSyncEr")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "foldersyncer");

        try
        {
            if (!Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
            }
            return Path.Combine(configDir, "activities.json");
        }
        catch
        {
            var fallbackDir = Path.Combine(Path.GetTempPath(), "foldersyncer");
            if (!Directory.Exists(fallbackDir))
            {
                Directory.CreateDirectory(fallbackDir);
            }
            return Path.Combine(fallbackDir, "activities.json");
        }
    }

    private void LoadFromDisk()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_filePath))
                    return;

                var json = File.ReadAllText(_filePath);
                var items = JsonSerializer.Deserialize<List<ActivityRecord>>(json, _jsonOptions);
                if (items != null)
                {
                    _activities.Clear();
                    _activities.AddRange(items.OrderByDescending(a => a.TimestampUtc).Take(MaxActivityRetention));
                }
            }
            catch
            {
                // If the file is unreadable or corrupted, start fresh
            }
        }
    }

    public IReadOnlyList<ActivityRecord> GetActivities()
    {
        lock (_lock)
        {
            return _activities.ToList();
        }
    }

    public void LogActivity(string activity, string? category = null, DateTime? timestampUtc = null)
    {
        if (string.IsNullOrWhiteSpace(activity))
            return;

        var now = timestampUtc ?? DateTime.UtcNow;

        lock (_lock)
        {
            // Debounce duplicate events within 3 seconds (e.g. multiple FileSystemWatcher changed events for the same file)
            if (string.Equals(_lastLoggedActivity, activity, StringComparison.OrdinalIgnoreCase) &&
                (now - _lastLoggedUtc).TotalSeconds < 3.0)
            {
                return;
            }

            _lastLoggedActivity = activity;
            _lastLoggedUtc = now;

            var record = new ActivityRecord
            {
                Activity = activity.Trim(),
                TimestampUtc = now,
                Category = category
            };

            _activities.Insert(0, record);
            if (_activities.Count > MaxActivityRetention)
            {
                _activities.RemoveRange(MaxActivityRetention, _activities.Count - MaxActivityRetention);
            }

            ActivityAdded?.Invoke(this, record);
            ScheduleSave();
        }
    }

    private void ScheduleSave()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _debounceSaveTimer?.Dispose();
            _debounceSaveTimer = new Timer(_ =>
            {
                _ = SaveAsync();
            }, null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
        }
    }

    public async Task SaveAsync()
    {
        List<ActivityRecord> snapshot;
        lock (_lock)
        {
            snapshot = _activities.ToList();
        }

        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tempPath = _filePath + $".{Guid.NewGuid():N}.tmp";
            var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
            await File.WriteAllTextAsync(tempPath, json);

            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch
        {
            // Suppress save failures on disk edge cases
        }
    }

    /// <summary>
    /// Formats a timestamp into human-readable relative duration matching user specification:
    /// e.g. "just now", "5min", "8min", "1h", "1h 30m", "2h", "1d".
    /// </summary>
    public static string FormatRelativeTime(DateTime timestampUtc, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var diff = now - timestampUtc;
        if (diff < TimeSpan.Zero)
            diff = TimeSpan.Zero;

        if (diff.TotalMinutes < 1)
        {
            return "just now";
        }

        if (diff.TotalHours < 1)
        {
            var mins = (int)diff.TotalMinutes;
            return $"{mins}min";
        }

        if (diff.TotalDays < 1)
        {
            var hours = (int)diff.TotalHours;
            var remainingMins = diff.Minutes;
            return remainingMins > 0 ? $"{hours}h {remainingMins}m" : $"{hours}h";
        }

        var days = (int)diff.TotalDays;
        var remainingHours = diff.Hours;
        return remainingHours > 0 ? $"{days}d {remainingHours}h" : $"{days}d";
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _debounceSaveTimer?.Dispose();
        _debounceSaveTimer = null;
        try
        {
            // Final synchronous save
            SaveAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }
    }
}

