using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderSyncEr.Client.Api;
using FolderSyncEr.Contracts;

namespace FolderSyncEr.Client.App.ViewModels;

public partial class LogsViewModel : ViewModelBase, IDisposable
{
    private readonly IFileSyncApi _api;
    private Timer? _autoRefreshTimer;

    [ObservableProperty]
    private ObservableCollection<ActivityLogDto> _logs = new();

    [ObservableProperty]
    private string _selectedActionFilter = "All";

    [ObservableProperty]
    private string _selectedResultFilter = "All";

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _autoRefreshEnabled = true;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public IReadOnlyList<string> ActionFilters { get; } = new[]
    {
        "All",
        "Upload",
        "Download",
        "Conflict",
        "Delete",
        "Move",
        "Login",
        "Error"
    };

    public IReadOnlyList<string> ResultFilters { get; } = new[]
    {
        "All",
        "Success",
        "Failure",
        "Conflict",
        "Rejected"
    };

    public LogsViewModel(IFileSyncApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));

        // Start auto-refresh timer (every 4 seconds)
        _autoRefreshTimer = new Timer(async _ =>
        {
            try
            {
                if (AutoRefreshEnabled && !IsLoading)
                {
                    await LoadLogsAsync(silent: true);
                }
            }
            catch
            {
                // Suppress background timer errors to prevent crashing
            }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
    }

    partial void OnSelectedActionFilterChanged(string value) => _ = LoadLogsAsync();
    partial void OnSelectedResultFilterChanged(string value) => _ = LoadLogsAsync();
    partial void OnSearchQueryChanged(string value) => _ = LoadLogsAsync();

    [RelayCommand]
    public async Task RefreshAsync() => await LoadLogsAsync();

    [RelayCommand]
    public void ClearDisplay()
    {
        Logs.Clear();
        StatusMessage = "Log display cleared.";
    }

    private async Task LoadLogsAsync(bool silent = false)
    {
        if (!silent) IsLoading = true;

        try
        {
            var action = SelectedActionFilter == "All" ? null : SelectedActionFilter;
            var result = SelectedResultFilter == "All" ? null : SelectedResultFilter;

            var paged = await _api.GetLogsAsync(
                page: 1,
                pageSize: 100,
                action: action,
                result: result,
                path: string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery.Trim());

            void UpdateLogs()
            {
                Logs = new ObservableCollection<ActivityLogDto>(paged.Items);
                StatusMessage = $"Loaded {Logs.Count} log entries (Total: {paged.TotalCount})";
            }

            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                UpdateLogs();
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(UpdateLogs);
            }
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                StatusMessage = $"Failed to fetch logs: {ex.Message}";
            }
        }
        finally
        {
            if (!silent) IsLoading = false;
        }
    }

    public void Dispose()
    {
        _autoRefreshTimer?.Dispose();
        _autoRefreshTimer = null;
    }
}

