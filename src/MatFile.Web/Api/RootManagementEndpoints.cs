using System.Security.Claims;
using MatFile.Web.Controls;
using MatFile.Web.Data;
using MatFile.Web.Security;
using MatFile.Web.Services;
using MatFile.Web.Storage;

namespace MatFile.Web.Api;

/// <summary>
/// Speicherorte direkt aus dem Explorer anlegen und pflegen.
/// Rechte je Rolle bleiben der Verwaltung vorbehalten.
/// </summary>
public static class RootManagementEndpoints
{
    public record RootRequest(
        string Name,
        string Type,
        string? Path,
        string? Host,
        int Port,
        string? Share,
        string? Url,
        string? Username,
        string? Password,
        bool UseTls,
        bool Passive,
        bool IsEnabled,
        string? Icon,
        int SortOrder,
        AccessRequest[]? Access);

    /// <summary>Rechte einer Rolle auf diesem Speicherort.</summary>
    public record AccessRequest(
        long RoleId,
        bool CanRead,
        bool CanWrite,
        bool CanDelete,
        bool CanUpload,
        bool CanDownload);

    public record RootDetail(
        long Id,
        string Name,
        string Type,
        string Path,
        string? Host,
        int Port,
        string? Share,
        string? Url,
        string? Username,
        bool HasPassword,
        bool UseTls,
        bool Passive,
        bool IsEnabled,
        string? Icon,
        int SortOrder,
        IReadOnlyList<AccessDetail> Access);

    public record AccessDetail(
        long RoleId,
        string RoleName,
        bool CanRead,
        bool CanWrite,
        bool CanDelete,
        bool CanUpload,
        bool CanDownload);

    public static IEndpointRouteBuilder MapRootManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/roots").RequireAuthorization(MatFilePolicies.ManageRoots);

        // Vollständige Liste für den Verwaltungsdialog - inklusive Pfad und Rollenzahl.
        group.MapGet("/", async (string? search, string? sort, string? dir, int? page, int? pageSize,
            StorageRootService roots) =>
        {
            var query = new ListQuery
            {
                Search = search,
                Sort = sort,
                Dir = dir,
                Page = page ?? 1,
                PageSize = pageSize ?? ListQuery.DefaultPageSize
            };

            var result = await roots.ListAsync(query);
            var rolesForMatrix = await roots.GetRoleAccessAsync(null);

            return Results.Ok(new
            {
                items = result.Items.Select(r => new
                {
                    r.Id,
                    r.Name,
                    Type = r.StorageType.ToString(),
                    Path = r.BasePath,
                    r.IsEnabled,
                    r.SortOrder,
                    r.IsAvailable,
                    r.RoleCount
                }),
                total = result.TotalCount,
                page = result.Page,
                pageSize = result.PageSize,
                pageCount = result.PageCount,
                roles = rolesForMatrix.Select(r => new { r.RoleId, r.RoleName })
            });
        });

        group.MapGet("/{id:long}", async (
            long id, SecretProtector protector, StorageRootService roots) =>
        {
            var root = await roots.GetAsync(id);

            return root is null
                ? Results.NotFound()
                : Results.Ok(ToDetail(root, protector, await roots.GetRoleAccessAsync(id)));
        });

        group.MapPost("/", async (RootRequest request, StorageRootService roots, ClaimsPrincipal user) =>
            await SaveAsync(null, request, roots, user));

        group.MapPut("/{id:long}", async (long id, RootRequest request, StorageRootService roots, ClaimsPrincipal user) =>
            await SaveAsync(id, request, roots, user));

        group.MapDelete("/{id:long}", async (long id, StorageRootService roots) =>
        {
            var result = await roots.DeleteAsync(id);

            return result.Success
                ? Results.NoContent()
                : Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status400BadRequest);
        });

        // Ordner der Gegenstelle durchsehen, bevor der Speicherort gespeichert ist.
        group.MapPost("/browse", async (
            RootRequest request,
            string? path,
            StorageRootService roots,
            CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<StorageType>(request.Type, ignoreCase: true, out var type))
            {
                return Results.Json(new { error = "invalid_type" }, statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                return await roots.BrowseAsync(type, request.Path, ToConnection(request), request.Password,
                    path ?? string.Empty, cancellationToken);
            }
            catch (StorageException exception)
            {
                return Results.Json(new { error = exception.Code }, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (Exception)
            {
                return Results.Json(new { error = "not_reachable" }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapPost("/test", async (RootRequest request, StorageRootService roots) =>
        {
            if (!Enum.TryParse<StorageType>(request.Type, ignoreCase: true, out var type))
            {
                return Results.Json(new { error = "invalid_type" }, statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await roots.TestConnectionAsync(
                null, request.Name, type, request.Path ?? string.Empty, ToConnection(request), request.Password);

            return result.Success
                ? Results.Ok(new { ok = true })
                : Results.Json(new { ok = false, error = result.Error }, statusCode: StatusCodes.Status200OK);
        });

        return app;
    }

    private static async Task<IResult> SaveAsync(
        long? id, RootRequest request, StorageRootService roots, ClaimsPrincipal user)
    {
        if (!Enum.TryParse<StorageType>(request.Type, ignoreCase: true, out var type))
        {
            return Results.Json(new { error = "invalid_type" }, statusCode: StatusCodes.Status400BadRequest);
        }

        // Ohne Rechteangabe bleibt die bestehende Zuordnung unangetastet.
        var access = await MergeAccessAsync(id, request.Access, roots);

        var result = await roots.SaveAsync(
            id,
            request.Name,
            type,
            request.Path ?? string.Empty,
            request.Icon,
            request.SortOrder,
            request.IsEnabled,
            access,
            ToConnection(request),
            request.Password);

        return result.Success
            ? Results.Ok(new { id = result.Id })
            : Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Übernimmt die übergebenen Rechte, lässt aber jede Rolle unverändert,
    /// über die der Aufrufer nichts gesagt hat.
    /// </summary>
    private static async Task<List<StorageRootService.RoleAccess>> MergeAccessAsync(
        long? id, AccessRequest[]? requested, StorageRootService roots)
    {
        var current = await roots.GetRoleAccessAsync(id);

        if (requested is null || requested.Length == 0)
        {
            return current;
        }

        return current.Select(entry =>
        {
            var match = requested.FirstOrDefault(a => a.RoleId == entry.RoleId);

            return match is null
                ? entry
                : entry with
                {
                    Permissions = new RootPermissions(
                        match.CanRead, match.CanWrite, match.CanDelete,
                        match.CanUpload, match.CanDownload)
                };
        }).ToList();
    }

    private static ConnectionSettings ToConnection(RootRequest request) => new()
    {
        Host = (request.Host ?? string.Empty).Trim(),
        Port = request.Port,
        Username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim(),
        Share = string.IsNullOrWhiteSpace(request.Share) ? null : request.Share.Trim(),
        Url = string.IsNullOrWhiteSpace(request.Url) ? null : request.Url.Trim(),
        UseTls = request.UseTls,
        Passive = request.Passive
    };

    private static RootDetail ToDetail(
        StorageRoot root, SecretProtector protector, IReadOnlyList<StorageRootService.RoleAccess> access)
    {
        var connection = ConnectionSettings.Parse(root.SettingsJson);

        return new RootDetail(
            root.Id,
            root.Name,
            root.StorageType.ToString(),
            root.BasePath,
            connection.Host,
            connection.Port,
            connection.Share,
            connection.Url,
            connection.Username,
            !string.IsNullOrEmpty(root.SecretEncrypted),
            connection.UseTls,
            connection.Passive,
            root.IsEnabled,
            root.DisplayIcon,
            root.SortOrder,
            access.Select(entry => new AccessDetail(
                entry.RoleId,
                entry.RoleName,
                entry.Permissions.CanRead,
                entry.Permissions.CanWrite,
                entry.Permissions.CanDelete,
                entry.Permissions.CanUpload,
                entry.Permissions.CanDownload)).ToList());
    }
}
