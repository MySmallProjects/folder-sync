using FolderSyncEr.Contracts;
using FolderSyncEr.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace FolderSyncEr.Server.Endpoints;

public static class SettingEndpoints
{
    public static IEndpointRouteBuilder MapSettingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/settings")
            .RequireAuthorization(policy => policy.RequireRole("Admin"))
            .WithTags("Settings");

        // GET /api/settings
        group.MapGet("", async (ISettingsService settingsService, CancellationToken ct) =>
        {
            var settings = await settingsService.GetAllSettingsAsync(ct);
            return Results.Ok(settings);
        })
        .WithName("GetSettings");

        // PUT /api/settings/{key}
        group.MapPut("{key}", async (
            [FromRoute] string key,
            [FromBody] UpdateSettingRequest request,
            ISettingsService settingsService,
            CancellationToken ct) =>
        {
            try
            {
                await settingsService.UpdateSettingAsync(key, request.Value, ct);
                return Results.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Title = "Cannot Update Setting",
                    Detail = ex.Message,
                    Status = StatusCodes.Status400BadRequest
                });
            }
        })
        .WithName("UpdateSetting");

        return app;
    }
}

