using System.Security.Claims;
using FolderSyncEr.Contracts;
using FolderSyncEr.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace FolderSyncEr.Server.Endpoints;

public static class LogEndpoints
{
    public static IEndpointRouteBuilder MapLogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/logs").RequireAuthorization().WithTags("Logs");

        // GET /api/logs
        group.MapGet("", async (
            ClaimsPrincipal user,
            ILogService logService,
            [FromQuery] int? userId,
            [FromQuery] string? deviceId,
            [FromQuery] string? action,
            [FromQuery] string? result,
            [FromQuery] string? path,
            [FromQuery] DateTime? fromUtc,
            [FromQuery] DateTime? toUtc,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            CancellationToken ct = default) =>
        {
            var callerId = GetUserId(user);
            var isAdmin = user.IsInRole("Admin");

            var logs = await logService.GetLogsAsync(
                callerId,
                isAdmin,
                userId,
                deviceId,
                action,
                result,
                path,
                fromUtc,
                toUtc,
                page,
                pageSize,
                ct
            );

            return Results.Ok(logs);
        })
        .WithName("GetLogs");

        // POST /api/logs/client
        group.MapPost("client", async (
            ClaimsPrincipal user,
            ILogService logService,
            [FromBody] ClientActivityLogRequest request,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var callerId = GetUserId(user);
            var deviceId = user.FindFirstValue("did");

            await logService.LogAsync(
                callerId,
                deviceId,
                request.Action,
                request.Path,
                request.Result,
                request.Message,
                httpContext.TraceIdentifier,
                ct
            );

            return Results.Ok();
        })
        .WithName("PostClientLog");

        return app;
    }

    private static int GetUserId(ClaimsPrincipal user)
    {
        var idStr = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return int.Parse(idStr!);
    }
}

