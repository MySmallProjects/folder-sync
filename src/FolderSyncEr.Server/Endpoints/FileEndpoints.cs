using System.Security.Claims;
using FolderSyncEr.Contracts;
using FolderSyncEr.Server.Data;
using FolderSyncEr.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FolderSyncEr.Server.Endpoints;

public static class FileEndpoints
{
    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api").RequireAuthorization().WithTags("Files");

        // GET /api/files
        group.MapGet("files", async (
            ClaimsPrincipal user,
            IFileService fileService,
            [FromQuery] long? sinceSeq,
            [FromQuery] DateTime? since,
            [FromQuery] bool includeDeleted = false,
            [FromQuery] string? cursor = null,
            [FromQuery] int limit = 1000,
            CancellationToken ct = default) =>
        {
            var userId = GetUserId(user);
            var response = await fileService.ListFilesAsync(userId, sinceSeq, since, includeDeleted, cursor, limit, ct);
            return Results.Ok(response);
        })
        .WithName("ListFiles");

        // GET /api/files/meta?path=
        group.MapGet("files/meta", async (
            ClaimsPrincipal user,
            IFileService fileService,
            [FromQuery] string path,
            CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            var file = await fileService.GetFileByPathAsync(userId, path, ct);
            if (file == null)
            {
                return Results.NotFound();
            }

            return Results.Ok(new FileDto(
                file.Id,
                file.Path,
                file.IsDirectory,
                file.Size,
                file.Sha256,
                file.Version,
                file.LastModifiedUtc,
                file.UploadedAtUtc,
                file.UploadedByDeviceId,
                file.IsDeleted,
                file.ChangeSeq
            ));
        })
        .WithName("GetFileMetaByPath");

        // GET /api/files/{id}
        group.MapGet("files/{id}", async (
            ClaimsPrincipal user,
            IFileService fileService,
            [FromRoute] string id,
            CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            var file = await fileService.GetFileByIdAsync(userId, id, ct);
            if (file == null)
            {
                return Results.NotFound();
            }

            return Results.Ok(new FileDto(
                file.Id,
                file.Path,
                file.IsDirectory,
                file.Size,
                file.Sha256,
                file.Version,
                file.LastModifiedUtc,
                file.UploadedAtUtc,
                file.UploadedByDeviceId,
                file.IsDeleted,
                file.ChangeSeq
            ));
        })
        .WithName("GetFileMetadata");

        // GET /api/files/{id}/content
        group.MapGet("files/{id}/content", async (
            ClaimsPrincipal user,
            IFileService fileService,
            IBlobStorageService blobStorage,
            ILogService logService,
            [FromRoute] string id,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            var file = await fileService.GetFileByIdAsync(userId, id, ct);
            if (file == null || file.IsDeleted || file.IsDirectory)
            {
                return Results.NotFound();
            }

            var prefix = file.Id.Length >= 2 ? file.Id[..2] : "00";
            var relativeBlobPath = Path.Combine(userId.ToString(), prefix, file.Id, $"{file.Version}.blob");

            var stream = blobStorage.OpenRead(relativeBlobPath);
            if (stream == null)
            {
                return Results.NotFound();
            }

            if (!string.IsNullOrEmpty(file.Sha256))
            {
                httpContext.Response.Headers.ETag = $"\"{file.Sha256}\"";
            }
            httpContext.Response.Headers[ApiHeaders.LastModifiedUtc] = file.LastModifiedUtc.ToString("o");

            await logService.LogAsync(userId, GetDeviceId(user), "Download", file.Path, "Success",
                $"Downloaded v{file.Version} ({file.Size} bytes)", httpContext.TraceIdentifier, ct);

            return Results.File(
                fileStream: stream,
                contentType: "application/octet-stream",
                fileDownloadName: Path.GetFileName(file.Path),
                enableRangeProcessing: true
            );
        })
        .WithName("DownloadFileContent");

        // PUT /api/files?path=
        group.MapPut("files", async (
            ClaimsPrincipal user,
            IFileService fileService,
            ILogService logService,
            [FromQuery] string path,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            var deviceId = GetDeviceId(user);

            var req = httpContext.Request;
            var providedSha256 = req.Headers[ApiHeaders.ContentSha256].FirstOrDefault();
            var ifMatch = req.Headers[ApiHeaders.IfMatch].FirstOrDefault();
            var ifNoneMatch = req.Headers[ApiHeaders.IfNoneMatch].FirstOrDefault();

            long? baseVersion = null;
            if (long.TryParse(req.Headers[ApiHeaders.BaseVersion].FirstOrDefault(), out var bv))
            {
                baseVersion = bv;
            }

            DateTime? lastModifiedUtc = null;
            if (DateTime.TryParse(req.Headers[ApiHeaders.LastModifiedUtc].FirstOrDefault(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var mtime))
            {
                lastModifiedUtc = mtime.ToUniversalTime();
            }

            var cmd = new UploadFileCommand(
                UserId: userId,
                RawPath: path,
                InputStream: req.Body,
                ProvidedSha256: providedSha256,
                IfMatchSha256: ifMatch,
                IfNoneMatch: ifNoneMatch,
                BaseVersion: baseVersion,
                LastModifiedUtc: lastModifiedUtc,
                DeviceId: deviceId
            );

            var result = await fileService.UploadFileAsync(cmd, ct);

            if (result.IsBadRequest)
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Title = "Bad Request",
                    Detail = result.ErrorMessage,
                    Status = StatusCodes.Status400BadRequest
                });
            }

            if (result.IsHashMismatch)
            {
                await logService.LogAsync(userId, deviceId, "Upload", path, "Rejected",
                    result.ErrorMessage, httpContext.TraceIdentifier, ct);

                return Results.UnprocessableEntity(new ProblemDetails
                {
                    Title = "SHA-256 Hash Mismatch",
                    Detail = result.ErrorMessage,
                    Status = StatusCodes.Status422UnprocessableEntity
                });
            }

            if (result.IsConflict)
            {
                await logService.LogAsync(userId, deviceId, "Conflict", path, "Conflict",
                    result.ErrorMessage, httpContext.TraceIdentifier, ct);

                return Results.Json(result.Conflict, statusCode: StatusCodes.Status409Conflict);
            }

            if (!result.IsSuccess || result.File == null)
            {
                return Results.Problem("Upload failed.", statusCode: StatusCodes.Status500InternalServerError);
            }

            var isCreated = result.File.Version == 1;
            httpContext.Response.Headers.ETag = $"\"{result.File.Sha256}\"";

            return isCreated
                ? Results.Created($"api/files/{result.File.Id}", result.File)
                : Results.Ok(result.File);
        })
        .Accepts<Stream>("application/octet-stream")
        .WithName("UploadFile");

        // POST /api/directories
        group.MapPost("directories", async (
            ClaimsPrincipal user,
            IFileService fileService,
            [FromBody] CreateDirectoryRequest request,
            CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            var deviceId = GetDeviceId(user);

            try
            {
                var dir = await fileService.CreateDirectoryAsync(userId, request.Path, deviceId, ct);
                return Results.Ok(new FileDto(
                    dir.Id,
                    dir.Path,
                    dir.IsDirectory,
                    dir.Size,
                    dir.Sha256,
                    dir.Version,
                    dir.LastModifiedUtc,
                    dir.UploadedAtUtc,
                    dir.UploadedByDeviceId,
                    dir.IsDeleted,
                    dir.ChangeSeq
                ));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Title = "Invalid Directory Path",
                    Detail = ex.Message,
                    Status = StatusCodes.Status400BadRequest
                });
            }
        })
        .WithName("CreateDirectory");

        // DELETE /api/files/{id}
        group.MapDelete("files/{id}", async (
            ClaimsPrincipal user,
            IFileService fileService,
            [FromRoute] string id,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            var deviceId = GetDeviceId(user);

            var ifMatch = httpContext.Request.Headers[ApiHeaders.IfMatch].FirstOrDefault();
            long? baseVersion = null;
            if (long.TryParse(httpContext.Request.Headers[ApiHeaders.BaseVersion].FirstOrDefault(), out var bv))
            {
                baseVersion = bv;
            }

            var result = await fileService.DeleteFileAsync(userId, id, ifMatch, baseVersion, deviceId, ct);

            if (result.IsNotFound)
            {
                return Results.NotFound();
            }

            if (result.IsConflict)
            {
                return Results.Json(result.Conflict, statusCode: StatusCodes.Status409Conflict);
            }

            return Results.NoContent();
        })
        .WithName("DeleteFile");

        // POST /api/files/move
        group.MapPost("files/move", async (
            ClaimsPrincipal user,
            IFileService fileService,
            [FromBody] MoveFileRequest request,
            CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            var deviceId = GetDeviceId(user);

            var moved = await fileService.MoveFileAsync(userId, request.FromPath, request.ToPath, request.IfMatchSha256, deviceId, ct);
            if (moved == null)
            {
                return Results.Problem("Move failed or source/target invalid or conflicting.", statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Ok(new FileDto(
                moved.Id,
                moved.Path,
                moved.IsDirectory,
                moved.Size,
                moved.Sha256,
                moved.Version,
                moved.LastModifiedUtc,
                moved.UploadedAtUtc,
                moved.UploadedByDeviceId,
                moved.IsDeleted,
                moved.ChangeSeq
            ));
        })
        .WithName("MoveFile");

        // PATCH /api/files/{id}
        group.MapPatch("files/{id}", async (
            ClaimsPrincipal user,
            IFileService fileService,
            [FromRoute] string id,
            [FromBody] PatchFileRequest request,
            CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            var updated = await fileService.PatchLastModifiedAsync(userId, id, request.LastModifiedUtc, ct);
            if (updated == null)
            {
                return Results.NotFound();
            }

            return Results.Ok(new FileDto(
                updated.Id,
                updated.Path,
                updated.IsDirectory,
                updated.Size,
                updated.Sha256,
                updated.Version,
                updated.LastModifiedUtc,
                updated.UploadedAtUtc,
                updated.UploadedByDeviceId,
                updated.IsDeleted,
                updated.ChangeSeq
            ));
        })
        .WithName("PatchFileLastModified");

        // GET /api/files/export (Admin inspection / archive download)
        group.MapGet("files/export", async (
            ClaimsPrincipal user,
            ServerDbContext db,
            IBlobStorageService blobStorage,
            CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            var files = await db.Files.AsNoTracking()
                .Where(f => f.UserId == userId && !f.IsDeleted && !f.IsDirectory)
                .ToListAsync(ct);

            var memStream = new MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(memStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var file in files)
                {
                    var entry = archive.CreateEntry(file.Path, System.IO.Compression.CompressionLevel.Fastest);
                    entry.LastWriteTime = file.LastModifiedUtc;
                    using var entryStream = entry.Open();
                    try
                    {
                        var prefix = file.Id.Length >= 2 ? file.Id[..2] : "00";
                        var relativeBlobPath = Path.Combine(userId.ToString(), prefix, file.Id, $"{file.Version}.blob");
                        using var blobStream = blobStorage.OpenRead(relativeBlobPath);
                        if (blobStream != null)
                        {
                            await blobStream.CopyToAsync(entryStream, ct);
                        }
                    }
                    catch
                    {
                        // Blob missing, continue
                    }
                }
            }
            memStream.Position = 0;
            return Results.File(memStream, "application/zip", $"foldersyncer-export-{DateTime.UtcNow:yyyyMMddHHmmss}.zip");
        })
        .WithName("ExportUserFilesZip");

        // GET /api/storage
        group.MapGet("storage", async (
            ClaimsPrincipal user,
            IFileService fileService,
            CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            var storage = await fileService.GetStorageInfoAsync(userId, ct);
            return Results.Ok(storage);
        })
        .WithName("GetStorageInfo");

        return app;
    }

    private static int GetUserId(ClaimsPrincipal user)
    {
        var idStr = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return int.Parse(idStr!);
    }

    private static string? GetDeviceId(ClaimsPrincipal user)
    {
        return user.FindFirstValue("did");
    }
}

