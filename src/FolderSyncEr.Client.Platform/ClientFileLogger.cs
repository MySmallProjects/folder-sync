using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace FolderSyncEr.Client.Platform;

/// <summary>
/// Rolling daily file logger for the FolderSyncEr desktop client.
/// Writes one log file per day (app-yyyy-MM-dd.log) inside the "Logs" folder
/// under the application configuration directory and retains up to 30 days of logs.
/// </summary>
public sealed class ClientFileLoggerProvider : ILoggerProvider
{
    public const int DefaultRetentionDays = 30;
    private const string LogFilePrefix = "app-";
    private const string LogFileExtension = ".log";

    private readonly string _logsDirectory;
    private readonly int _retentionDays;
    private readonly Func<DateTime> _utcNowProvider;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, ClientFileLogger> _loggers = new(StringComparer.Ordinal);
    private string? _lastCleanupDateStamp;

    public string LogsDirectory => _logsDirectory;

    public ClientFileLoggerProvider(
        string? logsDirectory = null,
        int retentionDays = DefaultRetentionDays,
        Func<DateTime>? utcNowProvider = null)
    {
        _logsDirectory = logsDirectory ?? GetDefaultLogsDirectory();
        _retentionDays = retentionDays > 0 ? retentionDays : DefaultRetentionDays;
        _utcNowProvider = utcNowProvider ?? (() => DateTime.UtcNow);

        EnsureDirectoryExists(_logsDirectory);
        CleanupOldLogs();
    }

    public static string GetDefaultLogsDirectory()
    {
        var settingsPath = ClientSettingsManager.GetDefaultSettingsFilePath();
        var configDir = Path.GetDirectoryName(settingsPath) ?? Path.Combine(Path.GetTempPath(), "foldersyncer");
        var logsDir = Path.Combine(configDir, "Logs");
        EnsureDirectoryExists(logsDir);
        return logsDir;
    }

    public static string GetCurrentLogFilePath(string? customLogsDirectory = null, DateTime? utcNow = null)
    {
        var dir = customLogsDirectory ?? GetDefaultLogsDirectory();
        EnsureDirectoryExists(dir);
        var dateStamp = (utcNow ?? DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Path.Combine(dir, $"{LogFilePrefix}{dateStamp}{LogFileExtension}");
    }

    public string GetCurrentLogPath()
    {
        return GetCurrentLogFilePath(_logsDirectory, _utcNowProvider());
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new ClientFileLogger(name, this));
    }

    internal void WriteEntry(string categoryName, LogLevel logLevel, string message, Exception? exception)
    {
        // Suppress built-in HttpClient pipeline chatter since FileSyncApiClient / AuthHeaderHandler log API calls explicitly
        if (categoryName.StartsWith("System.Net.Http.HttpClient", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var now = _utcNowProvider();
        var dateStamp = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var filePath = Path.Combine(_logsDirectory, $"{LogFilePrefix}{dateStamp}{LogFileExtension}");

        var shortCategory = GetShortCategory(categoryName);
        var levelTag = GetLevelTag(logLevel);
        var timestamp = now.ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.Append(timestamp)
          .Append(" [").Append(levelTag).Append("] ")
          .Append('[').Append(shortCategory).Append("] ")
          .AppendLine(message);

        if (exception != null)
        {
            sb.AppendLine($"  Exception: {exception.GetType().Name}: {exception.Message}");
        }

        lock (_writeLock)
        {
            try
            {
                EnsureDirectoryExists(_logsDirectory);
                File.AppendAllText(filePath, sb.ToString(), Encoding.UTF8);

                if (!string.Equals(_lastCleanupDateStamp, dateStamp, StringComparison.Ordinal))
                {
                    _lastCleanupDateStamp = dateStamp;
                    CleanupOldLogsInternal(now.Date);
                }
            }
            catch
            {
                // Never throw from logger if disk is temporarily inaccessible
            }
        }
    }

    public void CleanupOldLogs()
    {
        lock (_writeLock)
        {
            CleanupOldLogsInternal(_utcNowProvider().Date);
        }
    }

    private void CleanupOldLogsInternal(DateTime todayUtcDate)
    {
        try
        {
            if (!Directory.Exists(_logsDirectory))
            {
                return;
            }

            // Keep today plus the previous (_retentionDays - 1) days = _retentionDays daily files total
            var cutoffDate = todayUtcDate.AddDays(-(_retentionDays - 1));

            foreach (var file in Directory.EnumerateFiles(_logsDirectory, $"{LogFilePrefix}*{LogFileExtension}"))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (fileName.StartsWith(LogFilePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var datePart = fileName.Substring(LogFilePrefix.Length);
                    if (DateTime.TryParseExact(
                        datePart,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var fileDate))
                    {
                        if (fileDate.Date < cutoffDate)
                        {
                            try { File.Delete(file); } catch { }
                        }
                        continue;
                    }
                }

                // Fallback for non-date-matching files: check LastWriteTimeUtc
                try
                {
                    if (File.GetLastWriteTimeUtc(file).Date < cutoffDate)
                    {
                        File.Delete(file);
                    }
                }
                catch { }
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private static void EnsureDirectoryExists(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        catch { }
    }

    private static string GetShortCategory(string category)
    {
        var lastDot = category.LastIndexOf('.');
        return lastDot >= 0 && lastDot < category.Length - 1
            ? category.Substring(lastDot + 1)
            : category;
    }

    private static string GetLevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "INF"
    };

    public void Dispose()
    {
        _loggers.Clear();
    }

    private sealed class ClientFileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly ClientFileLoggerProvider _provider;

        public ClientFileLogger(string categoryName, ClientFileLoggerProvider provider)
        {
            _categoryName = categoryName;
            _provider = provider;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception == null)
            {
                return;
            }

            _provider.WriteEntry(_categoryName, logLevel, message, exception);
        }
    }
}

