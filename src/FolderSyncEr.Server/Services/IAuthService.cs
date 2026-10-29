using FolderSyncEr.Contracts;
using FolderSyncEr.Server.Data;

namespace FolderSyncEr.Server.Services;

public enum LoginFailureReason
{
    None,
    UserNotFound,
    UserDisabled,
    UserDeleted,
    InvalidPassword
}

public record AuthLoginResult(
    bool Success,
    LoginResponse? Response,
    LoginFailureReason FailureReason,
    string? ErrorMessage
);

public interface IAuthService
{
    Task<User?> GetUserByIdAsync(int userId, CancellationToken cancellationToken);
    Task<LoginResponse?> LoginAsync(LoginRequest request, string ipAddress, CancellationToken cancellationToken);
    Task<AuthLoginResult> AuthenticateAsync(LoginRequest request, string ipAddress, CancellationToken cancellationToken);
    Task<LoginResponse?> RefreshAsync(RefreshRequest request, string ipAddress, CancellationToken cancellationToken);
    Task<bool> LogoutDeviceAsync(int userId, string deviceId, CancellationToken cancellationToken);
    Task<User> CreateUserAsync(string username, string password, string role, CancellationToken cancellationToken);
    Task<bool> SeedInitialAdminAsync(string username, string password, CancellationToken cancellationToken);
    Task<IReadOnlyList<AdminUserDto>> ListUsersAsync(CancellationToken cancellationToken);
    Task<(AdminUserDto? User, string? Error)> CreateRegularUserAsync(string username, string password, CancellationToken cancellationToken);
    Task<(AdminUserDto? User, string? Error, bool NotFound)> UpdateUserStatusAsync(int userId, bool? isDisabled, bool? isDeleted, CancellationToken cancellationToken);
    Task<(bool Success, string? Error, bool NotFound)> ChangeUserPasswordAsync(int userId, string newPassword, CancellationToken cancellationToken);
}

