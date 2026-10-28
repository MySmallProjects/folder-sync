using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderSyncEr.Client.Api;
using FolderSyncEr.Contracts;

namespace FolderSyncEr.Client.App.ViewModels;

/// <summary>
/// Backs the "Deleted Files" tab: lists files/folders deleted on the server and lets the user
/// restore them (as a copy when the original path is taken locally) or delete them permanently.
/// </summary>
public partial class DeletedFilesViewModel : ViewModelBase
{
    private const int MaxCopyNameAttempts = 20;

    private readonly IFileSyncApi _api;
    private readonly Func<string?> _getSyncFolder;
    private readonly Func<Task> _requestSync;
    private readonly List<DeletedItemViewModel> _allItems = new();
    private Func<Task>? _pendingConfirmAction;

    [ObservableProperty]
    private ObservableCollection<DeletedItemViewModel> _items = new();

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _summaryText = "No deleted items";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusError;

    [ObservableProperty]
    private bool _isConfirmVisible;

    [ObservableProperty]
    private string _confirmTitle = string.Empty;

    [ObservableProperty]
    private string _confirmMessage = string.Empty;

    public bool HasItems => _allItems.Count > 0;
    public bool IsEmpty => !IsLoading && _allItems.Count == 0;
    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);
    public string StatusColor => IsStatusError ? "#F87171" : "#34D399";
    public Avalonia.Media.IBrush StatusBrush => Avalonia.Media.Brush.Parse(StatusColor);

    public DeletedFilesViewModel(IFileSyncApi api, Func<string?> getSyncFolder, Func<Task> requestSync)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _getSyncFolder = getSyncFolder ?? throw new ArgumentNullException(nameof(getSyncFolder));
        _requestSync = requestSync ?? throw new ArgumentNullException(nameof(requestSync));
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilter();
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));
    partial void OnIsStatusErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(StatusBrush));
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        RunOnUI(() => IsLoading = true);
        try
        {
            var deleted = await _api.ListDeletedItemsAsync();
            var built = deleted.Select(dto => new DeletedItemViewModel(dto, RestoreAsync, RequestPurge)).ToList();
            RunOnUI(() =>
            {
                _allItems.Clear();
                _allItems.AddRange(built);
                ApplyFilter();
            });
        }
        catch (Exception ex)
        {
            RunOnUI(() => SetStatus($"Could not load deleted items: {ex.Message}", isError: true));
        }
        finally
        {
            RunOnUI(() =>
            {
                IsLoading = false;
                OnPropertyChanged(nameof(HasItems));
                OnPropertyChanged(nameof(IsEmpty));
            });
        }
    }

    private static void RunOnUI(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(action);
        }
    }

    /// <summary>
    /// Restores an item. If something already exists at the original path in the local sync folder
    /// (e.g. a new file with the same name that has not been uploaded yet), the item is restored
    /// next to it as "name (restored yyyy-MM-dd).ext" so nothing gets overwritten.
    /// </summary>
    public async Task RestoreAsync(DeletedItemViewModel item)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            string? targetPath = null;
            var dateTag = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            if (LocalPathExists(item.Path))
            {
                targetPath = BuildCopyPath(item.Path, item.IsDirectory, dateTag, 1);
            }

            var attempt = 1;
            TrashActionResult<RestoreResponse> result;
            while (true)
            {
                if (targetPath != null && LocalPathExists(targetPath) && attempt < MaxCopyNameAttempts)
                {
                    attempt++;
                    targetPath = BuildCopyPath(item.Path, item.IsDirectory, dateTag, attempt);
                    continue;
                }

                result = await _api.RestoreDeletedItemAsync(item.Id, targetPath);

                // The copy name was taken on the server: try the next numbered name
                if (result.IsConflict && targetPath != null && attempt < MaxCopyNameAttempts)
                {
                    attempt++;
                    targetPath = BuildCopyPath(item.Path, item.IsDirectory, dateTag, attempt);
                    continue;
                }
                break;
            }

            if (!result.IsSuccess || result.Value == null)
            {
                SetStatus($"Restore failed for '{item.Path}': {result.ErrorMessage}", isError: true);
                return;
            }

            var restoredPath = result.Value.Path;
            var countText = result.Value.RestoredCount > 1 ? $" ({result.Value.RestoredCount} items)" : string.Empty;
            SetStatus(targetPath == null
                ? $"Restored '{restoredPath}'{countText}. It will reappear in your sync folder shortly."
                : $"'{item.Path}' already exists locally, so it was restored as '{restoredPath}'{countText}.",
                isError: false);

            await RefreshAsync();

            // Pull the restored content down right away instead of waiting for the next interval
            try
            {
                await _requestSync();
            }
            catch
            {
                // Background sync will pick it up later
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Restore failed for '{item.Path}': {ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void RequestPurge(DeletedItemViewModel item)
    {
        var what = item.IsDirectory
            ? $"the folder '{item.Path}'" + (item.ContainedItemCount > 0 ? $" and the {item.ContainedItemCount} deleted item(s) inside it" : string.Empty)
            : $"'{item.Path}'";

        ShowConfirm(
            "Delete forever?",
            $"This permanently deletes {what} from the server, including all stored versions. This cannot be undone.",
            async () =>
            {
                var result = await _api.PurgeDeletedItemAsync(item.Id);
                if (result.IsSuccess && result.Value != null)
                {
                    SetStatus($"Permanently deleted '{item.Path}' ({FormatSize(result.Value.FreedBytes)} freed).", isError: false);
                }
                else
                {
                    SetStatus($"Could not delete '{item.Path}': {result.ErrorMessage}", isError: true);
                }
                await RefreshAsync();
            });
    }

    [RelayCommand]
    private void EmptyTrash()
    {
        if (_allItems.Count == 0) return;

        ShowConfirm(
            "Empty trash?",
            $"This permanently deletes all {_allItems.Count} deleted item(s) from the server, including all stored versions. This cannot be undone.",
            async () =>
            {
                var result = await _api.EmptyTrashAsync();
                SetStatus($"Trash emptied: {result.PurgedCount} item(s) permanently deleted ({FormatSize(result.FreedBytes)} freed).", isError: false);
                await RefreshAsync();
            });
    }

    [RelayCommand]
    private async Task ConfirmYesAsync()
    {
        var action = _pendingConfirmAction;
        IsConfirmVisible = false;
        _pendingConfirmAction = null;
        if (action == null) return;

        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            SetStatus($"Operation failed: {ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ConfirmNo()
    {
        IsConfirmVisible = false;
        _pendingConfirmAction = null;
    }

    private void ShowConfirm(string title, string message, Func<Task> onConfirm)
    {
        ConfirmTitle = title;
        ConfirmMessage = message;
        _pendingConfirmAction = onConfirm;
        IsConfirmVisible = true;
    }

    private void SetStatus(string message, bool isError)
    {
        IsStatusError = isError;
        StatusMessage = message;
    }

    private void ApplyFilter()
    {
        IEnumerable<DeletedItemViewModel> filtered = _allItems;
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var q = SearchQuery.Trim();
            filtered = filtered.Where(i => i.Path.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        Items = new ObservableCollection<DeletedItemViewModel>(filtered);
        SummaryText = _allItems.Count == 0
            ? "No deleted items"
            : $"{Items.Count} of {_allItems.Count} deleted items shown";
        OnPropertyChanged(nameof(HasItems));
    }

    private bool LocalPathExists(string relativePath)
    {
        var root = _getSyncFolder();
        if (string.IsNullOrWhiteSpace(root)) return false;

        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) || Directory.Exists(full);
    }

    /// <summary>"docs/report.txt" -> "docs/report (restored 2026-10-28).txt" (attempt 2 adds " 2", ...).</summary>
    public static string BuildCopyPath(string originalPath, bool isDirectory, string dateTag, int attempt)
    {
        var slash = originalPath.LastIndexOf('/');
        var parent = slash >= 0 ? originalPath[..(slash + 1)] : string.Empty;
        var name = slash >= 0 ? originalPath[(slash + 1)..] : originalPath;

        var ext = isDirectory ? string.Empty : Path.GetExtension(name);
        var stem = string.IsNullOrEmpty(ext) ? name : name[..^ext.Length];
        var tag = attempt <= 1 ? $"restored {dateTag}" : $"restored {dateTag} {attempt}";

        return $"{parent}{stem} ({tag}){ext}";
    }

    internal static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
    }
}

/// <summary>One row in the Deleted Files list.</summary>
public partial class DeletedItemViewModel : ViewModelBase
{
    public string Id { get; }
    public string Path { get; }
    public string Name { get; }
    public string Location { get; }
    public bool IsDirectory { get; }
    public int ContainedItemCount { get; }
    public DateTime DeletedAtUtc { get; }

    public string Icon => IsDirectory ? "📁" : "📄";
    public string DeletedAtText => DeletedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    public string DetailText { get; }

    public IAsyncRelayCommand RestoreCommand { get; }
    public IRelayCommand PurgeCommand { get; }

    public DeletedItemViewModel(DeletedItemDto dto, Func<DeletedItemViewModel, Task> restore, Action<DeletedItemViewModel> purge)
    {
        Id = dto.Id;
        Path = dto.Path;
        IsDirectory = dto.IsDirectory;
        ContainedItemCount = dto.ContainedItemCount;
        DeletedAtUtc = DateTime.SpecifyKind(dto.DeletedAtUtc, DateTimeKind.Utc);

        var slash = dto.Path.LastIndexOf('/');
        Name = slash >= 0 ? dto.Path[(slash + 1)..] : dto.Path;
        Location = slash >= 0 ? "/" + dto.Path[..slash] : "/";

        DetailText = dto.IsDirectory
            ? (dto.ContainedItemCount > 0
                ? $"Folder · {dto.ContainedItemCount} item(s) · {DeletedFilesViewModel.FormatSize(dto.ContainedSize)}"
                : "Folder")
            : DeletedFilesViewModel.FormatSize(dto.Size);

        RestoreCommand = new AsyncRelayCommand(() => restore(this));
        PurgeCommand = new RelayCommand(() => purge(this));
    }
}

