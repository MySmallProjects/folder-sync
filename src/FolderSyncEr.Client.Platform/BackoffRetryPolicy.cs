using System;

namespace FolderSyncEr.Client.Platform;

/// <summary>
/// Implements Truncated Exponential Backoff with Jitter for network and server connection retries.
/// Formula: delay = min(MaxInterval, BaseInterval * Multiplier^(attempt - 1)) * (1 +/- JitterRatio)
/// </summary>
public class BackoffRetryPolicy
{
    private readonly object _lock = new();
    private readonly Random _rng;

    public TimeSpan BaseInterval { get; }
    public TimeSpan MaxInterval { get; }
    public double Multiplier { get; }
    public double JitterRatio { get; }

    public int ConsecutiveFailures { get; private set; }
    public DateTime? NextRetryUtc { get; private set; }
    public TimeSpan? CurrentDelay { get; private set; }

    public bool IsInBackoff => ConsecutiveFailures > 0;

    public BackoffRetryPolicy(
        TimeSpan? baseInterval = null,
        TimeSpan? maxInterval = null,
        double multiplier = 2.0,
        double jitterRatio = 0.10,
        Random? rng = null)
    {
        BaseInterval = baseInterval ?? TimeSpan.FromMinutes(1);
        MaxInterval = maxInterval ?? TimeSpan.FromMinutes(60);
        Multiplier = multiplier > 1.0 ? multiplier : 2.0;
        JitterRatio = Math.Clamp(jitterRatio, 0.0, 0.5);
        _rng = rng ?? new Random();
    }

    /// <summary>
    /// Calculates the delay for a given attempt number without modifying state.
    /// Uses deterministic RNG if provided.
    /// </summary>
    public TimeSpan CalculateDelay(int attempt, Random? rng = null)
    {
        if (attempt <= 0)
            return TimeSpan.Zero;

        // Base exponential calculation: BaseInterval * Multiplier^(attempt - 1)
        double exponent = attempt - 1;
        double factor = Math.Pow(Multiplier, exponent);
        double rawSeconds = BaseInterval.TotalSeconds * factor;

        // Truncate at MaxInterval cap
        double cappedSeconds = Math.Min(MaxInterval.TotalSeconds, rawSeconds);

        // Apply +/- JitterRatio (e.g. +/- 10% -> between 0.9 and 1.1)
        double jitterMultiplier;
        if (JitterRatio > 0.0)
        {
            var random = rng ?? _rng;
            lock (_lock)
            {
                double r = random.NextDouble(); // [0.0, 1.0)
                jitterMultiplier = (1.0 - JitterRatio) + (r * 2.0 * JitterRatio);
            }
        }
        else
        {
            jitterMultiplier = 1.0;
        }

        double jitteredSeconds = Math.Round(cappedSeconds * jitterMultiplier, 1);
        jitteredSeconds = Math.Max(1.0, jitteredSeconds);

        return TimeSpan.FromSeconds(jitteredSeconds);
    }

    /// <summary>
    /// Records a failed connection attempt and calculates the next scheduled retry timestamp.
    /// </summary>
    public TimeSpan RecordFailure(DateTime? nowUtc = null)
    {
        lock (_lock)
        {
            ConsecutiveFailures++;
            var delay = CalculateDelay(ConsecutiveFailures);
            CurrentDelay = delay;
            var now = nowUtc ?? DateTime.UtcNow;
            NextRetryUtc = now.Add(delay);
            return delay;
        }
    }

    /// <summary>
    /// Resets the retry policy upon a successful connection or manual user action.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            ConsecutiveFailures = 0;
            NextRetryUtc = null;
            CurrentDelay = null;
        }
    }

    /// <summary>
    /// Returns the remaining time until the next scheduled retry, or null if not currently in backoff.
    /// </summary>
    public TimeSpan? GetRemainingTime(DateTime? nowUtc = null)
    {
        lock (_lock)
        {
            if (NextRetryUtc == null)
                return null;

            var now = nowUtc ?? DateTime.UtcNow;
            var remaining = NextRetryUtc.Value - now;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Formats a remaining TimeSpan into a readable countdown string: mm:ss or hh:mm:ss.
    /// </summary>
    public static string FormatCountdown(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;

        return remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    /// <summary>
    /// Formats an interval into an approximate human-readable notation (e.g. 1m, 2m, 1h).
    /// </summary>
    public static string FormatInterval(TimeSpan? interval)
    {
        if (interval == null) return "1m";
        var ts = interval.Value;
        if (ts.TotalMinutes < 1) return $"{(int)ts.TotalSeconds}s";
        if (ts.TotalHours < 1) return $"{Math.Round(ts.TotalMinutes)}m";
        return $"{Math.Round(ts.TotalHours, 1)}h";
    }
}

