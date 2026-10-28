using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FolderSyncEr.Contracts;
using FolderSyncEr.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace FolderSyncEr.Server.Services;

public class AuthService : IAuthService
{
    private readonly ServerDbContext _db;
    private readonly IPasswordHasher<User> _passwordHasher;
    private readonly IConfiguration _config;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        ServerDbContext db,
        IPasswordHasher<User> passwordHasher,
        IConfiguration config,
        ILogger<AuthService> logger)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _config = config;
        _logger = logger;
    }

    public async Task<User?> GetUserByIdAsync(int userId, CancellationToken cancellationToken)
    {
        return await _db.Users.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDisabled, cancellationToken);
    }

    public async Task<LoginResponse?> LoginAsync(LoginRequest request, string ipAddress, CancellationToken cancellationToken)
    {
        var username = request.Username.Trim();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower(), cancellationToken);
        if (user == null || user.IsDisabled)
        {
            _logger.LogWarning("Login failed for username '{Username}' from IP {IP}: user not found or disabled", username, ipAddress);
            return null;
        }

        var verificationResult = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verificationResult == PasswordVerificationResult.Failed)
        {
            _logger.LogWarning("Login failed for username '{Username}' from IP {IP}: invalid password", username, ipAddress);
            return null;
        }

        user.LastLoginUtc = DateTime.UtcNow;

        // Register or update device
        var deviceId = string.IsNullOrWhiteSpace(request.DeviceId) ? Guid.NewGuid().ToString("N") : request.DeviceId.Trim();
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && d.UserId == user.Id, cancellationToken);
        if (device == null)
        {
            device = new Device
            {
                Id = deviceId,
                UserId = user.Id,
                Name = string.IsNullOrWhiteSpace(request.DeviceName) ? "Unknown Device" : request.DeviceName.Trim(),
                CreatedUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
                IsRevoked = false
            };
            _db.Devices.Add(device);
        }
        else
        {
            device.LastSeenUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(request.DeviceName))
            {
                device.Name = request.DeviceName.Trim();
            }
            device.IsRevoked = false;
        }

        // Generate tokens
        var (accessToken, accessExpires) = GenerateJwtToken(user, deviceId);
        var (rawRefreshToken, refreshTokenHash, refreshExpires) = GenerateRefreshToken();

        var rt = new RefreshToken
        {
            UserId = user.Id,
            DeviceId = deviceId,
            TokenHash = refreshTokenHash,
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = refreshExpires
        };
        _db.RefreshTokens.Add(rt);

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("User '{Username}' logged in successfully on device '{DeviceId}'", user.Username, deviceId);

        return new LoginResponse(
            accessToken,
            accessExpires,
            rawRefreshToken,
            refreshExpires,
            new UserDto(user.Id, user.Username, user.Role, user.CreatedUtc, user.LastLoginUtc)
        );
    }

    public async Task<LoginResponse?> RefreshAsync(RefreshRequest request, string ipAddress, CancellationToken cancellationToken)
    {
        var rawToken = request.RefreshToken.Trim();
        var tokenHash = HashToken(rawToken);
        var deviceId = request.DeviceId.Trim();

        var storedToken = await _db.RefreshTokens
            .FirstOrDefaultAsync(r => r.TokenHash == tokenHash && r.DeviceId == deviceId, cancellationToken);

        if (storedToken == null)
        {
            _logger.LogWarning("Refresh failed: token not found for device '{DeviceId}' from IP {IP}", deviceId, ipAddress);
            return null;
        }

        // Reuse detection: if token is already revoked, revoke all tokens for this device
        if (storedToken.RevokedUtc != null)
        {
            _logger.LogWarning("Refresh token reuse detected for user {UserId}, device '{DeviceId}'. Revoking all tokens.", storedToken.UserId, deviceId);
            var activeTokens = await _db.RefreshTokens
                .Where(r => r.UserId == storedToken.UserId && r.DeviceId == deviceId && r.RevokedUtc == null)
                .ToListAsync(cancellationToken);

            foreach (var t in activeTokens)
            {
                t.RevokedUtc = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync(cancellationToken);
            return null;
        }

        if (DateTime.UtcNow >= storedToken.ExpiresUtc)
        {
            _logger.LogWarning("Refresh token expired for user {UserId}, device '{DeviceId}'", storedToken.UserId, deviceId);
            return null;
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == storedToken.UserId && !u.IsDisabled, cancellationToken);
        if (user == null)
        {
            return null;
        }

        // Rotate token
        var (newAccessToken, newAccessExpires) = GenerateJwtToken(user, deviceId);
        var (newRawRefreshToken, newRefreshTokenHash, newRefreshExpires) = GenerateRefreshToken();

        var newRefreshToken = new RefreshToken
        {
            UserId = user.Id,
            DeviceId = deviceId,
            TokenHash = newRefreshTokenHash,
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = newRefreshExpires
        };
        _db.RefreshTokens.Add(newRefreshToken);
        await _db.SaveChangesAsync(cancellationToken);

        storedToken.RevokedUtc = DateTime.UtcNow;
        storedToken.ReplacedByTokenId = newRefreshToken.Id;
        await _db.SaveChangesAsync(cancellationToken);

        return new LoginResponse(
            newAccessToken,
            newAccessExpires,
            newRawRefreshToken,
            newRefreshExpires,
            new UserDto(user.Id, user.Username, user.Role, user.CreatedUtc, user.LastLoginUtc)
        );
    }

    public async Task<bool> LogoutDeviceAsync(int userId, string deviceId, CancellationToken cancellationToken)
    {
        var tokens = await _db.RefreshTokens
            .Where(r => r.UserId == userId && r.DeviceId == deviceId && r.RevokedUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var t in tokens)
        {
            t.RevokedUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<User> CreateUserAsync(string username, string password, string role, CancellationToken cancellationToken)
    {
        var user = new User
        {
            Username = username.Trim(),
            Role = role,
            CreatedUtc = DateTime.UtcNow
        };
        user.PasswordHash = _passwordHasher.HashPassword(user, password);
        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken);
        return user;
    }

    public async Task<bool> SeedInitialAdminAsync(string username, string password, CancellationToken cancellationToken)
    {
        var exists = await _db.Users.AnyAsync(cancellationToken);
        if (exists)
        {
            return false;
        }

        await CreateUserAsync(username, password, "Admin", cancellationToken);
        _logger.LogInformation("Seeded initial administrator account '{Username}'", username);
        return true;
    }

    private (string Token, DateTime ExpiresUtc) GenerateJwtToken(User user, string deviceId)
    {
        var secret = _config["Jwt:Secret"] ?? "FolderSyncEr_Default_Secret_Key_For_Development_Only_Must_Be_Long_Enough_256bits";
        var issuer = _config["Jwt:Issuer"] ?? "FolderSyncEr.Server";
        var audience = _config["Jwt:Audience"] ?? "FolderSyncEr.Client";
        var expiryMinutes = _config.GetValue<int>("Jwt:ExpiryMinutes", 15);

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var expiresUtc = DateTime.UtcNow.AddMinutes(expiryMinutes);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.Username),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim("did", deviceId)
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddSeconds(-30),
            expires: expiresUtc,
            signingCredentials: creds
        );

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresUtc);
    }

    private (string RawToken, string Hash, DateTime ExpiresUtc) GenerateRefreshToken()
    {
        var randomBytes = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        var rawToken = Convert.ToBase64String(randomBytes);

        var expiryDays = _config.GetValue<int>("Jwt:RefreshExpiryDays", 30);
        var expiresUtc = DateTime.UtcNow.AddDays(expiryDays);

        var hash = HashToken(rawToken);
        return (rawToken, hash, expiresUtc);
    }

    private static string HashToken(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

