using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderSyncEr.Client.Api;
using FolderSyncEr.Client.Platform;
using FolderSyncEr.Contracts;

namespace FolderSyncEr.Client.App.ViewModels;

public partial class LoggedInDeviceItemViewModel : ViewModelBase
{
    public string Id { get; }
    public string Name { get; }
    public DateTime CreatedUtc { get; }
    public DateTime LastSeenUtc { get; }
    public bool IsCurrentDevice { get; }
    public bool CanRevoke => !IsCurrentDevice;

    public string DisplayIdText => $"ID: {Id}";
    public string LastSeenText => $"Last seen: {LastSeenUtc:yyyy-MM-dd HH:mm:ss} UTC";

    public IAsyncRelayCommand RevokeCommand { get; }

    public LoggedInDeviceItemViewModel(
        DeviceDto dto,
        bool isCurrentDevice,
        Func<LoggedInDeviceItemViewModel, Task> onRevoke)
    {
        Id = dto.Id;
        Name = string.IsNullOrWhiteSpace(dto.Name) ? "Unnamed Device" : dto.Name;
        CreatedUtc = dto.CreatedUtc;
        LastSeenUtc = dto.LastSeenUtc;
        IsCurrentDevice = isCurrentDevice;
        RevokeCommand = new AsyncRelayCommand(() => onRevoke(this));
    }
}

public partial class LoginViewModel : ViewModelBase
{
    private readonly IFileSyncApi _api;
    private readonly AuthTokenManager _tokenManager;
    private readonly ClientSettingsManager? _settingsManager;

    [ObservableProperty]
    private string _username = "admin";

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _deviceName = Environment.MachineName;

    partial void OnDeviceNameChanged(string value) => OnPropertyChanged(nameof(CurrentDeviceDisplay));

    [ObservableProperty]
    private bool _isLoggingIn;

    [ObservableProperty]
    private bool _isLoggedIn;

    [ObservableProperty]
    private string _loggedInUsername = string.Empty;

    [ObservableProperty]
    private string _loggedInUserText = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isLoadingDevices;

    public ObservableCollection<LoggedInDeviceItemViewModel> LoggedInDevices { get; } = new();

    public bool HasErrorMessage => !string.IsNullOrEmpty(ErrorMessage);

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasErrorMessage));

    public string? DeviceId => _tokenManager.CurrentSession?.DeviceId;

    public string CurrentDeviceDisplay => string.IsNullOrWhiteSpace(DeviceId)
        ? DeviceName
        : $"{DeviceName} ({DeviceId})";

    public LoginViewModel(IFileSyncApi api, AuthTokenManager tokenManager, ClientSettingsManager? settingsManager = null)
    {
        _api = api;
        _tokenManager = tokenManager;
        _settingsManager = settingsManager;

        _tokenManager.SessionExpired += (_, _) =>
        {
            void HandleSessionExpired()
            {
                LoggedInUsername = string.Empty;
                LoggedInUserText = string.Empty;
                IsLoggedIn = false;
                LoggedInDevices.Clear();
                ErrorMessage = "Your session has expired or was reset on the server. Please sign in again.";
                OnPropertyChanged(nameof(DeviceId));
                OnPropertyChanged(nameof(CurrentDeviceDisplay));
            }

            if (Avalonia.Application.Current == null || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                HandleSessionExpired();
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(HandleSessionExpired);
            }
        };
    }

    public async Task InitializeSessionAsync(bool fetchDevicesFromServer = true)
    {
        if (_settingsManager != null)
        {
            var settings = await _settingsManager.LoadSettingsAsync();
            if (!string.IsNullOrWhiteSpace(settings.ServerUrl))
            {
                _api.ServerUrl = settings.ServerUrl;
            }
        }

        var session = await _tokenManager.GetSessionAsync();
        if (session != null && session.IsRefreshTokenValid)
        {
            if (!string.IsNullOrWhiteSpace(_api.ServerUrl))
            {
                await _tokenManager.UpdateServerUrlAsync(_api.ServerUrl);
            }
            else if (!string.IsNullOrWhiteSpace(session.ServerUrl))
            {
                _api.ServerUrl = session.ServerUrl;
            }

            LoggedInUsername = session.Username;
            LoggedInUserText = $"{session.Username} ({session.Role})";
            IsLoggedIn = true;
            OnPropertyChanged(nameof(DeviceId));
            OnPropertyChanged(nameof(CurrentDeviceDisplay));

            if (fetchDevicesFromServer)
            {
                await RefreshDevicesAsync();
            }
        }
    }

    [RelayCommand]
    public async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Username and password are required.";
            return;
        }

        if (_settingsManager != null)
        {
            var settings = await _settingsManager.LoadSettingsAsync();
            if (!string.IsNullOrWhiteSpace(settings.ServerUrl))
            {
                _api.ServerUrl = settings.ServerUrl;
            }
        }

        IsLoggingIn = true;
        ErrorMessage = null;

        try
        {
            var response = await _api.LoginAsync(Username, Password, DeviceName);
            LoggedInUsername = response.User.Username;
            LoggedInUserText = $"{response.User.Username} ({response.User.Role})";
            IsLoggedIn = true;
            Password = string.Empty; // Clear password from memory after successful login
            OnPropertyChanged(nameof(DeviceId));
            OnPropertyChanged(nameof(CurrentDeviceDisplay));
            await RefreshDevicesAsync();
        }
        catch (Exception ex)
        {
            IsLoggedIn = false;
            ErrorMessage = $"{ex.Message} (Target: {_api.ServerUrl})";
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    private static void RunOnUi(Action action)
    {
        if (Avalonia.Application.Current == null || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            action();
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    [RelayCommand]
    public async Task RefreshDevicesAsync()
    {
        if (!IsLoggedIn)
        {
            return;
        }

        RunOnUi(() => IsLoadingDevices = true);
        try
        {
            var devices = await _api.GetDevicesAsync();
            var currentId = DeviceId;

            RunOnUi(() =>
            {
                // Update DeviceName from the server record for the current device if available
                var currentRecord = devices.FirstOrDefault(d => string.Equals(d.Id, currentId, StringComparison.OrdinalIgnoreCase));
                if (currentRecord != null && !string.IsNullOrWhiteSpace(currentRecord.Name))
                {
                    DeviceName = currentRecord.Name;
                }

                LoggedInDevices.Clear();
                foreach (var d in devices
                    .OrderByDescending(x => string.Equals(x.Id, currentId, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(x => x.LastSeenUtc))
                {
                    var isCurrent = string.Equals(d.Id, currentId, StringComparison.OrdinalIgnoreCase);
                    LoggedInDevices.Add(new LoggedInDeviceItemViewModel(d, isCurrent, RevokeDeviceAsync));
                }

                OnPropertyChanged(nameof(DeviceId));
                OnPropertyChanged(nameof(CurrentDeviceDisplay));
            });
        }
        catch
        {
            // Keep existing device list if server is temporarily unreachable
        }
        finally
        {
            RunOnUi(() => IsLoadingDevices = false);
        }
    }

    private async Task RevokeDeviceAsync(LoggedInDeviceItemViewModel item)
    {
        try
        {
            var ok = await _api.RevokeDeviceAsync(item.Id);
            if (ok)
            {
                RunOnUi(() => LoggedInDevices.Remove(item));
            }
        }
        catch (Exception ex)
        {
            RunOnUi(() => ErrorMessage = $"Could not revoke device '{item.Name}': {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task LogoutAsync()
    {
        try
        {
            await _api.LogoutAsync();
        }
        finally
        {
            LoggedInUsername = string.Empty;
            LoggedInUserText = string.Empty;
            IsLoggedIn = false;
            LoggedInDevices.Clear();
            OnPropertyChanged(nameof(DeviceId));
            OnPropertyChanged(nameof(CurrentDeviceDisplay));
        }
    }
}
