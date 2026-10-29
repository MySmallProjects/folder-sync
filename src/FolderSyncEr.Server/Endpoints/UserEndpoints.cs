using System.Security.Claims;
using FolderSyncEr.Contracts;
using FolderSyncEr.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace FolderSyncEr.Server.Endpoints;

public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/users")
            .RequireAuthorization("AdminOnly")
            .WithTags("User Administration");

        // GET /api/users
        group.MapGet("", async (IAuthService authService, CancellationToken ct) =>
        {
            var users = await authService.ListUsersAsync(ct);
            return Results.Ok(users);
        })
        .WithName("ListUsers");

        // POST /api/users
        group.MapPost("", async (
            ClaimsPrincipal admin,
            [FromBody] CreateUserRequest request,
            IAuthService authService,
            ILogService logService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var (created, error) = await authService.CreateRegularUserAsync(request.Username, request.Password, ct);
            if (created == null)
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Title = "Cannot Create User",
                    Detail = error ?? "Failed to create user.",
                    Status = StatusCodes.Status400BadRequest
                });
            }

            await logService.LogAsync(GetUserId(admin), admin.FindFirstValue("did"), "UserAdmin", null, "Success",
                $"Created user '{created.Username}' (Id={created.Id})", httpContext.TraceIdentifier, ct);

            return Results.Created($"/api/users/{created.Id}", created);
        })
        .WithName("CreateUser");

        // PATCH /api/users/{id}/status
        group.MapPatch("{id:int}/status", async (
            ClaimsPrincipal admin,
            [FromRoute] int id,
            [FromBody] UpdateUserStatusRequest request,
            IAuthService authService,
            ILogService logService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var (updated, error, notFound) = await authService.UpdateUserStatusAsync(id, request.IsDisabled, request.IsDeleted, ct);
            if (notFound)
            {
                return Results.NotFound(new ProblemDetails
                {
                    Title = "User Not Found",
                    Detail = error ?? "User not found.",
                    Status = StatusCodes.Status404NotFound
                });
            }

            if (updated == null)
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Title = "Cannot Update User Status",
                    Detail = error ?? "Failed to update user status.",
                    Status = StatusCodes.Status400BadRequest
                });
            }

            await logService.LogAsync(GetUserId(admin), admin.FindFirstValue("did"), "UserAdmin", null, "Success",
                $"Updated user '{updated.Username}' (Id={updated.Id}): IsDisabled={updated.IsDisabled}, IsDeleted={updated.IsDeleted}",
                httpContext.TraceIdentifier, ct);

            return Results.Ok(updated);
        })
        .WithName("UpdateUserStatus");

        // PUT /api/users/{id}/password
        group.MapPut("{id:int}/password", async (
            ClaimsPrincipal admin,
            [FromRoute] int id,
            [FromBody] ChangeUserPasswordRequest request,
            IAuthService authService,
            ILogService logService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var (success, error, notFound) = await authService.ChangeUserPasswordAsync(id, request.NewPassword, ct);
            if (notFound)
            {
                return Results.NotFound(new ProblemDetails
                {
                    Title = "User Not Found",
                    Detail = error ?? "User not found.",
                    Status = StatusCodes.Status404NotFound
                });
            }

            if (!success)
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Title = "Cannot Change Password",
                    Detail = error ?? "Failed to change password.",
                    Status = StatusCodes.Status400BadRequest
                });
            }

            await logService.LogAsync(GetUserId(admin), admin.FindFirstValue("did"), "UserAdmin", null, "Success",
                $"Changed password for user Id={id}", httpContext.TraceIdentifier, ct);

            return Results.Ok();
        })
        .WithName("ChangeUserPassword");

        return app;
    }

    private static int? GetUserId(ClaimsPrincipal user)
    {
        var idStr = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return int.TryParse(idStr, out var id) ? id : null;
    }
}

