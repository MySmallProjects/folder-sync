using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderSyncEr.Client.Api;

namespace FolderSyncEr.Client.App.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    private readonly IFileSyncApi _api;
    private readonly AuthTokenManager _tokenManager;

    [ObservableProperty]
    private string _username = "admin";

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _deviceName = Environment.MachineName;

    [ObservableProperty]
    private bool _isLoggingIn;

    [ObservableProperty]
    private bool _isLoggedIn;

    [ObservableProperty]
    private string _loggedInUserText = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    public LoginViewModel(IFileSyncApi api, AuthTokenManager tokenManager)
    {
        _api = api;
        _tokenManager = tokenManager;
    }

    public async Task InitializeSessionAsync()
    {
        var session = await _tokenManager.GetSessionAsync();
        if (session != null && session.IsRefreshTokenValid)
        {
            IsLoggedIn = true;
            LoggedInUserText = $"{session.Username} ({session.Role}) on {session.DeviceId[..Math.Min(8, session.DeviceId.Length)]}";
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

        IsLoggingIn = true;
        ErrorMessage = null;

        try
        {
            var response = await _api.LoginAsync(Username, Password, DeviceName);
            IsLoggedIn = true;
            LoggedInUserText = $"{response.User.Username} ({response.User.Role})";
            Password = string.Empty; // Clear password from memory after successful login
        }
        catch (Exception ex)
        {
            IsLoggedIn = false;
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoggingIn = false;
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
            IsLoggedIn = false;
            LoggedInUserText = string.Empty;
        }
    }
}

