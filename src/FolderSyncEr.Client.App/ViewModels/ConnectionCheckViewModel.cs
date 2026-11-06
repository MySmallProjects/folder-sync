using System;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderSyncEr.Client.Api;
using FolderSyncEr.Client.Platform;

namespace FolderSyncEr.Client.App.ViewModels;

public partial class ConnectionCheckViewModel : ViewModelBase
{
    private const string ServerUrlStoreKey = "ConfiguredServerUrl";
    private readonly IFileSyncApi _api;
    private readonly ISecretStore? _secretStore;
    private readonly ClientSettingsManager? _settingsManager;

    [ObservableProperty]
    private string _serverUrl = "http://localhost:5000";

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusBrush))]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isServerUnreachable;

    public IBrush ConnectionStatusBrush =>
        IsConnected ? ConnectedBrush : DisconnectedBrush;

    private static readonly IBrush ConnectedBrush = Brush.Parse("#10B981");
    private static readonly IBrush DisconnectedBrush = Brush.Parse("#EF4444");

    [ObservableProperty]
    private string _statusText = "Not checked";

    [ObservableProperty]
    private string _serverVersionText = "-";

    [ObservableProperty]
    private string _latencyText = "-";

    [ObservableProperty]
    private string _serverTimeText = "-";

    [ObservableProperty]
    private string _serverCompiledText = "-";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    private string? _errorMessage;

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveMessage))]
    private string? _saveMessage;

    public bool HasSaveMessage => !string.IsNullOrWhiteSpace(SaveMessage);

    public ConnectionCheckViewModel(IFileSyncApi api, ISecretStore? secretStore = null, ClientSettingsManager? settingsManager = null)
    {
        _api = api;
        _secretStore = secretStore;
        _settingsManager = settingsManager;
        _serverUrl = _api.ServerUrl;
    }

    public async Task InitializeAsync()
    {
        if (_settingsManager != null)
        {
            var settings = await _settingsManager.LoadSettingsAsync();
            if (!string.IsNullOrWhiteSpace(settings.ServerUrl))
            {
                ServerUrl = settings.ServerUrl;
                _api.ServerUrl = settings.ServerUrl;
                return;
            }
        }

        if (_secretStore != null)
        {
            var saved = await _secretStore.GetSecretAsync(ServerUrlStoreKey);
            if (!string.IsNullOrWhiteSpace(saved))
            {
                ServerUrl = saved;
                _api.ServerUrl = saved;
            }
        }
    }

    [RelayCommand]
    public async Task SaveServerUrlAsync()
    {
        SaveMessage = null;
        ErrorMessage = null;

        if (string.IsNullOrWhiteSpace(ServerUrl))
        {
            ErrorMessage = "Server URL cannot be empty.";
            return;
        }

        var trimmed = ServerUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ErrorMessage = "Invalid server URL. Please enter a valid HTTP or HTTPS URL (e.g. http://localhost:5000).";
            return;
        }

        _api.ServerUrl = trimmed;
        ServerUrl = trimmed;

        if (_secretStore != null)
        {
            await _secretStore.SetSecretAsync(ServerUrlStoreKey, trimmed);
        }

        if (_settingsManager != null)
        {
            var current = await _settingsManager.LoadSettingsAsync();
            current.ServerUrl = trimmed;
            await _settingsManager.SaveSettingsAsync(current);
        }

        SaveMessage = $"Server URL saved: {trimmed}";
    }

    private static void RunOnUi(Action action)
    {
        if (Avalonia.Application.Current == null || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            action();
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    [RelayCommand]
    public async Task CheckConnectionAsync()
    {
        RunOnUi(() =>
        {
            IsChecking = true;
            ErrorMessage = null;
            SaveMessage = null;
        });

        if (!string.IsNullOrWhiteSpace(ServerUrl))
        {
            _api.ServerUrl = ServerUrl.Trim().TrimEnd('/');
        }

        try
        {
            var result = await _api.CheckConnectionAsync();
            RunOnUi(() =>
            {
                IsConnected = result.IsConnected;
                IsServerUnreachable = !result.IsConnected;
                StatusText = result.Status;
                ServerVersionText = !string.IsNullOrWhiteSpace(result.Version) ? $"v{result.Version}" : "-";
                LatencyText = $"{result.LatencyMs} ms";
                ServerTimeText = result.ServerTimeUtc?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "-";
                ServerCompiledText = result.CompiledUtc?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "-";
                ErrorMessage = result.ErrorMessage;
            });
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                IsConnected = false;
                IsServerUnreachable = true;
                StatusText = "Error";
                ServerVersionText = "-";
                LatencyText = "-";
                ServerTimeText = "-";
                ServerCompiledText = "-";
                ErrorMessage = $"{ex.Message} (Target: {_api.ServerUrl})";
            });
        }
        finally
        {
            RunOnUi(() => IsChecking = false);
        }
    }
}
