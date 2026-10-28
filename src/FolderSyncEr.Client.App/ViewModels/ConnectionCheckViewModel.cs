using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderSyncEr.Client.Api;

namespace FolderSyncEr.Client.App.ViewModels;

public partial class ConnectionCheckViewModel : ViewModelBase
{
    private readonly IFileSyncApi _api;

    [ObservableProperty]
    private string _serverUrl = "http://localhost:5000";

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _statusText = "Not checked";

    [ObservableProperty]
    private string _latencyText = "-";

    [ObservableProperty]
    private string _serverTimeText = "-";

    [ObservableProperty]
    private string? _errorMessage;

    public ConnectionCheckViewModel(IFileSyncApi api)
    {
        _api = api;
        _serverUrl = _api.ServerUrl;
    }

    [RelayCommand]
    public async Task CheckConnectionAsync()
    {
        IsChecking = true;
        ErrorMessage = null;
        _api.ServerUrl = ServerUrl;

        try
        {
            var result = await _api.CheckConnectionAsync();
            IsConnected = result.IsConnected;
            StatusText = result.Status;
            LatencyText = $"{result.LatencyMs} ms";
            ServerTimeText = result.ServerTimeUtc?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "-";
            ErrorMessage = result.ErrorMessage;
        }
        catch (Exception ex)
        {
            IsConnected = false;
            StatusText = "Error";
            LatencyText = "-";
            ServerTimeText = "-";
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsChecking = false;
        }
    }
}

