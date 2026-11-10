using System;
using System.Collections.Generic;

namespace FolderSyncEr.Client.Platform;

public class SyncRunDetails
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
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

    public string TimestampFormatted => TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
    public string DurationFormatted => $"{DurationSeconds:0.##}s";
    public string StatusText => Success ? "Successful" : (string.IsNullOrWhiteSpace(ErrorMessage) ? "Failed" : $"Failed: {ErrorMessage}");

    public string UploadedHeader => $"Uploaded to Server ({UploadedFiles.Count})";
    public string DownloadedHeader => $"Downloaded from Server ({DownloadedFiles.Count})";
    public string VerifiedHeader => $"Verified / Up to Date ({VerifiedFiles.Count})";
    public string ConflictedHeader => $"Conflicts ({ConflictedFiles.Count})";
}

