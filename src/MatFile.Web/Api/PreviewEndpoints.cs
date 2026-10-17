using System.Security.Claims;
using System.Text;
using MatFile.Web.Data;
using MatFile.Web.Security;
using MatFile.Web.Services;
using MatFile.Web.Storage;
using Microsoft.Net.Http.Headers;

namespace MatFile.Web.Api;

/// <summary>Vorschau: Streaming für Medien, Text als JSON und das Protokoll zu einem Eintrag.</summary>
public static class PreviewEndpoints
{
    public record TextResponse(string Content, bool Truncated, long Size, DateTime Modified, bool Editable);

    public record SaveTextRequest(long RootId, string Path, string? Content, DateTime? IfModified);

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

                // Gekürzt gelesen heißt: nicht bearbeitbar. Sonst würde das Speichern
                // den nicht geladenen Rest der Datei abschneiden.
                return Results.Ok(new TextResponse(
                    Encoding.UTF8.GetString(buffer, 0, read).TrimStart('﻿'),
                    truncated,
                    entry.Size,
                    entry.ModifiedDate,
                    !truncated && access.Permissions.CanWrite));
            }
            catch (StorageException exception)
            {
                return Results.Json(new { error = exception.Code }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // ---------------------------------------------------------------- Text speichern

        group.MapPut("/text", async (
            SaveTextRequest request,
            StorageRootService roots,
            FileOperationService operations,
            ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
        {
            if (user.GetUserId() is not { } userId)
            {
                return Results.Unauthorized();
            }

            var opened = await roots.OpenAsync(user, request.RootId);
            if (opened is not { } access)
            {
                return Results.Json(new { error = "root_not_found" }, statusCode: StatusCodes.Status404NotFound);
            }

            if (!access.Permissions.CanWrite)
            {
                return Results.Json(new { error = "access_denied" }, statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                var entry = await access.Provider.GetAsync(request.Path, cancellationToken);
                if (entry is null || entry.IsDirectory)
                {
                    return Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound);
                }

                if (PreviewPolicy.KindOf(entry.Name) != PreviewKind.Text)
                {
                    return Results.Json(new { error = "not_editable" }, statusCode: StatusCodes.Status415UnsupportedMediaType);
                }

                if (entry.Size > PreviewPolicy.MaxTextBytes)
                {
                    return Results.Json(new { error = "file_too_large" }, statusCode: StatusCodes.Status413PayloadTooLarge);
                }

                // Hat jemand anders in der Zwischenzeit gespeichert, wird nichts überschrieben.
                if (request.IfModified is { } known
                    && Math.Abs((entry.ModifiedDate - known).TotalSeconds) > 2)
                {
                    return Results.Json(new { error = "modified_elsewhere" }, statusCode: StatusCodes.Status409Conflict);
                }

                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                    .GetBytes(request.Content ?? string.Empty);

                await using (var target = await access.Provider.OpenWriteAsync(request.Path, overwrite: true, cancellationToken))
                {
                    await target.WriteAsync(bytes, cancellationToken);
                }

                await operations.LogEditAsync(userId, request.RootId, request.Path);

                var saved = await access.Provider.GetAsync(request.Path, cancellationToken);

                return Results.Ok(new
                {
                    size = saved?.Size ?? bytes.Length,
                    modified = saved?.ModifiedDate ?? DateTime.UtcNow
                });
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
            ActivityLogService activity,
            ClaimsPrincipal user) =>
        {
            if (user.GetUserId() is not { } userId)
            {
                return Results.Unauthorized();
            }

            // Ohne das Recht "Protokoll einsehen" sieht man nur die eigenen Aktionen.
            var entries = await activity.ForEntryAsync(rootId, path, userId, user.HasPermission(Permission.ViewActivityLog));

            return Results.Ok(entries);
        });

        return app;
    }
}
