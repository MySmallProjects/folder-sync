using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderSyncEr.Client.Api;
using FolderSyncEr.Client.Sync.Common;
using FolderSyncEr.Client.Sync.Storage;
using FolderSyncEr.Contracts;

namespace FolderSyncEr.Client.App.ViewModels;

public partial class FileListViewModel : ViewModelBase
{
    private readonly IFileSyncApi _api;
    private readonly Func<string?> _getSyncFolder;
    private readonly List<FileItemViewModel> _allFiles = new();

    [ObservableProperty]
    private ObservableCollection<FileItemViewModel> _files = new();

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private string _selectedStatusFilter = "All";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _summaryText = "0 files";

    [ObservableProperty]
    private bool _areAllFilesInSync;

    public IReadOnlyList<string> StatusFilters { get; } = new[]
    {
        "All",
        "In sync",
        "Local newer",
        "Server newer",
        "Conflict",
        "Errors"
    };

    public FileListViewModel(IFileSyncApi api, Func<string?> getSyncFolder)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _getSyncFolder = getSyncFolder ?? throw new ArgumentNullException(nameof(getSyncFolder));
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilter();
    partial void OnSelectedStatusFilterChanged(string value) => ApplyFilter();

    public void Clear()
    {
        _allFiles.Clear();
        Files = new ObservableCollection<FileItemViewModel>();
        AreAllFilesInSync = false;
        SummaryText = "0 files";
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var syncFolder = _getSyncFolder();
        if (string.IsNullOrWhiteSpace(syncFolder) || !Directory.Exists(syncFolder))
        {
            SummaryText = "Select a sync folder first";
            return;
        }

        IsLoading = true;
        var items = new List<FileItemViewModel>();

        try
        {
            // 1. Scan local files
            var localEntries = new Dictionary<string, (long size, DateTime mtimeUtc)>(StringComparer.OrdinalIgnoreCase);
            var di = new DirectoryInfo(syncFolder);
            foreach (var fi in di.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(syncFolder, fi.FullName).Replace('\\', '/');
                if (rel.StartsWith(".foldersyncer", StringComparison.OrdinalIgnoreCase))
                    continue;

                localEntries[rel] = (fi.Length, fi.LastWriteTimeUtc);
            }

            // 2. Fetch remote files
            IReadOnlyList<FileDto> remoteFiles = Array.Empty<FileDto>();
            var remoteReachable = false;
            try
            {
                var resp = await _api.ListFilesAsync(includeDeleted: false, limit: 5000);
                remoteFiles = resp.Items;
                remoteReachable = true;
            }
            catch
            {
                // API offline or unreachable, local only
            }

            var remoteDict = remoteFiles
                .Where(r => !r.IsDeleted && !r.IsDirectory)
                .ToDictionary(r => RemoteSnapshot.NormalizePath(r.Path), StringComparer.OrdinalIgnoreCase);

            // 3. Merge paths
            var allPaths = new SortedSet<string>(localEntries.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var k in remoteDict.Keys)
                allPaths.Add(k);

            foreach (var path in allPaths)
            {
                var hasLocal = localEntries.TryGetValue(path, out var local);
                var hasRemote = remoteDict.TryGetValue(path, out var remote);

                var vm = new FileItemViewModel
                {
                    Path = path,
                    FileName = Path.GetFileName(path)
                };

                if (hasLocal)
                {
                    vm.LocalSize = local.size;
                    vm.LocalModifiedUtc = local.mtimeUtc;
                }

                if (hasRemote && remote != null)
                {
                    vm.ServerSize = remote.Size;
                    vm.ServerModifiedUtc = remote.LastModifiedUtc;
                    var hashPrefix = !string.IsNullOrEmpty(remote.Sha256)
                        ? (remote.Sha256.Length > 8 ? remote.Sha256[..8] : remote.Sha256)
                        : "-";
                    vm.ServerVersion = $"v{remote.Version} [{hashPrefix}]";
                }

                // Determine sync status
                if (hasLocal && hasRemote && remote != null)
                {
                    if (Math.Abs((local.mtimeUtc - remote.LastModifiedUtc).TotalSeconds) < 2 && local.size == remote.Size)
                    {
                        vm.Status = "In sync";
                        vm.StatusColor = "#10B981"; // Green
                    }
                    else if (local.mtimeUtc > remote.LastModifiedUtc)
                    {
                        vm.Status = "Local newer";
                        vm.StatusColor = "#3B82F6"; // Blue
                    }
                    else
                    {
                        vm.Status = "Server newer";
                        vm.StatusColor = "#8B5CF6"; // Purple
                    }
                }
                else if (hasLocal && !hasRemote)
                {
                    vm.Status = "Local newer";
                    vm.StatusColor = "#3B82F6";
                }
                else if (!hasLocal && hasRemote)
                {
                    vm.Status = "Server newer";
                    vm.StatusColor = "#8B5CF6";
                }

                items.Add(vm);
            }

            void UpdateUI()
            {
                _allFiles.Clear();
                foreach (var it in items)
                {
                    _allFiles.Add(it);
                }
                AreAllFilesInSync = remoteReachable &&
                                    _allFiles.All(f => string.Equals(f.Status, "In sync", StringComparison.OrdinalIgnoreCase));
                ApplyFilter();
            }

            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                UpdateUI();
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(UpdateUI);
            }
        }
        finally
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                IsLoading = false;
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => IsLoading = false);
            }
        }
    }

    private void ApplyFilter()
    {
        var filtered = _allFiles.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var q = SearchQuery.Trim();
            filtered = filtered.Where(f => f.Path.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                           f.FileName.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.Equals(SelectedStatusFilter, "All", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(SelectedStatusFilter, "Errors", StringComparison.OrdinalIgnoreCase))
            {
                filtered = filtered.Where(f => f.Status.Equals("Error", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                filtered = filtered.Where(f => f.Status.Equals(SelectedStatusFilter, StringComparison.OrdinalIgnoreCase));
            }
        }

        Files = new ObservableCollection<FileItemViewModel>(filtered);
        SummaryText = $"{Files.Count} of {_allFiles.Count} files shown";
    }
}

