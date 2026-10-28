using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace FolderSyncEr.Client.Sync.Registry;

/// <summary>
/// Thread-safe registry tracking failed file operations within an active sync run.
/// Prevents SharpSync from incorrectly stamping failed transfers as 'Synced' (Mitigating Risk R1).
/// </summary>
public sealed class RunFailureRegistry
{
    private readonly ConcurrentDictionary<string, Exception> _failures = new(StringComparer.OrdinalIgnoreCase);

    public void RecordFailure(string path, Exception exception)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalized = Normalize(path);
        _failures[normalized] = exception;
    }

    public bool HasFailed(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return _failures.ContainsKey(Normalize(path));
    }

    public bool TryGetFailure(string path, out Exception? exception)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            exception = null;
            return false;
        }

        return _failures.TryGetValue(Normalize(path), out exception);
    }

    public void Clear()
    {
        _failures.Clear();
    }

    public IReadOnlyDictionary<string, Exception> GetAllFailures() => _failures;

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
}

