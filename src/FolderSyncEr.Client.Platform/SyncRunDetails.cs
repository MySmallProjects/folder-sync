using System;
using System.Collections.Generic;

namespace FolderSyncEr.Client.Platform;

public class SyncRunDetails
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public DateTime? TimestampStartUtc { get; set; }
    public int RepeatCount { get; set; } = 1;
    public double DurationSeconds { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }

    public List<string> UploadedFiles { get; set; } = new();
    public List<string> DownloadedFiles { get; set; } = new();
    public List<string> VerifiedFiles { get; set; } = new();
    public List<string> ConflictedFiles { get; set; } = new();
    public List<string> DeletedFiles { get; set; } = new();

    public int TotalItemsProcessed => UploadedFiles.Count + DownloadedFiles.Count + ConflictedFiles.Count + VerifiedFiles.Count;

    public bool HasUploadedFiles => UploadedFiles.Count > 0;
    public bool HasDownloadedFiles => DownloadedFiles.Count > 0;
    public bool HasConflictedFiles => ConflictedFiles.Count > 0;
    public bool HasVerifiedFiles => VerifiedFiles.Count > 0;
    public bool HasDeletedFiles => DeletedFiles.Count > 0;

    public bool HasDateRange => TimestampStartUtc.HasValue && TimestampStartUtc.Value != TimestampUtc && RepeatCount > 1;
    public string TimestampFormatted => TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
    public string TimestampStartFormatted => TimestampStartUtc?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? string.Empty;
    public string RunsCountText => RepeatCount > 1 ? $"{RepeatCount} runs" : string.Empty;
    public string DurationFormatted => $"{DurationSeconds:0.##}s";
    public string StatusText => Success ? "Successful" : (string.IsNullOrWhiteSpace(ErrorMessage) ? "Failed" : $"Failed: {ErrorMessage}");

    public string UploadedHeader => $"Uploaded to Server ({UploadedFiles.Count})";
    public string DownloadedHeader => $"Downloaded from Server ({DownloadedFiles.Count})";
    public string VerifiedHeader => $"Verified / Up to Date ({VerifiedFiles.Count})";
    public string ConflictedHeader => $"Conflicts ({ConflictedFiles.Count})";
}

