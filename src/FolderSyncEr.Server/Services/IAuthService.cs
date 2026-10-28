using FolderSyncEr.Contracts;
using FolderSyncEr.Server.Data;

namespace FolderSyncEr.Server.Services;

public interface IAuthService
{
    Task<User?> GetUserByIdAsync(int userId, CancellationToken cancellationToken);
    Task<LoginResponse?> LoginAsync(LoginRequest request, string ipAddress, CancellationToken cancellationToken);
    Task<LoginResponse?> RefreshAsync(RefreshRequest request, string ipAddress, CancellationToken cancellationToken);
    Task<bool> LogoutDeviceAsync(int userId, string deviceId, CancellationToken cancellationToken);
    Task<User> CreateUserAsync(string username, string password, string role, CancellationToken cancellationToken);
    Task<bool> SeedInitialAdminAsync(string username, string password, CancellationToken cancellationToken);
}

