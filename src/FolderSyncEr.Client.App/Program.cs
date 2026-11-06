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

            // Logging (Console + Rolling Daily File Logger in ~/.config/foldersyncer/Logs/app-yyyy-MM-dd.log)
            var fileLoggerProvider = new ClientFileLoggerProvider();
            services.AddSingleton(fileLoggerProvider);
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.AddProvider(fileLoggerProvider);
                builder.SetMinimumLevel(LogLevel.Information);
            });

            // Platform & Secrets
            services.AddSingleton<ClientSettingsManager>();
            services.AddSingleton<INetworkConnectivityService, NetworkConnectivityService>();
            services.AddSingleton<BackoffRetryPolicy>();
            services.AddSingleton<ISecretStore>(sp =>
            {
                var logger = sp.GetService<ILogger<ISecretStore>>();
                return SecretStoreFactory.Create(logger);
            });

            // API & Auth
            services.AddSingleton<AuthTokenManager>();
            services.AddTransient<AuthHeaderHandler>();

            services.AddHttpClient("FileSyncApi")
                .ConfigurePrimaryHttpMessageHandler(sp =>
                {
                    var settingsManager = sp.GetService<ClientSettingsManager>();
                    var handler = new System.Net.Http.SocketsHttpHandler();
                    handler.SslOptions.RemoteCertificateValidationCallback = (sender, cert, chain, sslErrors) =>
                    {
                        if (sslErrors == System.Net.Security.SslPolicyErrors.None)
                        {
                            return true;
                        }

                        if (settingsManager != null)
                        {
                            try
                            {
                                var settings = settingsManager.LoadSettings();
                                if (settings.AllowUntrustedCertificates)
                                {
                                    return true;
                                }
                            }
                            catch { }
                        }

                        return false;
                    };
                    return handler;
                })
                .AddHttpMessageHandler<AuthHeaderHandler>();

            services.AddSingleton<IFileSyncApi>(sp =>
            {
                var factory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
                var httpClient = factory.CreateClient("FileSyncApi");
                var tokenManager = sp.GetRequiredService<AuthTokenManager>();
                var logger = sp.GetRequiredService<ILogger<FileSyncApiClient>>();
                var settingsManager = sp.GetService<ClientSettingsManager>();

                var client = new FileSyncApiClient(httpClient, tokenManager, logger);

                if (settingsManager != null)
                {
                    try
                    {
                        var settings = settingsManager.LoadSettings();
                        if (!string.IsNullOrWhiteSpace(settings.ServerUrl))
                        {
                            client.ServerUrl = settings.ServerUrl;
                        }
                    }
                    catch { }
                }

                return client;
            });

            // ViewModels
            services.AddTransient<ConnectionCheckViewModel>();
            services.AddTransient<LoginViewModel>();
            services.AddTransient<FolderSelectionViewModel>();
            services.AddTransient<MainWindowViewModel>();

            App.Services = services.BuildServiceProvider();

            var startupLogger = App.Services.GetRequiredService<ILogger<Program>>();
            startupLogger.LogInformation("FolderSyncEr Client started. Log file: {LogFilePath}", fileLoggerProvider.GetCurrentLogPath());

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
