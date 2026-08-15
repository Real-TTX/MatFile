using System.IO.Compression;
using System.Security.Claims;
using MatFile.Web.Data;
using MatFile.Web.Security;
using MatFile.Web.Services;
using MatFile.Web.Storage;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace MatFile.Web.Api;

/// <summary>Schreibende Dateioperationen, Upload, ZIP-Download und Jobs.</summary>
public static class FileOperationEndpoints
{
    public record FolderRequest(long RootId, string? Path, string Name);

    public record RenameRequest(long RootId, string Path, string NewName);

    public record DeleteRequest(long RootId, string[] Paths);

    public record TransferRequest(long SourceRootId, string[] Paths, long TargetRootId, string? TargetPath, bool Move, bool Overwrite);

    public record CompressRequest(long RootId, string[] Paths, string? TargetPath, string Name);

    public record ExtractRequest(long RootId, string Path, string? TargetPath);

    public record UploadBeginRequest(long RootId, string? Path, string Name, long Size, bool Overwrite);

    public record PermissionRequest(long RootId, string? Path, int Mode);

    public static IEndpointRouteBuilder MapFileOperationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/fs").RequireAuthorization(MatFilePolicies.UseExplorer);

        // ---------------------------------------------------------------- Sofortige Aktionen

        group.MapPost("/folder", (FolderRequest request, FileOperationService operations, ClaimsPrincipal user)
            => Run(user, id => operations.CreateFolderAsync(id, request.RootId, request.Path, request.Name)));

        group.MapPost("/file", (FolderRequest request, FileOperationService operations, ClaimsPrincipal user)
            => Run(user, id => operations.CreateFileAsync(id, request.RootId, request.Path, request.Name)));

        group.MapPost("/rename", (RenameRequest request, FileOperationService operations, ClaimsPrincipal user)
            => Run(user, id => operations.RenameAsync(id, request.RootId, request.Path, request.NewName)));

        // ---------------------------------------------------------------- Rechte (nur wo das Protokoll sie kennt)

        group.MapGet("/permissions", async (
            long rootId,
            string? path,
            FileOperationService operations,
            ClaimsPrincipal user) =>
        {
            if (user.GetUserId() is not { } userId)
            {
                return Results.Unauthorized();
            }

            try
            {
                var (permissions, canChange) = await operations.GetPermissionsAsync(userId, rootId, path ?? string.Empty);

                if (permissions is null)
                {
                    return Results.Ok(new { supported = false });
                }

                return Results.Ok(new
                {
                    supported = true,
                    canChange,
                    mode = permissions.Mode,
                    modeText = PermissionMode.ToText(permissions.Mode),
                    permissions.Owner,
                    permissions.Group
                });
            }
            catch (StorageException exception)
            {
                return Problem(exception);
            }
        });

        group.MapPut("/permissions", async (
            PermissionRequest request,
            FileOperationService operations,
            ClaimsPrincipal user) =>
        {
            if (user.GetUserId() is not { } userId)
            {
                return Results.Unauthorized();
            }

            try
            {
                await operations.SetPermissionsAsync(userId, request.RootId, request.Path ?? string.Empty, request.Mode);

                return Results.Ok(new
                {
                    mode = request.Mode,
                    modeText = PermissionMode.ToText(request.Mode)
                });
            }
            catch (StorageException exception)
            {
                return Problem(exception);
            }
        });

        // ---------------------------------------------------------------- Jobs

        group.MapPost("/delete", (DeleteRequest request, FileOperationService operations, ClaimsPrincipal user)
            => RunJob(user, id => operations.EnqueueDeleteAsync(id, request.RootId, request.Paths)));

        group.MapPost("/transfer", (TransferRequest request, FileOperationService operations, ClaimsPrincipal user)
            => RunJob(user, id => operations.EnqueueTransferAsync(
                id, request.SourceRootId, request.Paths, request.TargetRootId,
                request.TargetPath ?? string.Empty, request.Move, request.Overwrite)));

        group.MapPost("/compress", (CompressRequest request, FileOperationService operations, ClaimsPrincipal user)
            => RunJob(user, id => operations.EnqueueCompressAsync(
                id, request.RootId, request.Paths, request.TargetPath ?? string.Empty, request.Name)));

        group.MapPost("/extract", (ExtractRequest request, FileOperationService operations, ClaimsPrincipal user)
            => RunJob(user, id => operations.EnqueueExtractAsync(
                id, request.RootId, request.Path, request.TargetPath ?? string.Empty)));

        // ---------------------------------------------------------------- ZIP-Download

        group.MapGet("/zip", async (
            long rootId,
            HttpContext http,
            StorageRootService roots,
            ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
        {
            var paths = http.Request.Query["path"].Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToArray();

            var opened = await roots.OpenAsync(user, rootId);
            if (opened is not { } access)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (!access.Permissions.CanDownload || paths.Length == 0)
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var fileName = paths.Length == 1
                ? Path.GetFileNameWithoutExtension(paths[0].TrimEnd('/').Split('/').Last()) + ".zip"
                : access.Root.Name + ".zip";

            http.Response.ContentType = "application/zip";
            http.Response.Headers[HeaderNames.ContentDisposition] =
                new ContentDispositionHeaderValue("attachment") { FileNameStar = fileName }.ToString();

            // ZipArchive schreibt synchron in den Zielstrom - für diese Antwort erlauben wir das.
            var bodyControl = http.Features.Get<IHttpBodyControlFeature>();
            if (bodyControl is not null)
            {
                bodyControl.AllowSynchronousIO = true;
            }

            using var archive = new ZipArchive(http.Response.Body, ZipArchiveMode.Create, leaveOpen: true);

            foreach (var path in paths)
            {
                var entry = await access.Provider.GetAsync(path, cancellationToken);
                if (entry is null)
                {
                    continue;
                }

                await AddToArchiveAsync(archive, access.Provider, entry, entry.Name, cancellationToken);
            }
        });

        // ---------------------------------------------------------------- Upload

        group.MapPost("/upload/begin", async (UploadBeginRequest request, UploadService uploads, ClaimsPrincipal user) =>
        {
            if (user.GetUserId() is not { } userId)
            {
                return Results.Unauthorized();
            }

            try
            {
                var session = await uploads.BeginAsync(
                    userId, request.RootId, request.Path, request.Name, request.Size, request.Overwrite);

                return Results.Ok(new { uploadId = session.Id, received = 0L });
            }
            catch (StorageException exception)
            {
                return Problem(exception);
            }
        });

        group.MapPost("/upload/chunk", async (
            Guid uploadId,
            long offset,
            HttpContext http,
            UploadService uploads,
            ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
        {
            if (user.GetUserId() is not { } userId)
            {
                return Results.Unauthorized();
            }

            try
            {
                var received = await uploads.AppendAsync(uploadId, userId, offset, http.Request.Body, cancellationToken);
                return Results.Ok(new { received });
            }
            catch (StorageException exception)
            {
                return Problem(exception);
            }
        });

        group.MapPost("/upload/finish", async (Guid uploadId, UploadService uploads, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            if (user.GetUserId() is not { } userId)
            {
                return Results.Unauthorized();
            }

            try
            {
                return Results.Ok(await uploads.FinishAsync(uploadId, userId, cancellationToken));
            }
            catch (StorageException exception)
            {
                return Problem(exception);
            }
        });

        group.MapPost("/upload/abort", async (Guid uploadId, UploadService uploads, ClaimsPrincipal user) =>
        {
            if (user.GetUserId() is not { } userId)
            {
                return Results.Unauthorized();
            }

            try
            {
                await uploads.AbortAsync(uploadId, userId);
                return Results.NoContent();
            }
            catch (StorageException exception)
            {
                return Problem(exception);
            }
        });

        // ---------------------------------------------------------------- Jobs abfragen

        var jobGroup = app.MapGroup("/api/jobs").RequireAuthorization(MatFilePolicies.UseExplorer);

        jobGroup.MapGet("/", async (MatFileDbContext db, ClaimsPrincipal user) =>
        {
            if (user.GetUserId() is not { } userId)
            {
                return Results.Unauthorized();
            }

            var jobs = await JobService.RecentAsync(db, userId);
            return Results.Ok(jobs.Select(JobService.ToView));
        });

        jobGroup.MapPost("/{token:guid}/cancel", async (Guid token, MatFileDbContext db, JobService jobs, ClaimsPrincipal user) =>
        {
            if (user.GetUserId() is not { } userId)
            {
                return Results.Unauthorized();
            }

            var job = await db.JobTask.FirstOrDefaultAsync(j => j.Token == token && j.UserId == userId);
            if (job is null)
            {
                return Results.NotFound();
            }

            if (job.Status == JobStatus.Pending)
            {
                job.Status = JobStatus.Cancelled;
                job.FinishedDate = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            else
            {
                jobs.Cancel(job.Id);
            }

            return Results.NoContent();
        });

        return app;
    }

    private static async Task AddToArchiveAsync(
        ZipArchive archive, IStorageProvider provider, StorageEntry entry, string entryName, CancellationToken cancellationToken)
    {
        if (!entry.IsDirectory)
        {
            var zipEntry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            await using var target = zipEntry.Open();
            await using var source = await provider.OpenReadAsync(entry.Path, cancellationToken);
            await source.CopyToAsync(target, cancellationToken);
            return;
        }

        var listing = await provider.ListAsync(entry.Path, cancellationToken);

        if (listing.Entries.Count == 0)
        {
            archive.CreateEntry(entryName + "/");
            return;
        }

        foreach (var child in listing.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AddToArchiveAsync(archive, provider, child, $"{entryName}/{child.Name}", cancellationToken);
        }
    }

    private static async Task<IResult> Run(ClaimsPrincipal user, Func<long, Task<StorageEntry>> action)
    {
        if (user.GetUserId() is not { } userId)
        {
            return Results.Unauthorized();
        }

        try
        {
            return Results.Ok(await action(userId));
        }
        catch (StorageException exception)
        {
            return Problem(exception);
        }
    }

    private static async Task<IResult> RunJob(ClaimsPrincipal user, Func<long, Task<JobTask>> action)
    {
        if (user.GetUserId() is not { } userId)
        {
            return Results.Unauthorized();
        }

        try
        {
            return Results.Ok(JobService.ToView(await action(userId)));
        }
        catch (StorageException exception)
        {
            return Problem(exception);
        }
    }

    private static IResult Problem(StorageException exception)
    {
        var status = exception.Code switch
        {
            "not_found" or "root_not_found" or "upload_not_found" => StatusCodes.Status404NotFound,
            "access_denied" => StatusCodes.Status403Forbidden,
            "conflict" => StatusCodes.Status409Conflict,
            "permissions_unsupported" => StatusCodes.Status501NotImplemented,
            "upload_too_large" => StatusCodes.Status413PayloadTooLarge,
            "root_unavailable" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest
        };

        return Results.Json(new { error = exception.Code }, statusCode: status);
    }
}
