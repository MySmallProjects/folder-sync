using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderSyncEr.Client.Api;
using FolderSyncEr.Client.Platform;
using FolderSyncEr.Client.Sync.Conflicts;
using FolderSyncEr.Client.Sync.Coordinator;
using Microsoft.Extensions.Logging;

namespace FolderSyncEr.Client.App.ViewModels;

public enum TrayIconState
{
    Disconnected,
    Active,
    Synced
}

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly ISecretStore _secretStore;
    private readonly IFileSyncApi _api;
    private readonly ClientSettingsManager _settingsManager;
    private readonly ILoggerFactory? _loggerFactory;
    private bool _isInitializing;

    public ConnectionCheckViewModel Connection { get; }
    public LoginViewModel Login { get; }
    public FolderSelectionViewModel Folder { get; }
    public FileListViewModel FileList { get; }
    public DeletedFilesViewModel DeletedFiles { get; }
    public LogsViewModel Logs { get; }
    public SettingsViewModel Settings { get; }
    public ConflictPromptViewModel ConflictPrompt { get; }

    public SyncCoordinator? Coordinator { get; private set; }

    [ObservableProperty]
    private int _selectedTabIndex;

    // Tab order in MainWindow.axaml
    public const int FilesTabIndex = 0;
    public const int DeletedFilesTabIndex = 1;
    public const int LogsTabIndex = 2;
    public const int AccountTabIndex = 4;

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == DeletedFilesTabIndex && Login.IsLoggedIn)
        {
            _ = DeletedFiles.RefreshCommand.ExecuteAsync(null);
        }
        else if (value == LogsTabIndex && Login.IsLoggedIn && Connection.IsConnected)
        {
            _ = Logs.RefreshCommand.ExecuteAsync(null);
        }
        else if (value == AccountTabIndex && Login.IsLoggedIn)
        {
            _ = Login.RefreshDevicesCommand.ExecuteAsync(null);
        }
    }

    [ObservableProperty]
    private string _secretStoreDescription;

    [ObservableProperty]
    private bool _isSecretStoreInsecure;

    [ObservableProperty]
    private string _syncStatusSummary = "Ready";

    [ObservableProperty]
    private bool _showElevationWarning;

    [ObservableProperty]
    private string _elevationWarningText = string.Empty;

    [RelayCommand]
    private void DismissElevationWarning()
    {
        ShowElevationWarning = false;
    }

    [ObservableProperty]
    private bool _isSynchronizing;

    partial void OnIsSynchronizingChanged(bool value) => OnPropertyChanged(nameof(CurrentTrayIconState));

    [ObservableProperty]
    private bool _isPaused;

    public string PauseResumeButtonText => IsPaused ? "▶ Resume" : "⏸ Pause";

    partial void OnIsPausedChanged(bool value) => OnPropertyChanged(nameof(PauseResumeButtonText));

    [ObservableProperty]
    private double _progressPercentage;

    [ObservableProperty]
    private string _progressItem = string.Empty;

    [ObservableProperty]
    private string _lastSyncText = "Never";

    public bool IsReadyForSync => Connection.IsConnected && Login.IsLoggedIn && Folder.IsFolderSelected;

    public TrayIconState CurrentTrayIconState
    {
        get
        {
            if (!Connection.IsConnected)
                return TrayIconState.Disconnected;
            if (Login.IsLoggedIn && Folder.IsFolderSelected && !IsSynchronizing && FileList.AreAllFilesInSync)
                return TrayIconState.Synced;
            return TrayIconState.Active;
        }
    }

    public MainWindowViewModel(
        ISecretStore secretStore,
        IFileSyncApi api,
        ConnectionCheckViewModel connection,
        LoginViewModel login,
        FolderSelectionViewModel folder,
        ClientSettingsManager? settingsManager = null,
        ILoggerFactory? loggerFactory = null)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _settingsManager = settingsManager ?? new ClientSettingsManager();
        _loggerFactory = loggerFactory;
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Login = login ?? throw new ArgumentNullException(nameof(login));
        Folder = folder ?? throw new ArgumentNullException(nameof(folder));

        ConflictPrompt = new ConflictPromptViewModel();

        FileList = new FileListViewModel(_api, () => Folder.SelectedFolderPath);

        DeletedFiles = new DeletedFilesViewModel(
            _api,
            () => Folder.SelectedFolderPath,
            () => SyncNowAsync());

        Logs = new LogsViewModel(_api, () => Connection.IsConnected && Login.IsLoggedIn && SelectedTabIndex == LogsTabIndex);

        Settings = new SettingsViewModel(
            _api,
            _secretStore,
            _settingsManager,
            () => Folder.SelectedFolderPath,
            async newFolder =>
            {
                await Folder.SaveAndSetFolderPathAsync(newFolder);
            },
            newPolicy =>
            {
                if (Coordinator != null)
                {
                    Coordinator.Policy = newPolicy;
                }
            });

        _secretStoreDescription = secretStore.Description;
        _isSecretStoreInsecure = secretStore.IsInsecureFallback;

        // Check for elevated / sudo privileges
        if (PrivilegeDetector.IsRunningAsRoot() || PrivilegeDetector.IsInvokedViaSudo())
        {
            ShowElevationWarning = true;
            ElevationWarningText = PrivilegeDetector.IsInvokedViaSudo()
                ? $"⚠️ Running via sudo (invoked by '{PrivilegeDetector.GetInvokingUsername()}'). Regular user mode is recommended so synced files maintain standard user permissions."
                : "⚠️ Running as root. Regular user mode is recommended so synced files maintain standard user permissions.";
        }

        // Auto-switch tabs or update sync readiness
        FileList.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileList.AreAllFilesInSync))
            {
                OnPropertyChanged(nameof(CurrentTrayIconState));
            }
        };
        Connection.PropertyChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(IsReadyForSync));
            OnPropertyChanged(nameof(CurrentTrayIconState));
            if (!_isInitializing && e.PropertyName == nameof(Connection.IsConnected) && Connection.IsConnected && Coordinator == null)
            {
                RebuildCoordinator();
            }
            if (e.PropertyName == nameof(Connection.ServerUrl))
            {
                if (!string.IsNullOrWhiteSpace(Connection.ServerUrl))
                {
                    _api.ServerUrl = Connection.ServerUrl;
                }
                if (Settings != null && Settings.ServerUrl != Connection.ServerUrl)
                {
                    Settings.ServerUrl = Connection.ServerUrl;
                }
            }
        };
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Settings.ServerUrl))
            {
                if (!string.IsNullOrWhiteSpace(Settings.ServerUrl))
                {
                    _api.ServerUrl = Settings.ServerUrl;
                }
                if (Connection != null && Connection.ServerUrl != Settings.ServerUrl)
                {
                    Connection.ServerUrl = Settings.ServerUrl;
                }
            }
        };
        Login.PropertyChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(IsReadyForSync));
            OnPropertyChanged(nameof(CurrentTrayIconState));
            if (e.PropertyName == nameof(Login.IsLoggedIn))
            {
                if (Login.IsLoggedIn)
                {
                    if (!_isInitializing)
                    {
                        if (!Connection.IsConnected)
                        {
                            _ = Connection.CheckConnectionCommand.ExecuteAsync(null);
                        }
                        if (Folder.IsFolderSelected)
                        {
                            RebuildCoordinator();
                        }
                    }
                }
                else
                {
                    Coordinator?.Dispose();
                    Coordinator = null;
                    IsSynchronizing = false;
                    SyncStatusSummary = "Sign in to sync.";
                    SelectedTabIndex = AccountTabIndex;
                }
            }
        };
        Folder.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Folder.SelectedFolderPath))
            {
                if (Settings != null)
                {
                    Settings.CurrentSyncFolder = Folder.SelectedFolderPath ?? string.Empty;
                }
                if (!_isInitializing && Folder.IsFolderSelected && Login.IsLoggedIn)
                {
                    RebuildCoordinator();
                }
            }
            OnPropertyChanged(nameof(IsReadyForSync));
            OnPropertyChanged(nameof(CurrentTrayIconState));
        };
    }

    public bool IsStartupInitialized { get; private set; }

    public async Task InitializeAsync()
    {
        IsStartupInitialized = true;
        _isInitializing = true;
        Settings.IsInitialized = false;

        try
        {
            var appSettings = await _settingsManager.LoadSettingsAsync();
            if (appSettings != null)
            {
                Settings.SyncIntervalSeconds = appSettings.SyncIntervalSeconds;
                Settings.CloseToTray = appSettings.CloseToTray;
                Settings.StartMinimized = appSettings.StartMinimized;
                if (Enum.TryParse<ConflictPolicy>(appSettings.ConflictPolicy, out var pol))
                {
                    Settings.SelectedConflictPolicy = pol;
                }
                if (!string.IsNullOrWhiteSpace(appSettings.ServerUrl))
                {
                    _api.ServerUrl = appSettings.ServerUrl;
                    Connection.ServerUrl = appSettings.ServerUrl;
                    Settings.ServerUrl = appSettings.ServerUrl;
                }
            }

            await Connection.InitializeAsync();
            await Connection.CheckConnectionCommand.ExecuteAsync(null);
            await Login.InitializeSessionAsync(fetchDevicesFromServer: Connection.IsConnected);
            if (!string.IsNullOrWhiteSpace(_api.ServerUrl))
            {
                Connection.ServerUrl = _api.ServerUrl;
                Settings.ServerUrl = _api.ServerUrl;
            }
            await Folder.InitializeFolderAsync();
            if (string.IsNullOrWhiteSpace(Folder.SelectedFolderPath) && !string.IsNullOrWhiteSpace(appSettings?.SyncFolderPath))
            {
                await Folder.SaveAndSetFolderPathAsync(appSettings.SyncFolderPath);
            }

            Settings.CurrentSyncFolder = Folder.SelectedFolderPath ?? string.Empty;
            Settings.IsInitialized = true;
        }
        finally
        {
            _isInitializing = false;
        }

        if (Login.IsLoggedIn && Folder.IsFolderSelected)
        {
            RebuildCoordinator();
            await FileList.RefreshCommand.ExecuteAsync(null);
            SelectedTabIndex = 0; // Synced Files
        }
        else
        {
            SelectedTabIndex = AccountTabIndex; // Account & Connection tab
        }
    }

    public void RebuildCoordinator()
    {
        if (!Login.IsLoggedIn || !Folder.IsFolderSelected || string.IsNullOrWhiteSpace(Folder.SelectedFolderPath))
            return;

        try
        {
            Coordinator?.Dispose();

            var deviceId = Login.DeviceId ?? Environment.MachineName;
            Coordinator = new SyncCoordinator(
                api: _api,
                localFolderPath: Folder.SelectedFolderPath,
                deviceId: deviceId,
                policy: Settings.SelectedConflictPolicy,
                promptHandler: ConflictPrompt,
                loggerFactory: _loggerFactory);

            Coordinator.ProgressChanged += (s, e) =>
            {
                void UpdateProgress()
                {
                    ProgressPercentage = e.Percentage;
                    ProgressItem = e.CurrentItem;
                    SyncStatusSummary = $"Syncing: {e.ProcessedItems}/{e.TotalItems} items ({e.Percentage:0}%)";
                }

                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                {
                    UpdateProgress();
                }
                else
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(UpdateProgress);
                }
            };

            Coordinator.SyncCompleted += (s, e) =>
            {
                void HandleSyncCompleted()
                {
                    IsSynchronizing = false;
                    LastSyncText = DateTime.UtcNow.ToString("HH:mm:ss 'UTC'");
                    if (e.Result.Success)
                    {
                        Connection.IsConnected = true;
                        SyncStatusSummary = $"In Sync ({e.Result.ItemsProcessed} items, {e.Result.Duration.TotalSeconds:0.#}s)";
                    }
                    else
                    {
                        if (e.Result.ErrorMessage != null &&
                            e.Result.ErrorMessage.StartsWith("Server unreachable", StringComparison.OrdinalIgnoreCase))
                        {
                            Connection.IsConnected = false;
                        }
                        SyncStatusSummary = $"Sync error: {e.Result.ErrorMessage}";
                    }

                    OnPropertyChanged(nameof(CurrentTrayIconState));

                    // Refresh file list safely on the UI thread
                    _ = FileList.RefreshCommand.ExecuteAsync(null);

                    // Local deletions that were just pushed should show up in the Deleted Files tab
                    if (SelectedTabIndex == DeletedFilesTabIndex)
                    {
                        _ = DeletedFiles.RefreshCommand.ExecuteAsync(null);
                    }
                }

                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                {
                    HandleSyncCompleted();
                }
                else
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(HandleSyncCompleted);
                }
            };

            Coordinator.StartBackgroundSync(TimeSpan.FromSeconds(Settings.SyncIntervalSeconds));
            SyncStatusSummary = "Background sync active.";
            _ = Coordinator.RunSyncAsync();
        }
        catch (Exception ex)
        {
            SyncStatusSummary = $"Coordinator init error: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task SyncNowAsync()
    {
        if (Coordinator == null)
        {
            RebuildCoordinator();
        }

        if (Coordinator == null)
        {
            SyncStatusSummary = "Complete setup before syncing.";
            return;
        }

        IsSynchronizing = true;
        SyncStatusSummary = "Starting sync run...";
        ProgressPercentage = 0;
        ProgressItem = string.Empty;

        try
        {
            var result = await Coordinator.RunSyncAsync();
            if (result.Success)
            {
                SyncStatusSummary = $"Sync completed ({result.ItemsProcessed} items).";
            }
            else
            {
                SyncStatusSummary = $"Sync ended: {result.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            SyncStatusSummary = $"Sync error: {ex.Message}";
        }
        finally
        {
            IsSynchronizing = false;
            await FileList.RefreshCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    public async Task TogglePauseResumeAsync()
    {
        if (Coordinator == null) return;

        if (IsPaused)
        {
            await Coordinator.ResumeAsync();
            IsPaused = false;
            SyncStatusSummary = "Sync resumed.";
        }
        else
        {
            await Coordinator.PauseAsync();
            IsPaused = true;
            SyncStatusSummary = "Sync paused.";
        }
    }

    public void Dispose()
    {
        Coordinator?.Dispose();
        Logs.Dispose();
    }
}
