using System.Security.Claims;
using FolderSyncEr.Contracts;
using FolderSyncEr.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace FolderSyncEr.Server.Endpoints;

/// <summary>
/// Deleted items ("trash"): list tombstoned files/folders, restore them, or delete them permanently.
/// </summary>
public static class TrashEndpoints
{
    public static IEndpointRouteBuilder MapTrashEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/trash").RequireAuthorization().WithTags("Trash");

        // GET /api/trash
        group.MapGet("", async (ClaimsPrincipal user, ITrashService trash, CancellationToken ct) =>
        {
            var items = await trash.ListDeletedAsync(GetUserId(user), ct);
            return Results.Ok(items);
        })
        .WithName("ListDeletedItems");

        // POST /api/trash/{id}/restore
        group.MapPost("{id}/restore", async (
            ClaimsPrincipal user,
            ITrashService trash,
            [FromRoute] string id,
            [FromBody] RestoreRequest? request,
            CancellationToken ct) =>
        {
            var result = await trash.RestoreAsync(GetUserId(user), id, request?.TargetPath, GetDeviceId(user), ct);
            return ToHttpResult(result);
        })
        .WithName("RestoreDeletedItem");

        // DELETE /api/trash/{id}  (permanent)
        group.MapDelete("{id}", async (
            ClaimsPrincipal user,
            ITrashService trash,
            [FromRoute] string id,
            CancellationToken ct) =>
        {
            var result = await trash.PurgeAsync(GetUserId(user), id, GetDeviceId(user), ct);
            return ToHttpResult(result);
        })
        .WithName("PurgeDeletedItem");

        // DELETE /api/trash  (empty trash, permanent)
        group.MapDelete("", async (ClaimsPrincipal user, ITrashService trash, CancellationToken ct) =>
        {
            var result = await trash.EmptyTrashAsync(GetUserId(user), GetDeviceId(user), ct);
            return Results.Ok(result);
        })
        .WithName("EmptyTrash");

        return app;
    }

    private static IResult ToHttpResult<T>(TrashOpResult<T> result) => result.Status switch
    {
        TrashOpStatus.Success => Results.Ok(result.Value),
        TrashOpStatus.NotFound => Results.NotFound(new ProblemDetails { Title = "Not Found", Detail = result.Error, Status = StatusCodes.Status404NotFound }),
        TrashOpStatus.Conflict => Results.Conflict(new ProblemDetails { Title = "Conflict", Detail = result.Error, Status = StatusCodes.Status409Conflict }),
        TrashOpStatus.BadRequest => Results.BadRequest(new ProblemDetails { Title = "Bad Request", Detail = result.Error, Status = StatusCodes.Status400BadRequest }),
        TrashOpStatus.ContentMissing => Results.Problem(result.Error, statusCode: StatusCodes.Status410Gone, title: "Content Missing"),
        _ => Results.Problem("Unexpected trash operation result.")
    };

    private static int GetUserId(ClaimsPrincipal user)
    {
        var idStr = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return int.Parse(idStr!);
    }

    private static string? GetDeviceId(ClaimsPrincipal user) => user.FindFirstValue("did");
}

