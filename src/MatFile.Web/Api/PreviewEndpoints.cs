using System.Security.Claims;
using System.Text;
using MatFile.Web.Data;
using MatFile.Web.Security;
using MatFile.Web.Services;
using MatFile.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace MatFile.Web.Api;

/// <summary>Vorschau: Streaming für Medien, Text als JSON und das Protokoll zu einem Eintrag.</summary>
public static class PreviewEndpoints
{
    public record TextResponse(string Content, bool Truncated, long Size);

    public record ActivityResponse(string Action, DateTime Date, string? User);

    public static IEndpointRouteBuilder MapPreviewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/fs").RequireAuthorization(MatFilePolicies.UseExplorer);

        // ---------------------------------------------------------------- Medien streamen

        group.MapGet("/preview", async (
            long rootId,
            string path,
            HttpContext http,
            StorageRootService roots,
            ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
        {
            var opened = await roots.OpenAsync(user, rootId);
            if (opened is not { } access)
            {
                return Results.Json(new { error = "root_not_found" }, statusCode: StatusCodes.Status404NotFound);
            }

            // Vorschau liefert den Dateiinhalt aus - dafür gilt dasselbe Recht wie beim Download.
            if (!access.Permissions.CanDownload)
            {
                return Results.Json(new { error = "access_denied" }, statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                var entry = await access.Provider.GetAsync(path, cancellationToken);
                if (entry is null || entry.IsDirectory)
                {
                    return Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound);
                }

                var contentType = PreviewPolicy.InlineContentType(entry.Name);
                if (contentType is null)
                {
                    return Results.Json(new { error = "not_previewable" }, statusCode: StatusCodes.Status415UnsupportedMediaType);
                }

                // Niemals raten und niemals Skripte ausführen lassen.
                http.Response.Headers[HeaderNames.ContentDisposition] =
                    new ContentDispositionHeaderValue("inline") { FileNameStar = entry.Name }.ToString();
                http.Response.Headers["X-Content-Type-Options"] = "nosniff";
                http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";

                var stream = await access.Provider.OpenReadAsync(path, cancellationToken);

                // Ohne Dateinamen, sonst setzt Results.Stream "attachment" und überschreibt das "inline" oben.
                return Results.Stream(stream, contentType, fileDownloadName: null,
                    lastModified: entry.ModifiedDate, enableRangeProcessing: true);
            }
            catch (StorageException exception)
            {
                var status = exception.Code == "not_found"
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status400BadRequest;

                return Results.Json(new { error = exception.Code }, statusCode: status);
            }
        });

        // ---------------------------------------------------------------- Textvorschau

        group.MapGet("/text", async (
            long rootId,
            string path,
            StorageRootService roots,
            ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
        {
            var opened = await roots.OpenAsync(user, rootId);
            if (opened is not { } access)
            {
                return Results.Json(new { error = "root_not_found" }, statusCode: StatusCodes.Status404NotFound);
            }

            if (!access.Permissions.CanDownload)
            {
                return Results.Json(new { error = "access_denied" }, statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                var entry = await access.Provider.GetAsync(path, cancellationToken);
                if (entry is null || entry.IsDirectory)
                {
                    return Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound);
                }

                if (PreviewPolicy.KindOf(entry.Name) != PreviewKind.Text)
                {
                    return Results.Json(new { error = "not_previewable" }, statusCode: StatusCodes.Status415UnsupportedMediaType);
                }

                await using var stream = await access.Provider.OpenReadAsync(path, cancellationToken);

                var buffer = new byte[PreviewPolicy.MaxTextBytes];
                var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
                var truncated = entry.Size > read;

                return Results.Ok(new TextResponse(
                    Encoding.UTF8.GetString(buffer, 0, read), truncated, entry.Size));
            }
            catch (StorageException exception)
            {
                return Results.Json(new { error = exception.Code }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // ---------------------------------------------------------------- Protokoll zu einem Eintrag

        group.MapGet("/activity", async (
            long rootId,
            string path,
            MatFileDbContext db,
            ClaimsPrincipal user) =>
        {
            if (user.GetUserId() is not { } userId)
            {
                return Results.Unauthorized();
            }

            var query = db.ActivityLog.AsNoTracking()
                .Where(a => a.StorageRootId == rootId && a.TargetPath == path);

            // Ohne das Recht "Protokoll einsehen" sieht man nur die eigenen Aktionen.
            if (!user.HasPermission(Permission.ViewActivityLog))
            {
                query = query.Where(a => a.UserId == userId);
            }

            var entries = await query
                .OrderByDescending(a => a.Id)
                .Take(20)
                .Select(a => new ActivityResponse(a.ActionType, a.CreateDate, a.User!.DisplayName))
                .ToListAsync();

            return Results.Ok(entries);
        });

        return app;
    }
}
