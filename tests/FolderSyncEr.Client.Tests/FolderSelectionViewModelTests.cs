using FolderSyncEr.Client.App.ViewModels;
using FolderSyncEr.Client.Platform;

namespace FolderSyncEr.Client.Tests;

public class FolderSelectionViewModelTests : IDisposable
{
    private readonly string _tempSecretFile;
    private readonly string _tempSyncFolder;
    private readonly ISecretStore _store;

    public FolderSelectionViewModelTests()
    {
        _tempSecretFile = Path.Combine(Path.GetTempPath(), $"folder_vm_sec_{Guid.NewGuid():N}.dat");
        _tempSyncFolder = Path.Combine(Path.GetTempPath(), $"sync_folder_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempSyncFolder);

        _store = new EncryptedFileFallbackSecretStore(_tempSecretFile);
    }

    [Fact]
    public async Task SaveAndSetFolderPath_WithValidFolder_UpdatesStateAndPersists()
    {
        var vm = new FolderSelectionViewModel(_store);

        await vm.SaveAndSetFolderPathAsync(_tempSyncFolder);

        Assert.True(vm.IsFolderSelected);
        Assert.Equal(_tempSyncFolder, vm.SelectedFolderPath);
        Assert.Null(vm.ErrorMessage);

        // Verify stored in secret store
        var stored = await _store.GetSecretAsync("SyncFolderPath");
        Assert.Equal(_tempSyncFolder, stored);

        // Initialize new VM should pick it up
        var vm2 = new FolderSelectionViewModel(_store);
        await vm2.InitializeFolderAsync();
        Assert.True(vm2.IsFolderSelected);
        Assert.Equal(_tempSyncFolder, vm2.SelectedFolderPath);
    }

    [Fact]
    public async Task SaveAndSetFolderPath_WithNonExistentFolder_SetsError()
    {
        var vm = new FolderSelectionViewModel(_store);
        var nonExistent = Path.Combine(_tempSyncFolder, "does_not_exist_subfolder");

        await vm.SaveAndSetFolderPathAsync(nonExistent);

        Assert.False(vm.IsFolderSelected);
        Assert.NotNull(vm.ErrorMessage);
    }

    public void Dispose()
    {
        if (File.Exists(_tempSecretFile))
        {
            try { File.Delete(_tempSecretFile); } catch { }
        }
        if (Directory.Exists(_tempSyncFolder))
        {
            try { Directory.Delete(_tempSyncFolder, recursive: true); } catch { }
        }
    }
}

