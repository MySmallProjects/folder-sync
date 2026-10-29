using System;

namespace FolderSyncEr.Client.Platform;

public class ClientSettings
{
    public string ServerUrl { get; set; } = "http://localhost:5000";
    public bool AllowUntrustedCertificates { get; set; } = false;
    public string? SyncFolderPath { get; set; }
    public string ConflictPolicy { get; set; } = "KeepBoth";
    public int SyncIntervalSeconds { get; set; } = 60;
    public int UpdateCooldownSeconds { get; set; } = 30;
    public int MaxWaitSeconds { get; set; } = 300;
    public bool CloseToTray { get; set; } = true;
    public bool StartMinimized { get; set; } = false;
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
}

