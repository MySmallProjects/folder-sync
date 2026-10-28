using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderSyncEr.Client.Sync.Conflicts;

namespace FolderSyncEr.Client.App.ViewModels;

public partial class ConflictPromptViewModel : ViewModelBase, IConflictPromptHandler
{
    private TaskCompletionSource<ConflictResolutionChoice>? _tcs;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _localInfoText = string.Empty;

    [ObservableProperty]
    private string _remoteInfoText = string.Empty;

    [ObservableProperty]
    private string _deviceInfoText = string.Empty;

    public Task<ConflictResolutionChoice> RequestResolutionAsync(ConflictPromptInfo prompt, CancellationToken cancellationToken)
    {
        _tcs = new TaskCompletionSource<ConflictResolutionChoice>();

        void ShowPrompt()
        {
            FilePath = prompt.Path;
            LocalInfoText = $"Size: {FormatBytes(prompt.LocalSize)} | Modified: {prompt.LocalModifiedUtc:yyyy-MM-dd HH:mm:ss} UTC | Hash: {FormatHash(prompt.LocalHash)}";
            RemoteInfoText = $"Size: {FormatBytes(prompt.RemoteSize)} | Modified: {prompt.RemoteModifiedUtc:yyyy-MM-dd HH:mm:ss} UTC | Hash: {FormatHash(prompt.RemoteHash)}";
            DeviceInfoText = $"Conflict originating from device: {prompt.DeviceName}";
            IsVisible = true;
        }

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            ShowPrompt();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(ShowPrompt);
        }

        cancellationToken.Register(() =>
        {
            void HidePrompt() => IsVisible = false;
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                HidePrompt();
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(HidePrompt);
            }
            _tcs.TrySetCanceled(cancellationToken);
        });

        return _tcs.Task;
    }

    [RelayCommand]
    private void KeepLocal()
    {
        IsVisible = false;
        _tcs?.TrySetResult(ConflictResolutionChoice.KeepLocal);
    }

    [RelayCommand]
    private void KeepServer()
    {
        IsVisible = false;
        _tcs?.TrySetResult(ConflictResolutionChoice.KeepServer);
    }

    [RelayCommand]
    private void KeepBoth()
    {
        IsVisible = false;
        _tcs?.TrySetResult(ConflictResolutionChoice.KeepBoth);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB" };
        int i = 0;
        double val = bytes;
        while (val >= 1024 && i < units.Length - 1)
        {
            val /= 1024;
            i++;
        }
        return $"{val:0.#} {units[i]}";
    }

    private static string FormatHash(string? hash)
    {
        if (string.IsNullOrEmpty(hash)) return "-";
        return hash.Length > 12 ? hash[..12] + "..." : hash;
    }
}

