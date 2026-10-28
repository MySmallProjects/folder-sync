using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderSyncEr.Client.Api;
using FolderSyncEr.Client.Platform;
using FolderSyncEr.Client.Sync.Conflicts;

namespace FolderSyncEr.Client.App.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly IFileSyncApi _api;
    private readonly ISecretStore _secretStore;
    private readonly Func<string?> _getSyncFolder;
    private readonly Action<string> _setSyncFolder;
    private readonly Action<ConflictPolicy> _setConflictPolicy;

    [ObservableProperty]
    private string _currentSyncFolder = string.Empty;

    [ObservableProperty]
    private string _newSyncFolderInput = string.Empty;

    [ObservableProperty]
    private string _serverUrl = string.Empty;

    [ObservableProperty]
    private ConflictPolicy _selectedConflictPolicy = ConflictPolicy.KeepBoth;

    [ObservableProperty]
    private int _syncIntervalSeconds = 60;

    [ObservableProperty]
    private bool _closeToTray = true;

    [ObservableProperty]
    private bool _showFolderChangeConfirmation;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isDownloadingArchive;

    public IReadOnlyList<ConflictPolicy> AvailablePolicies { get; } = new[]
    {
        ConflictPolicy.KeepBoth,
        ConflictPolicy.NewestWins,
        ConflictPolicy.Prompt
    };

    public SettingsViewModel(
        IFileSyncApi api,
        ISecretStore secretStore,
        Func<string?> getSyncFolder,
        Action<string> setSyncFolder,
        Action<ConflictPolicy> setConflictPolicy)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _getSyncFolder = getSyncFolder ?? throw new ArgumentNullException(nameof(getSyncFolder));
        _setSyncFolder = setSyncFolder ?? throw new ArgumentNullException(nameof(setSyncFolder));
        _setConflictPolicy = setConflictPolicy ?? throw new ArgumentNullException(nameof(setConflictPolicy));

        CurrentSyncFolder = _getSyncFolder() ?? string.Empty;
        ServerUrl = _api.ServerUrl;
    }

    partial void OnSelectedConflictPolicyChanged(ConflictPolicy value)
    {
        _setConflictPolicy(value);
        StatusMessage = $"Conflict policy changed to: {value}";
    }

    [RelayCommand]
    public void RequestChangeFolder()
    {
        if (string.IsNullOrWhiteSpace(NewSyncFolderInput))
        {
            StatusMessage = "Please enter a valid directory path.";
            return;
        }

        var fullPath = Path.GetFullPath(NewSyncFolderInput.Trim());
        if (!Directory.Exists(fullPath))
        {
            StatusMessage = "Specified folder does not exist.";
            return;
        }

        // Show confirmation explaining what happens to existing sync state
        ShowFolderChangeConfirmation = true;
    }

    [RelayCommand]
    public void ConfirmChangeFolder()
    {
        var fullPath = Path.GetFullPath(NewSyncFolderInput.Trim());
        _setSyncFolder(fullPath);
        CurrentSyncFolder = fullPath;
        NewSyncFolderInput = string.Empty;
        ShowFolderChangeConfirmation = false;
        StatusMessage = $"Sync folder successfully switched to: {fullPath}";
    }

    [RelayCommand]
    public void CancelChangeFolder()
    {
        ShowFolderChangeConfirmation = false;
        NewSyncFolderInput = string.Empty;
    }

    [RelayCommand]
    public async Task DownloadArchiveAsync(string? customTargetDirectory = null)
    {
        IsDownloadingArchive = true;
        StatusMessage = "Exporting server files archive...";
        try
        {
            using var stream = await _api.ExportFolderZipAsync();
            var targetDir = !string.IsNullOrWhiteSpace(customTargetDirectory)
                ? customTargetDirectory
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

            try
            {
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }
            }
            catch
            {
                // Fallback to temp path if user home is read-only or inaccessible
                targetDir = Path.GetTempPath();
            }

            var targetPath = Path.Combine(targetDir, $"foldersyncer-export-{DateTime.UtcNow:yyyyMMddHHmmss}.zip");

            using var file = File.Create(targetPath);
            await stream.CopyToAsync(file);

            StatusMessage = $"Server archive saved to: {targetPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to export archive: {ex.Message}";
        }
        finally
        {
            IsDownloadingArchive = false;
        }
    }
}
