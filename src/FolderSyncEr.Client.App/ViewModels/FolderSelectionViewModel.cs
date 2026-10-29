using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderSyncEr.Client.Platform;

namespace FolderSyncEr.Client.App.ViewModels;

public partial class FolderSelectionViewModel : ViewModelBase
{
    private const string SyncFolderKey = "SyncFolderPath";
    private readonly ISecretStore _store;

    [ObservableProperty]
    private string? _selectedFolderPath;

    [ObservableProperty]
    private bool _isFolderSelected;

    [ObservableProperty]
    private string _folderStatusText = "No sync folder selected";

    [ObservableProperty]
    private string? _errorMessage;

    public FolderSelectionViewModel(ISecretStore store)
    {
        _store = store;
    }

    public async Task InitializeFolderAsync()
    {
        var stored = await _store.GetSecretAsync(SyncFolderKey);
        if (!string.IsNullOrWhiteSpace(stored) && Directory.Exists(stored))
        {
            SetFolderPath(stored);
        }
    }

    public async Task SelectFolderFromProviderAsync(IStorageProvider storageProvider)
    {
        ErrorMessage = null;
        try
        {
            var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Folder to Synchronize",
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                var localPath = folders[0].TryGetLocalPath();
                if (!string.IsNullOrEmpty(localPath))
                {
                    await SaveAndSetFolderPathAsync(localPath);
                }
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to select folder: {ex.Message}";
        }
    }

    public async Task SaveAndSetFolderPathAsync(string path)
    {
        if (!Directory.Exists(path))
        {
            ErrorMessage = "Selected directory does not exist.";
            return;
        }

        // Test write permission
        try
        {
            var testFile = Path.Combine(path, $".sync_write_test_{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(testFile, "test");
            File.Delete(testFile);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Selected directory is not writable: {ex.Message}";
            return;
        }

        SetFolderPath(path);
        await _store.SetSecretAsync(SyncFolderKey, path);
    }

    private void SetFolderPath(string path)
    {
        IsFolderSelected = true;
        SelectedFolderPath = path;
        try
        {
            var fileCount = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count();
            FolderStatusText = $"Selected: {fileCount} file(s) found";
        }
        catch
        {
            FolderStatusText = "Selected and accessible";
        }
    }
}

