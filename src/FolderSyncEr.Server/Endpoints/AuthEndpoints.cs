using System.Security.Claims;
using FolderSyncEr.Contracts;
using FolderSyncEr.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace FolderSyncEr.Server.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/auth").WithTags("Authentication");

        group.MapPost("login", async (
            [FromBody] LoginRequest request,
            IAuthService authService,
            ILogService logService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var result = await authService.LoginAsync(request, ip, ct);
            if (result == null)
            {
                await logService.LogAsync(null, request.DeviceId, "Login", null, "Failed",
                    $"Invalid login attempt for username '{request.Username}'", httpContext.TraceIdentifier, ct);
                return Results.Problem("Invalid username or password.", statusCode: StatusCodes.Status401Unauthorized);
            }

            await logService.LogAsync(result.User.Id, request.DeviceId, "Login", null, "Success",
                $"Logged in on device '{request.DeviceName}'", httpContext.TraceIdentifier, ct);

            return Results.Ok(result);
        })
        .RequireRateLimiting("LoginLimiter")
        .AllowAnonymous()
        .WithName("Login");

        group.MapPost("refresh", async (
            [FromBody] RefreshRequest request,
            IAuthService authService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var result = await authService.RefreshAsync(request, ip, ct);
            if (result == null)
            {
                return Results.Problem("Invalid or expired refresh token.", statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(result);
        })
        .AllowAnonymous()
        .WithName("RefreshToken");

        group.MapPost("logout", async (
            ClaimsPrincipal user,
            IAuthService authService,
            CancellationToken ct) =>
        {
            var userIdStr = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
            var deviceId = user.FindFirstValue("did");

            if (int.TryParse(userIdStr, out var userId) && !string.IsNullOrWhiteSpace(deviceId))
            {
                await authService.LogoutDeviceAsync(userId, deviceId, ct);
            }

            return Results.Ok();
        })
        .RequireAuthorization()
        .WithName("Logout");

        group.MapGet("me", async (
            ClaimsPrincipal user,
            IAuthService authService,
            CancellationToken ct) =>
        {
            var userIdStr = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
            if (!int.TryParse(userIdStr, out var userId))
            {
                return Results.Unauthorized();
            }

            var userEntity = await authService.GetUserByIdAsync(userId, ct);
            if (userEntity == null)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new UserDto(
                userEntity.Id,
                userEntity.Username,
                userEntity.Role,
                userEntity.CreatedUtc,
                userEntity.LastLoginUtc
            ));
        })
        .RequireAuthorization()
        .WithName("GetCurrentUser");

        return app;
    }
}

