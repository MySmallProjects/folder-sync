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
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.InitializeAsync();
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

