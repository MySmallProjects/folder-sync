using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using FolderSyncEr.Client.App.ViewModels;
using FolderSyncEr.Client.App.Views;
using FolderSyncEr.Client.Platform;
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

            var settingsManager = Services.GetService<ClientSettingsManager>();
            var initialSettings = settingsManager?.LoadSettings();
            var startMinimized = initialSettings?.StartMinimized ?? false;

            var mainVm = Services.GetRequiredService<MainWindowViewModel>();
            var mainWindow = new MainWindow
            {
                DataContext = mainVm
            };

            if (startMinimized)
            {
                // Do NOT assign desktop.MainWindow here, because Avalonia's
                // ClassicDesktopStyleApplicationLifetime.StartCore automatically calls
                // MainWindow?.Show() right after OnFrameworkInitializationCompleted returns.
                _ = mainVm.InitializeAsync();
            }
            else
            {
                desktop.MainWindow = mainWindow;
                mainWindow.Show();
            }

            // Load application icons
            var defaultIcon = LoadIcon("icon.png");
            var offlineIcon = LoadIcon("icon-offline.png");
            var syncedIcon = LoadIcon("icon-synced.png");
            if (defaultIcon != null)
            {
                mainWindow.Icon = defaultIcon;
            }

            // Track explicit shutdown to prevent recursive Closing -> Shutdown -> Close -> Closing loop
            var isShuttingDown = false;

            void PerformCleanShutdown()
            {
                if (isShuttingDown) return;
                isShuttingDown = true;

                try
                {
                    if (TrayIcon.GetIcons(this) is { } icons)
                    {
                        foreach (var icon in icons)
                        {
                            icon.IsVisible = false;
                        }
                    }
                }
                catch { }

                try
                {
                    mainWindow.Hide();
                }
                catch { }

                // Run disposal and exit in a background task off the UI thread
                Task.Run(() =>
                {
                    try
                    {
                        mainVm.Dispose();
                    }
                    catch { }

                    try
                    {
                        desktop.Shutdown(0);
                    }
                    catch { }

                    Environment.Exit(0);
                });

                // Guarantee process termination within 1 second under any circumstance
                new System.Threading.Thread(() =>
                {
                    System.Threading.Thread.Sleep(1000);
                    Environment.Exit(0);
                })
                {
                    IsBackground = true
                }.Start();
            }

            desktop.ShutdownRequested += (_, _) =>
            {
                PerformCleanShutdown();
            };

            // Setup System Tray icon
            SetupSystemTray(desktop, mainWindow, mainVm, defaultIcon, offlineIcon, syncedIcon, PerformCleanShutdown);

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
                    PerformCleanShutdown();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static WindowIcon? LoadIcon(string assetName)
    {
        try
        {
            var uri = new Uri($"avares://FolderSyncEr.Client.App/Assets/{assetName}");
            if (AssetLoader.Exists(uri))
            {
                using var stream = AssetLoader.Open(uri);
                return new WindowIcon(stream);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ICON WARNING] Could not load app icon '{assetName}': {ex.Message}");
        }
        return null;
    }

    private void SetupSystemTray(
        IClassicDesktopStyleApplicationLifetime desktop,
        Window mainWindow,
        MainWindowViewModel mainVm,
        WindowIcon? defaultIcon,
        WindowIcon? offlineIcon,
        WindowIcon? syncedIcon,
        Action onQuit)
    {
        try
        {
            var trayIcon = new TrayIcon
            {
                ToolTipText = "FolderSyncEr — Cross-Device File Sync",
                IsVisible = true,
                Icon = defaultIcon
            };

            void UpdateTrayAppearance()
            {
                switch (mainVm.CurrentTrayIconState)
                {
                    case TrayIconState.Disconnected:
                        trayIcon.Icon = offlineIcon ?? defaultIcon;
                        trayIcon.ToolTipText = "FolderSyncEr — Disconnected from server";
                        break;
                    case TrayIconState.Synced:
                        trayIcon.Icon = syncedIcon ?? defaultIcon;
                        trayIcon.ToolTipText = "FolderSyncEr — All files in sync";
                        break;
                    default:
                        trayIcon.Icon = defaultIcon;
                        trayIcon.ToolTipText = "FolderSyncEr — Cross-Device File Sync";
                        break;
                }
            }

            UpdateTrayAppearance();

            mainVm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.CurrentTrayIconState))
                {
                    if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                    {
                        UpdateTrayAppearance();
                    }
                    else
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateTrayAppearance);
                    }
                }
            };

            var menu = new NativeMenu();

            var openItem = new NativeMenuItem("Open FolderSyncEr");
            openItem.Click += (_, _) =>
            {
                desktop.MainWindow ??= mainWindow;
                BringWindowToForeground(mainWindow);
            };

            var syncNowItem = new NativeMenuItem("Sync Now");
            syncNowItem.Click += async (_, _) =>
            {
                await mainVm.SyncNowAsync();
            };

            var pauseResumeItem = new NativeMenuItem("Pause / Resume");
            pauseResumeItem.Click += async (_, _) =>
            {
                await mainVm.TogglePauseResumeAsync();
            };

            var quitItem = new NativeMenuItem("Quit");
            quitItem.Click += (_, _) =>
            {
                try
                {
                    trayIcon.IsVisible = false;
                }
                catch { }
                onQuit();
            };

            menu.Items.Add(openItem);
            menu.Items.Add(syncNowItem);
            menu.Items.Add(pauseResumeItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quitItem);

            trayIcon.Menu = menu;
            trayIcon.Clicked += (_, _) =>
            {
                desktop.MainWindow ??= mainWindow;
                BringWindowToForeground(mainWindow);
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
