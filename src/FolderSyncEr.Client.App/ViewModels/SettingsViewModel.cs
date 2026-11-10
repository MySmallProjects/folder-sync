using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderSyncEr.Client.Api;
using FolderSyncEr.Client.Platform;
using FolderSyncEr.Client.Sync.Conflicts;
using FolderSyncEr.Contracts;

namespace FolderSyncEr.Client.App.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly IFileSyncApi _api;
    private readonly ISecretStore _secretStore;
    private readonly ClientSettingsManager _settingsManager;
    private readonly Func<string?> _getSyncFolder;
    private readonly Func<string, Task> _setSyncFolder;
    private readonly Action<ConflictPolicy> _setConflictPolicy;

    [ObservableProperty]
    private string _currentSyncFolder = string.Empty;

    partial void OnCurrentSyncFolderChanged(string value)
    {
        if (!ShowFolderChangeConfirmation)
        {
            NewSyncFolderInput = value;
        }
    }

    [ObservableProperty]
    private string _newSyncFolderInput = string.Empty;

    [ObservableProperty]
    private string _serverUrl = string.Empty;

    [ObservableProperty]
    private bool _allowUntrustedCertificates;

    [ObservableProperty]
    private ConflictPolicy _selectedConflictPolicy = ConflictPolicy.KeepBoth;

    [ObservableProperty]
    private int _syncIntervalSeconds = 60;

    [ObservableProperty]
    private int _updateCooldownSeconds = 30;

    [ObservableProperty]
    private int _maxWaitSeconds = 300;

    [ObservableProperty]
    private bool _closeToTray = true;

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private bool _showFilesTab = true;

    [ObservableProperty]
    private bool _showDeletedTab = true;

    [ObservableProperty]
    private bool _showServerLogsTab = true;

    [ObservableProperty]
    private bool _showNotesTab = true;

    partial void OnShowFilesTabChanged(bool value)
    {
        if (IsInitialized) _ = PersistCurrentSettingsAsync();
    }

    partial void OnShowDeletedTabChanged(bool value)
    {
        if (IsInitialized) _ = PersistCurrentSettingsAsync();
    }

    partial void OnShowServerLogsTabChanged(bool value)
    {
        if (IsInitialized) _ = PersistCurrentSettingsAsync();
    }

    partial void OnShowNotesTabChanged(bool value)
    {
        if (IsInitialized) _ = PersistCurrentSettingsAsync();
    }

    [ObservableProperty]
    private bool _showFolderChangeConfirmation;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isDownloadingArchive;

    [ObservableProperty]
    private string _settingsFilePath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenSettingsStatus))]
    private string? _openSettingsStatus;

    public bool HasOpenSettingsStatus => !string.IsNullOrWhiteSpace(OpenSettingsStatus);

    [ObservableProperty]
    private string _currentLogFilePath = string.Empty;

    [ObservableProperty]
    private string _secretStoreDescription = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenLogStatus))]
    private string? _openLogStatus;

    public bool HasOpenLogStatus => !string.IsNullOrWhiteSpace(OpenLogStatus);

    [ObservableProperty]
    private string _appVersion = "1.0.0";

    [ObservableProperty]
    private string _appCompileDateTime = "-";

    public IReadOnlyList<ConflictPolicy> AvailablePolicies { get; } = new[]
    {
        ConflictPolicy.KeepBoth,
        ConflictPolicy.NewestWins,
        ConflictPolicy.Prompt
    };

    public SettingsViewModel(
        IFileSyncApi api,
        ISecretStore secretStore,
        ClientSettingsManager settingsManager,
        Func<string?> getSyncFolder,
        Func<string, Task> setSyncFolder,
        Action<ConflictPolicy> setConflictPolicy)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _settingsManager = settingsManager ?? new ClientSettingsManager();
        _getSyncFolder = getSyncFolder ?? throw new ArgumentNullException(nameof(getSyncFolder));
        _setSyncFolder = setSyncFolder ?? throw new ArgumentNullException(nameof(setSyncFolder));
        _setConflictPolicy = setConflictPolicy ?? throw new ArgumentNullException(nameof(setConflictPolicy));

        CurrentSyncFolder = _getSyncFolder() ?? string.Empty;
        NewSyncFolderInput = CurrentSyncFolder;
        ServerUrl = _api.ServerUrl;
        SettingsFilePath = _settingsManager.SettingsFilePath;
        var configDir = Path.GetDirectoryName(_settingsManager.SettingsFilePath);
        var logsDir = !string.IsNullOrEmpty(configDir) ? Path.Combine(configDir, "Logs") : null;
        CurrentLogFilePath = ClientFileLoggerProvider.GetCurrentLogFilePath(logsDir);
        SecretStoreDescription = _secretStore.Description;

        var appAssembly = typeof(SettingsViewModel).Assembly;
        AppVersion = BuildInfo.GetVersionString(appAssembly);
        AppCompileDateTime = BuildInfo.GetBuildDateUtc(appAssembly).ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
        ExecutablePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs().FirstOrDefault() ?? AppContext.BaseDirectory;
    }

    public string ExecutablePath { get; }

    public bool IsInitialized { get; set; }

    public SettingsViewModel(
        IFileSyncApi api,
        ISecretStore secretStore,
        Func<string?> getSyncFolder,
        Func<string, Task> setSyncFolder,
        Action<ConflictPolicy> setConflictPolicy)
        : this(api, secretStore, new ClientSettingsManager(), getSyncFolder, setSyncFolder, setConflictPolicy)
    {
    }

    public SettingsViewModel(
        IFileSyncApi api,
        ISecretStore secretStore,
        Func<string?> getSyncFolder,
        Action<string> setSyncFolder,
        Action<ConflictPolicy> setConflictPolicy)
        : this(api, secretStore, new ClientSettingsManager(), getSyncFolder, folder => { setSyncFolder(folder); return Task.CompletedTask; }, setConflictPolicy)
    {
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerUrlStatusMessage))]
    private string _serverUrlStatusMessage = string.Empty;

    public bool HasServerUrlStatusMessage => !string.IsNullOrWhiteSpace(ServerUrlStatusMessage);

    partial void OnSelectedConflictPolicyChanged(ConflictPolicy value)
    {
        _setConflictPolicy(value);
        StatusMessage = $"Conflict policy changed to: {value}";
        if (IsInitialized)
        {
            _ = PersistCurrentSettingsAsync();
        }
    }

    partial void OnAllowUntrustedCertificatesChanged(bool value)
    {
        if (IsInitialized)
        {
            _ = PersistCurrentSettingsAsync();
        }
    }

    partial void OnSyncIntervalSecondsChanged(int value)
    {
        if (IsInitialized)
        {
            _ = PersistCurrentSettingsAsync();
        }
    }

    partial void OnUpdateCooldownSecondsChanged(int value)
    {
        if (IsInitialized)
        {
            _ = PersistCurrentSettingsAsync();
        }
    }

    partial void OnMaxWaitSecondsChanged(int value)
    {
        if (IsInitialized)
        {
            _ = PersistCurrentSettingsAsync();
        }
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (IsInitialized)
        {
            _ = PersistCurrentSettingsAsync();
        }
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        if (IsInitialized)
        {
            _ = PersistCurrentSettingsAsync();
        }
    }

    [RelayCommand]
    public async Task SaveServerUrlAsync()
    {
        ServerUrlStatusMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(ServerUrl))
        {
            ServerUrlStatusMessage = "Server URL cannot be empty.";
            return;
        }

        var trimmed = ServerUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ServerUrlStatusMessage = "Invalid URL. Please enter a valid HTTP or HTTPS URL (e.g. http://localhost:5000).";
            return;
        }

        _api.ServerUrl = trimmed;
        ServerUrl = trimmed;
        await _secretStore.SetSecretAsync("ConfiguredServerUrl", trimmed);
        await PersistCurrentSettingsAsync();
        ServerUrlStatusMessage = $"Server URL saved: {trimmed}";
    }

    [RelayCommand]
    public async Task OpenSettingsFileAsync()
    {
        OpenSettingsStatus = null;
        if (string.IsNullOrWhiteSpace(SettingsFilePath) || !File.Exists(SettingsFilePath))
        {
            await PersistCurrentSettingsAsync();
        }

        var success = ClientSettingsManager.OpenSettingsFileInDefaultViewer(SettingsFilePath);
        if (success)
        {
            OpenSettingsStatus = "Settings file opened in default viewer.";
        }
        else
        {
            OpenSettingsStatus = $"File path: {SettingsFilePath}";
        }
    }

    [RelayCommand]
    public void OpenLogFile()
    {
        OpenLogStatus = null;
        var configDir = Path.GetDirectoryName(_settingsManager.SettingsFilePath);
        var logsDir = !string.IsNullOrEmpty(configDir) ? Path.Combine(configDir, "Logs") : null;
        CurrentLogFilePath = ClientFileLoggerProvider.GetCurrentLogFilePath(logsDir);

        try
        {
            var dir = Path.GetDirectoryName(CurrentLogFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            if (!File.Exists(CurrentLogFilePath))
            {
                File.WriteAllText(CurrentLogFilePath, string.Empty);
            }
        }
        catch { }

        var success = ClientSettingsManager.OpenSettingsFileInDefaultViewer(CurrentLogFilePath);
        if (success)
        {
            OpenLogStatus = "Log file opened in default viewer.";
        }
        else
        {
            OpenLogStatus = $"Log file path: {CurrentLogFilePath}";
        }
    }

    public async Task PersistCurrentSettingsAsync()
    {
        try
        {
            var settings = await _settingsManager.LoadSettingsAsync();
            if (!string.IsNullOrWhiteSpace(ServerUrl))
            {
                settings.ServerUrl = ServerUrl;
            }
            settings.AllowUntrustedCertificates = AllowUntrustedCertificates;
            if (!string.IsNullOrWhiteSpace(CurrentSyncFolder))
            {
                settings.SyncFolderPath = CurrentSyncFolder;
            }
            settings.ConflictPolicy = SelectedConflictPolicy.ToString();
            settings.SyncIntervalSeconds = SyncIntervalSeconds;
            settings.UpdateCooldownSeconds = UpdateCooldownSeconds;
            settings.MaxWaitSeconds = MaxWaitSeconds;
            settings.CloseToTray = CloseToTray;
            settings.StartMinimized = StartMinimized;
            settings.ShowFilesTab = ShowFilesTab;
            settings.ShowDeletedTab = ShowDeletedTab;
            settings.ShowServerLogsTab = ShowServerLogsTab;
            settings.ShowNotesTab = ShowNotesTab;
            settings.LastUpdatedUtc = DateTime.UtcNow;
            await _settingsManager.SaveSettingsAsync(settings);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SETTINGS PERSIST ERROR] {ex.Message}");
        }
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

        if (string.Equals(fullPath, CurrentSyncFolder, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "Folder is already set to this path.";
            return;
        }

        // Show confirmation explaining what happens to existing sync state
        ShowFolderChangeConfirmation = true;
    }

    [RelayCommand]
    public async Task ConfirmChangeFolderAsync()
    {
        var fullPath = Path.GetFullPath(NewSyncFolderInput.Trim());
        await _setSyncFolder(fullPath);
        CurrentSyncFolder = fullPath;
        NewSyncFolderInput = fullPath;
        ShowFolderChangeConfirmation = false;
        await PersistCurrentSettingsAsync();
        StatusMessage = $"Sync folder successfully switched to: {fullPath}";
    }

    public void ConfirmChangeFolder() => ConfirmChangeFolderAsync().GetAwaiter().GetResult();

    [RelayCommand]
    public void CancelChangeFolder()
    {
        ShowFolderChangeConfirmation = false;
        NewSyncFolderInput = CurrentSyncFolder;
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
