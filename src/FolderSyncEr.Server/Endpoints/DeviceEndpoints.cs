using System.Security.Claims;
using FolderSyncEr.Contracts;
using FolderSyncEr.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FolderSyncEr.Server.Endpoints;

public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/devices").RequireAuthorization().WithTags("Devices");

        // GET /api/devices
        group.MapGet("", async (ClaimsPrincipal user, ServerDbContext db, CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            var devices = await db.Devices
                .AsNoTracking()
                .Where(d => d.UserId == userId)
                .OrderByDescending(d => d.LastSeenUtc)
                .Select(d => new DeviceDto(d.Id, d.UserId, d.Name, d.CreatedUtc, d.LastSeenUtc, d.IsRevoked))
                .ToListAsync(ct);

            return Results.Ok(devices);
        })
        .WithName("GetDevices");

        // DELETE /api/devices/{id}
        group.MapDelete("{id}", async (ClaimsPrincipal user, [FromRoute] string id, ServerDbContext db, CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            var device = await db.Devices.FirstOrDefaultAsync(d => d.UserId == userId && d.Id == id, ct);
            if (device == null)
            {
                return Results.NotFound();
            }

            device.IsRevoked = true;

            // Revoke all refresh tokens for this device
            var tokens = await db.RefreshTokens
                .Where(r => r.UserId == userId && r.DeviceId == id && r.RevokedUtc == null)
                .ToListAsync(ct);

            foreach (var t in tokens)
            {
                t.RevokedUtc = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .WithName("RevokeDevice");

        return app;
    }

    private static int GetUserId(ClaimsPrincipal user)
    {
        var idStr = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return int.Parse(idStr!);
    }
}

