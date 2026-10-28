using FolderSyncEr.Contracts;
using FolderSyncEr.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace FolderSyncEr.Server.Endpoints;

public static class StatusEndpoints
{
    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.Status, async (ServerDbContext db, CancellationToken ct) =>
        {
            var dbReachable = false;
            try
            {
                dbReachable = await db.Database.CanConnectAsync(ct);
            }
            catch
            {
                dbReachable = false;
            }

            var version = typeof(StatusEndpoints).Assembly.GetName().Version?.ToString() ?? "1.0.0";
            var response = new StatusResponse(
                Status: dbReachable ? "Healthy" : "Degraded",
                Version: version,
                ServerTimeUtc: DateTime.UtcNow,
                DbReachable: dbReachable
            );

            return Results.Ok(response);
        })
        .AllowAnonymous()
        .WithName("GetStatus")
        .WithTags("Status")
        .WithDescription("Checks service health, version, database connectivity, and server time.");

        return app;
    }
}

