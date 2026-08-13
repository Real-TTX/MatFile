using System.Security.Claims;
using MatFile.Web.Data;
using MatFile.Web.Security;
using MatFile.Web.Services;
using MatFile.Web.Storage;
using Microsoft.EntityFrameworkCore;

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
        int SortOrder);

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
        int SortOrder);

    public static IEndpointRouteBuilder MapRootManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/roots").RequireAuthorization(MatFilePolicies.ManageRoots);

        group.MapGet("/{id:long}", async (long id, MatFileDbContext db, SecretProtector protector) =>
        {
            var root = await db.StorageRoot.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);

            return root is null ? Results.NotFound() : Results.Ok(ToDetail(root, protector));
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

        // Bestehende Rechte je Rolle bleiben unangetastet.
        var access = await roots.GetRoleAccessAsync(id);

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

    private static RootDetail ToDetail(StorageRoot root, SecretProtector protector)
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
            root.SortOrder);
    }
}
