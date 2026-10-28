using System;
using System.IO;
using Avalonia;
using FolderSyncEr.Client.Api;
using FolderSyncEr.Client.App.ViewModels;
using FolderSyncEr.Client.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FolderSyncEr.Client.App;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            var msg = $"[FATAL UNHANDLED] {DateTime.UtcNow:O} {ex?.ToString() ?? e.ExceptionObject?.ToString()}";
            Console.Error.WriteLine(msg);
            try { File.WriteAllText("crash.log", msg); } catch { }
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            var msg = $"[UNOBSERVED TASK EXCEPTION] {DateTime.UtcNow:O} {e.Exception}";
            Console.Error.WriteLine(msg);
            try { File.WriteAllText("crash.log", msg); } catch { }
        };

        try
        {
            var services = new ServiceCollection();

            // Logging
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });

            // Platform & Secrets
            services.AddSingleton<ISecretStore>(sp =>
            {
                var logger = sp.GetService<ILogger<ISecretStore>>();
                return SecretStoreFactory.Create(logger);
            });

            // API & Auth
            services.AddSingleton<AuthTokenManager>();
            services.AddTransient<AuthHeaderHandler>();

            services.AddHttpClient<IFileSyncApi, FileSyncApiClient>()
                .AddHttpMessageHandler<AuthHeaderHandler>();

            // ViewModels
            services.AddTransient<ConnectionCheckViewModel>();
            services.AddTransient<LoginViewModel>();
            services.AddTransient<FolderSelectionViewModel>();
            services.AddTransient<MainWindowViewModel>();

            App.Services = services.BuildServiceProvider();

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            var msg = $"[STARTUP FATAL ERROR] {DateTime.UtcNow:O} {ex}";
            Console.Error.WriteLine(msg);
            try { File.WriteAllText("crash.log", msg); } catch { }
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
