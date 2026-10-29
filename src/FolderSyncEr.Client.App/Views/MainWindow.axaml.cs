using Avalonia.Controls;
using Avalonia.Interactivity;
using FolderSyncEr.Client.App.ViewModels;

namespace FolderSyncEr.Client.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        var pickFolderBtn = this.FindControl<Button>("PickFolderButton");
        if (pickFolderBtn != null)
        {
            pickFolderBtn.Click += OnPickFolderClicked;
        }
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is MainWindowViewModel vm && !vm.IsStartupInitialized)
            {
                await vm.InitializeAsync();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[INIT ERROR] Failed to initialize MainWindowViewModel: {ex}");
        }
    }

    private async void OnPickFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && StorageProvider.CanPickFolder)
        {
            await vm.Folder.SelectFolderFromProviderAsync(StorageProvider);
        }
    }
}

