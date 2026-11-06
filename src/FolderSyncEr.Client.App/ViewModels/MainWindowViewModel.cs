using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
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
    private Timer? _statusTickerTimer;
    private bool _isReconnecting;
    private bool _isDisposed;

    public ConnectionCheckViewModel Connection { get; }
    public LoginViewModel Login { get; }
    public FolderSelectionViewModel Folder { get; }
    public FileListViewModel FileList { get; }
    public DeletedFilesViewModel DeletedFiles { get; }
    public ActivitiesViewModel Activities { get; }
    public LogsViewModel Logs { get; }
    public SettingsViewModel Settings { get; }
    public ConflictPromptViewModel ConflictPrompt { get; }
    public INetworkConnectivityService NetworkConnectivity { get; }
    public BackoffRetryPolicy BackoffPolicy { get; }
    public IClientActivityService ActivityService { get; }

    public SyncCoordinator? Coordinator { get; private set; }

    private bool? _lastReportedConnected;

    [ObservableProperty]
    private int _selectedTabIndex;

    // Tab order in MainWindow.axaml: Files | Deleted | Activities | Server Logs | Settings | Accounts
    public const int FilesTabIndex = 0;
    public const int DeletedFilesTabIndex = 1;
    public const int ActivitiesTabIndex = 2;
    public const int LogsTabIndex = 3;
    public const int SettingsTabIndex = 4;
    public const int AccountTabIndex = 5;

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == DeletedFilesTabIndex && Login.IsLoggedIn)
        {
            _ = DeletedFiles.RefreshCommand.ExecuteAsync(null);
        }
        else if (value == ActivitiesTabIndex)
        {
            Activities.UpdateWhenTimes();
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

    public IBrush ConnectionStatusBrush
    {
        get
        {
            if (Connection == null || !Connection.IsConnected)
            {
                return ServerNotFoundBrush;
            }

            if (Login == null || !Login.IsLoggedIn)
            {
                return ServerFoundNotLoggedInBrush;
            }

            return SignedInBrush;
        }
    }

    public string ConnectionStatusToolTip
    {
        get
        {
            if (Connection == null || !Connection.IsConnected)
            {
                return "Server not found";
            }

            if (Login == null || !Login.IsLoggedIn)
            {
                return "Server online — Not signed in";
            }

            if (!string.IsNullOrWhiteSpace(Login.LoggedInUserText))
            {
                return $"Connected and signed in as {Login.LoggedInUserText}";
            }

            return "Connected and signed in";
        }
    }

    private static readonly IBrush ServerNotFoundBrush = Brush.Parse("#EF4444"); // Red: server not found
    private static readonly IBrush ServerFoundNotLoggedInBrush = Brush.Parse("#F59E0B"); // Amber: server found, not signed in
    private static readonly IBrush SignedInBrush = Brush.Parse("#10B981"); // Green: signed in

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
    private DateTime? _lastSuccessfulSyncUtc;

    partial void OnLastSuccessfulSyncUtcChanged(DateTime? value) => UpdateLastSyncDisplay();

    [ObservableProperty]
    private string _lastSyncText = "Never";

    [ObservableProperty]
    private string _lastSyncToolTip = "No successful sync yet";

    [ObservableProperty]
    private bool _showRetryIndicator;

    [ObservableProperty]
    private string _retryStatusText = string.Empty;

    [ObservableProperty]
    private string _retryToolTip = string.Empty;

    [ObservableProperty]
    private IBrush _retryStatusBrush = RetryAmberBrush;

    private static readonly IBrush RetryAmberBrush = Brush.Parse("#F59E0B"); // Amber
    private static readonly IBrush RetryRedBrush = Brush.Parse("#EF4444");   // Red

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
        ILoggerFactory? loggerFactory = null,
        INetworkConnectivityService? networkConnectivity = null,
        BackoffRetryPolicy? backoffPolicy = null,
        IClientActivityService? activityService = null)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _settingsManager = settingsManager ?? new ClientSettingsManager();
        _loggerFactory = loggerFactory;
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Login = login ?? throw new ArgumentNullException(nameof(login));
        Folder = folder ?? throw new ArgumentNullException(nameof(folder));
        NetworkConnectivity = networkConnectivity ?? new NetworkConnectivityService();
        BackoffPolicy = backoffPolicy ?? new BackoffRetryPolicy();
        ActivityService = activityService ?? new ClientActivityService();
        Activities = new ActivitiesViewModel(ActivityService);

        ConflictPrompt = new ConflictPromptViewModel();

        FileList = new FileListViewModel(_api, () => Folder.SelectedFolderPath);

        DeletedFiles = new DeletedFilesViewModel(
            _api,
            () => Folder.SelectedFolderPath,
            () => SyncNowAsync(),
            ActivityService);

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
            if (e.PropertyName == nameof(Connection.IsConnected))
            {
                OnPropertyChanged(nameof(ConnectionStatusBrush));
                OnPropertyChanged(nameof(ConnectionStatusToolTip));
                if (Connection.IsConnected)
                {
                    BackoffPolicy.Reset();
                    ShowRetryIndicator = false;
                    if (_lastReportedConnected == false)
                    {
                        ActivityService.LogActivity("Connection re-established", "Connection");
                    }
                }
                else
                {
                    if (!BackoffPolicy.IsInBackoff)
                    {
                        BackoffPolicy.RecordFailure();
                    }
                    if (_lastReportedConnected == true)
                    {
                        ActivityService.LogActivity("Connection lost", "Connection");
                    }
                }
                _lastReportedConnected = Connection.IsConnected;
                UpdateStatusAndCountdownDisplay();
            }
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
            if (e.PropertyName == nameof(Settings.UpdateCooldownSeconds) && Coordinator != null)
            {
                Coordinator.CooldownDuration = TimeSpan.FromSeconds(Settings.UpdateCooldownSeconds);
            }
            if (e.PropertyName == nameof(Settings.MaxWaitSeconds) && Coordinator != null)
            {
                Coordinator.MaxWaitDuration = TimeSpan.FromSeconds(Settings.MaxWaitSeconds);
            }
        };
        Login.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Login.IsLoggedIn) || e.PropertyName == nameof(Login.LoggedInUserText))
            {
                OnPropertyChanged(nameof(ConnectionStatusBrush));
                OnPropertyChanged(nameof(ConnectionStatusToolTip));
            }
            OnPropertyChanged(nameof(IsReadyForSync));
            OnPropertyChanged(nameof(CurrentTrayIconState));
            if (e.PropertyName == nameof(Login.IsLoggedIn))
            {
                if (Login.IsLoggedIn)
                {
                    var user = !string.IsNullOrWhiteSpace(Login.LoggedInUserText) ? Login.LoggedInUserText : "User";
                    ActivityService.LogActivity($"Logged in as {user}", "Auth");
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
                    ActivityService.LogActivity("Logged out", "Auth");
                    Coordinator?.Dispose();
                    Coordinator = null;
                    IsSynchronizing = false;
                    FileList.Clear();
                    DeletedFiles.Clear();
                    Logs.ClearDisplay();
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

        NetworkConnectivity.ConnectivityChanged += (_, isAvailable) =>
        {
            if (isAvailable)
            {
                ActivityService.LogActivity("Network restored", "Network");
                BackoffPolicy.Reset();
                _ = TriggerReconnectAsync();
            }
            else
            {
                ActivityService.LogActivity("Network offline", "Network");
            }
            UpdateStatusAndCountdownDisplay();
        };

        NetworkConnectivity.AddressOrRouteChanged += (_, _) =>
        {
            if (NetworkConnectivity.IsNetworkAvailable && !Connection.IsConnected)
            {
                _ = TriggerReconnectAsync();
            }
            UpdateStatusAndCountdownDisplay();
        };

        UpdateStatusAndCountdownDisplay();
        _statusTickerTimer = new Timer(_ => UpdateStatusAndCountdownDisplay(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
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
                Settings.UpdateCooldownSeconds = appSettings.UpdateCooldownSeconds;
                Settings.MaxWaitSeconds = appSettings.MaxWaitSeconds;
                Settings.CloseToTray = appSettings.CloseToTray;
                Settings.StartMinimized = appSettings.StartMinimized;
                Settings.AllowUntrustedCertificates = appSettings.AllowUntrustedCertificates;
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
                loggerFactory: _loggerFactory,
                backoffPolicy: BackoffPolicy,
                activityService: ActivityService);

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
                    if (!Login.IsLoggedIn)
                        return;

                    IsSynchronizing = false;
                    if (e.Result.Success)
                    {
                        LastSuccessfulSyncUtc = DateTime.UtcNow;
                        Connection.IsConnected = true;
                        BackoffPolicy.Reset();
                        ShowRetryIndicator = false;
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
                    UpdateStatusAndCountdownDisplay();

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

            Coordinator.StartBackgroundSync(
                periodicInterval: TimeSpan.FromSeconds(Settings.SyncIntervalSeconds),
                cooldown: TimeSpan.FromSeconds(Settings.UpdateCooldownSeconds),
                maxWait: TimeSpan.FromSeconds(Settings.MaxWaitSeconds));
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
        BackoffPolicy.Reset();

        if (Coordinator == null)
        {
            RebuildCoordinator();
        }

        if (Coordinator == null)
        {
            SyncStatusSummary = "Complete setup before syncing.";
            return;
        }

        if (!Connection.IsConnected)
        {
            await Connection.CheckConnectionCommand.ExecuteAsync(null);
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
                if (result.ItemsProcessed == 0)
                {
                    ActivityService.LogActivity("Sync successful", "Sync");
                }
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
            if (Login.IsLoggedIn)
            {
                await FileList.RefreshCommand.ExecuteAsync(null);
            }
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
            ActivityService.LogActivity("Sync resumed", "Sync");
        }
        else
        {
            await Coordinator.PauseAsync();
            IsPaused = true;
            SyncStatusSummary = "Sync paused.";
            ActivityService.LogActivity("Sync paused", "Sync");
        }
    }

    public void UpdateLastSyncDisplay() => UpdateStatusAndCountdownDisplay();

    public void UpdateStatusAndCountdownDisplay()
    {
        void DoUpdate()
        {
            if (LastSuccessfulSyncUtc == null)
            {
                LastSyncText = "Never";
                LastSyncToolTip = "No successful sync yet";
            }
            else
            {
                var dt = LastSuccessfulSyncUtc.Value;
                var fullDateTimeStr = dt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

                if (Connection?.IsConnected == true)
                {
                    LastSyncText = dt.ToString("HH:mm:ss 'UTC'");
                    LastSyncToolTip = $"Last successful sync: {fullDateTimeStr}";
                }
                else
                {
                    var timeAgo = FormatTimeAgo(DateTime.UtcNow - dt);
                    LastSyncText = timeAgo;
                    LastSyncToolTip = $"Last successful sync: {fullDateTimeStr} ({timeAgo})";
                }
            }

            var isNetworkAvailable = NetworkConnectivity.IsNetworkAvailable;
            if (!isNetworkAvailable)
            {
                ShowRetryIndicator = true;
                RetryStatusText = "Network Offline";
                RetryToolTip = "No active network adapters detected (Retries paused until network reconnects)";
                RetryStatusBrush = RetryRedBrush;
                return;
            }

            if (Connection == null || !Connection.IsConnected)
            {
                ShowRetryIndicator = true;
                var remaining = BackoffPolicy.GetRemainingTime();
                if (remaining != null && remaining.Value > TimeSpan.Zero)
                {
                    RetryStatusText = $"Next retry in {BackoffRetryPolicy.FormatCountdown(remaining.Value)}";
                    var approx = BackoffRetryPolicy.FormatInterval(BackoffPolicy.CurrentDelay);
                    RetryToolTip = $"Attempt #{BackoffPolicy.ConsecutiveFailures} • Backoff interval: ~{approx} (Server unreachable)";
                    RetryStatusBrush = RetryAmberBrush;
                }
                else
                {
                    if (!_isReconnecting)
                    {
                        _ = TriggerReconnectAsync();
                    }
                    RetryStatusText = "Connecting...";
                    RetryToolTip = "Attempting to reconnect to server...";
                    RetryStatusBrush = RetryAmberBrush;
                }
            }
            else
            {
                ShowRetryIndicator = false;
            }

            Activities.UpdateWhenTimes();
        }

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            DoUpdate();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(DoUpdate);
        }
    }

    public async Task TriggerReconnectAsync()
    {
        if (_isReconnecting || _isDisposed) return;
        _isReconnecting = true;
        try
        {
            if (!NetworkConnectivity.IsNetworkAvailable)
                return;

            await Connection.CheckConnectionCommand.ExecuteAsync(null);
            if (Connection.IsConnected)
            {
                BackoffPolicy.Reset();
                ShowRetryIndicator = false;
                if (Login.IsLoggedIn && Folder.IsFolderSelected)
                {
                    if (Coordinator == null)
                    {
                        RebuildCoordinator();
                    }
                    else
                    {
                        _ = Coordinator.RunSyncAsync();
                    }
                }
            }
            else
            {
                BackoffPolicy.RecordFailure();
            }
        }
        catch
        {
            BackoffPolicy.RecordFailure();
        }
        finally
        {
            _isReconnecting = false;
            UpdateStatusAndCountdownDisplay();
        }
    }

    public static string FormatTimeAgo(TimeSpan diff)
    {
        if (diff < TimeSpan.Zero)
            diff = TimeSpan.Zero;

        if (diff.TotalMinutes < 1)
        {
            var sec = (int)diff.TotalSeconds;
            return sec <= 1 ? "1s ago" : $"{sec}s ago";
        }

        if (diff.TotalHours < 1)
        {
            var mins = (int)diff.TotalMinutes;
            return $"{mins}min ago";
        }

        if (diff.TotalDays >= 1)
        {
            var days = (int)diff.TotalDays;
            var remHours = diff.Hours;
            return remHours > 0 ? $"{days}d {remHours}hr ago" : $"{days}d ago";
        }

        var hours = (int)diff.TotalHours;
        var remainingMins = diff.Minutes;
        return remainingMins > 0 ? $"{hours}hr {remainingMins}min ago" : $"{hours}hr ago";
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _statusTickerTimer?.Dispose();
        _statusTickerTimer = null;
        NetworkConnectivity.Dispose();
        Coordinator?.Dispose();
        Logs.Dispose();
        ActivityService.Dispose();
    }
}
