using System;

namespace FolderSyncEr.Client.Platform;

public class ClientSettings
{
    public string ServerUrl { get; set; } = "http://localhost:5000";
    public string? SyncFolderPath { get; set; }
    public string ConflictPolicy { get; set; } = "KeepBoth";
    public int SyncIntervalSeconds { get; set; } = 60;
    public bool CloseToTray { get; set; } = true;
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
}

