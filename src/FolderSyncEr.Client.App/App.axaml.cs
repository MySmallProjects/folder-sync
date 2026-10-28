using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using FolderSyncEr.Client.App.ViewModels;
using FolderSyncEr.Client.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace FolderSyncEr.Client.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var mainVm = Services.GetRequiredService<MainWindowViewModel>();
            var mainWindow = new MainWindow
            {
                DataContext = mainVm
            };
            desktop.MainWindow = mainWindow;
            mainWindow.Show();

            // Load application icon
            WindowIcon? icon = null;
            try
            {
                var uri = new Uri("avares://FolderSyncEr.Client.App/Assets/icon.png");
                if (AssetLoader.Exists(uri))
                {
                    using var stream = AssetLoader.Open(uri);
                    icon = new WindowIcon(stream);
                    mainWindow.Icon = icon;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ICON WARNING] Could not load app icon: {ex.Message}");
            }

            // Track explicit shutdown to prevent recursive Closing -> Shutdown -> Close -> Closing loop
            var isShuttingDown = false;

            desktop.ShutdownRequested += (_, _) =>
            {
                isShuttingDown = true;
            };

            // Setup System Tray icon
            SetupSystemTray(desktop, mainVm, icon, () => isShuttingDown = true);

            // Handle close behavior (close-to-tray or quit)
            mainWindow.Closing += (s, e) =>
            {
                if (isShuttingDown)
                {
                    return;
                }

                if (mainVm.Settings.CloseToTray)
                {
                    e.Cancel = true;
                    mainWindow.Hide();
                }
                else
                {
                    isShuttingDown = true;
                    mainVm.Dispose();
                    desktop.Shutdown();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupSystemTray(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindowViewModel mainVm,
        WindowIcon? icon,
        Action onQuit)
    {
        try
        {
            var trayIcon = new TrayIcon
            {
                ToolTipText = "FolderSyncEr — Cross-Device File Sync",
                IsVisible = true,
                Icon = icon
            };

            var menu = new NativeMenu();

            var openItem = new NativeMenuItem("Open FolderSyncEr");
            openItem.Click += (_, _) =>
            {
                if (desktop.MainWindow != null)
                {
                    BringWindowToForeground(desktop.MainWindow);
                }
            };

            var syncNowItem = new NativeMenuItem("Sync Now");
            syncNowItem.Click += async (_, _) =>
            {
                await mainVm.SyncNowCommand.ExecuteAsync(null);
            };

            var pauseResumeItem = new NativeMenuItem("Pause / Resume");
            pauseResumeItem.Click += async (_, _) =>
            {
                await mainVm.TogglePauseResumeCommand.ExecuteAsync(null);
            };

            var quitItem = new NativeMenuItem("Quit");
            quitItem.Click += (_, _) =>
            {
                onQuit();
                mainVm.Dispose();
                desktop.Shutdown();
            };

            menu.Items.Add(openItem);
            menu.Items.Add(syncNowItem);
            menu.Items.Add(pauseResumeItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quitItem);

            trayIcon.Menu = menu;
            trayIcon.Clicked += (_, _) =>
            {
                if (desktop.MainWindow != null)
                {
                    BringWindowToForeground(desktop.MainWindow);
                }
            };

            var trayIcons = new TrayIcons { trayIcon };
            TrayIcon.SetIcons(this, trayIcons);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TRAY WARNING] System tray could not be initialized: {ex.Message}");
        }
    }

    private static void BringWindowToForeground(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        // Toggling Topmost forces native window managers (X11/Wayland Mutter/KWin, Windows DWM)
        // to bypass focus-stealing prevention and raise the window to the foreground
        window.Topmost = true;
        window.Activate();
        window.Topmost = false;
        window.Focus();
    }
}
