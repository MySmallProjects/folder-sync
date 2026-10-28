using CommunityToolkit.Mvvm.ComponentModel;
using FolderSyncEr.Client.Platform;

namespace FolderSyncEr.Client.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ISecretStore _secretStore;

    public ConnectionCheckViewModel Connection { get; }
    public LoginViewModel Login { get; }
    public FolderSelectionViewModel Folder { get; }

    [ObservableProperty]
    private string _secretStoreDescription;

    [ObservableProperty]
    private bool _isSecretStoreInsecure;

    public MainWindowViewModel(
        ISecretStore secretStore,
        ConnectionCheckViewModel connection,
        LoginViewModel login,
        FolderSelectionViewModel folder)
    {
        _secretStore = secretStore;
        Connection = connection;
        Login = login;
        Folder = folder;

        _secretStoreDescription = secretStore.Description;
        _isSecretStoreInsecure = secretStore.IsInsecureFallback;

        // Listen to changes to update ready state
        Connection.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsReadyForSync));
        Login.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsReadyForSync));
        Folder.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsReadyForSync));
    }

    public bool IsReadyForSync => Connection.IsConnected && Login.IsLoggedIn && Folder.IsFolderSelected;

    public async Task InitializeAsync()
    {
        await Login.InitializeSessionAsync();
        await Folder.InitializeFolderAsync();
        await Connection.CheckConnectionCommand.ExecuteAsync(null);
    }
}

