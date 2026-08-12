using System.Security.Claims;
using MatFile.Web.Security;
using MatFile.Web.Services;
using MatFile.Web.Storage;

namespace MatFile.Web.Api;

/// <summary>
/// JSON-Schnittstelle für den Explorer. Alle Antworten sind relativ zum jeweiligen Root -
/// der echte Pfad im Container verlässt die Anwendung nie.
/// </summary>
public static class FileSystemEndpoints
{
    public record RootResponse(long Id, string Name, string Type, string? Icon, int SortOrder, RootPermissions Permissions);

    public record EntryResponse(string Name, string Path, bool IsDirectory, long Size, DateTime Modified, bool IsHidden, bool IsSymlink);

    public record ListResponse(
        long RootId,
        string RootName,
        string Path,
        string? ParentPath,
        RootPermissions Permissions,
        IReadOnlyList<EntryResponse> Entries);

    public static IEndpointRouteBuilder MapFileSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/fs").RequireAuthorization(MatFilePolicies.UseExplorer);

        group.MapGet("/roots", async (StorageRootService roots, ClaimsPrincipal user) =>
        {
            var accessible = await roots.GetAccessibleRootsAsync(user);

            return Results.Ok(accessible.Select(r => new RootResponse(
                r.Id, r.Name, r.StorageType.ToString(), r.DisplayIcon, r.SortOrder, r.Permissions)));
        });

        group.MapGet("/list", async (
            long rootId,
            string? path,
            StorageRootService roots,
            ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
        {
            var opened = await roots.OpenAsync(user, rootId);
            if (opened is not { } access)
            {
                return Results.Json(new { error = "root_not_found" }, statusCode: StatusCodes.Status404NotFound);
            }

            try
            {
                var listing = await access.Provider.ListAsync(path, cancellationToken);

                return Results.Ok(new ListResponse(
                    access.Root.Id,
                    access.Root.Name,
                    listing.Path,
                    listing.ParentPath,
                    access.Permissions,
                    listing.Entries.Select(e => new EntryResponse(
                        e.Name, e.Path, e.IsDirectory, e.Size, e.ModifiedDate, e.IsHidden, e.IsSymlink)).ToList()));
            }
            catch (StorageException exception)
            {
                var status = exception.Code switch
                {
                    "not_found" => StatusCodes.Status404NotFound,
                    "invalid_path" => StatusCodes.Status400BadRequest,
                    "root_unavailable" => StatusCodes.Status503ServiceUnavailable,
                    _ => StatusCodes.Status400BadRequest
                };

                return Results.Json(new { error = exception.Code }, statusCode: status);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new { error = "access_denied" }, statusCode: StatusCodes.Status403Forbidden);
            }
        });

        return app;
    }
}
