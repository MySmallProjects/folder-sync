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

namespace FolderSyncEr.Client.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly ISecretStore _secretStore;
    private readonly IFileSyncApi _api;

    public ConnectionCheckViewModel Connection { get; }
    public LoginViewModel Login { get; }
    public FolderSelectionViewModel Folder { get; }
    public FileListViewModel FileList { get; }
    public LogsViewModel Logs { get; }
    public SettingsViewModel Settings { get; }
    public ConflictPromptViewModel ConflictPrompt { get; }

    public SyncCoordinator? Coordinator { get; private set; }

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _secretStoreDescription;

    [ObservableProperty]
    private bool _isSecretStoreInsecure;

    [ObservableProperty]
    private string _syncStatusSummary = "Ready";

    [ObservableProperty]
    private bool _isSynchronizing;

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

    public MainWindowViewModel(
        ISecretStore secretStore,
        IFileSyncApi api,
        ConnectionCheckViewModel connection,
        LoginViewModel login,
        FolderSelectionViewModel folder)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Login = login ?? throw new ArgumentNullException(nameof(login));
        Folder = folder ?? throw new ArgumentNullException(nameof(folder));

        ConflictPrompt = new ConflictPromptViewModel();

        FileList = new FileListViewModel(_api, () => Folder.SelectedFolderPath);

        Logs = new LogsViewModel(_api);

        Settings = new SettingsViewModel(
            _api,
            _secretStore,
            () => Folder.SelectedFolderPath,
            newFolder =>
            {
                Folder.SelectedFolderPath = newFolder;
                RebuildCoordinator();
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

        // Auto-switch tabs or update sync readiness
        Connection.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsReadyForSync));
        Login.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsReadyForSync));
            if (Login.IsLoggedIn && Folder.IsFolderSelected)
            {
                RebuildCoordinator();
            }
        };
        Folder.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsReadyForSync));
            if (Folder.IsFolderSelected && Login.IsLoggedIn)
            {
                RebuildCoordinator();
            }
        };
    }

    public async Task InitializeAsync()
    {
        await Login.InitializeSessionAsync();
        await Folder.InitializeFolderAsync();
        await Connection.CheckConnectionCommand.ExecuteAsync(null);

        if (IsReadyForSync)
        {
            RebuildCoordinator();
            await FileList.RefreshCommand.ExecuteAsync(null);
        }
    }

    public void RebuildCoordinator()
    {
        if (!IsReadyForSync || string.IsNullOrWhiteSpace(Folder.SelectedFolderPath))
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
                promptHandler: ConflictPrompt);

            Coordinator.ProgressChanged += (s, e) =>
            {
                ProgressPercentage = e.Percentage;
                ProgressItem = e.CurrentItem;
                SyncStatusSummary = $"Syncing: {e.ProcessedItems}/{e.TotalItems} items ({e.Percentage:0}%)";
            };

            Coordinator.SyncCompleted += (s, e) =>
            {
                IsSynchronizing = false;
                LastSyncText = DateTime.UtcNow.ToString("HH:mm:ss 'UTC'");
                SyncStatusSummary = e.Result.Success
                    ? $"In Sync ({e.Result.ItemsProcessed} items, {e.Result.Duration.TotalSeconds:0.#}s)"
                    : $"Sync error: {e.Result.ErrorMessage}";

                // Refresh file list after sync completes
                _ = FileList.RefreshCommand.ExecuteAsync(null);
            };

            Coordinator.StartBackgroundSync(TimeSpan.FromSeconds(Settings.SyncIntervalSeconds));
            SyncStatusSummary = "Background sync active.";
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
