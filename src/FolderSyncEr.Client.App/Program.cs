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

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
