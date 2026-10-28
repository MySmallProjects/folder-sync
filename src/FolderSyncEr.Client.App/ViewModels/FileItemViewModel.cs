using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FolderSyncEr.Client.App.ViewModels;

public partial class FileItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _path = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _localVersion = "-";

    [ObservableProperty]
    private string _serverVersion = "-";

    [ObservableProperty]
    private DateTime? _localModifiedUtc;

    [ObservableProperty]
    private DateTime? _serverModifiedUtc;

    [ObservableProperty]
    private long _localSize;

    [ObservableProperty]
    private long _serverSize;

    [ObservableProperty]
    private string _status = "In sync";

    [ObservableProperty]
    private string _statusColor = "#10B981";

    [ObservableProperty]
    private Avalonia.Media.IBrush _statusBrush = Avalonia.Media.Brush.Parse("#10B981");

    partial void OnStatusColorChanged(string value)
    {
        try
        {
            StatusBrush = Avalonia.Media.Brush.Parse(value);
        }
        catch
        {
            StatusBrush = Avalonia.Media.Brush.Parse("#9CA3AF");
        }
    }

    public string LocalSizeFormatted => FormatBytes(LocalSize);
    public string ServerSizeFormatted => FormatBytes(ServerSize);

    public string LocalModifiedText => LocalModifiedUtc?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "-";
    public string ServerModifiedText => ServerModifiedUtc?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "-";

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        double val = bytes;
        while (val >= 1024 && i < units.Length - 1)
        {
            val /= 1024;
            i++;
        }
        return $"{val:0.#} {units[i]}";
    }
}

