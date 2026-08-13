using System.Security.Claims;
using MatFile.Web.Controls;
using MatFile.Web.Data;
using MatFile.Web.Security;
using MatFile.Web.Services;
using Microsoft.Extensions.Localization;

namespace MatFile.Web.Api;

/// <summary>
/// Verwaltung von Benutzern und Rollen als JSON - die Oberfläche dafür sind
/// Dialoge im Explorer, keine eigenen Seiten mehr.
/// </summary>
public static class AdminEndpoints
{
    public record UserRequest(
        string? Username,
        string? DisplayName,
        string? Email,
        bool IsActive,
        string? NewPassword,
        bool MustChangePassword,
        long[]? RoleIds);

    public record RoleRequest(string? Name, string? Description, string[]? Permissions);

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        MapUsers(app);
        MapRoles(app);
        return app;
    }

    // ---------------------------------------------------------------- Benutzer

    private static void MapUsers(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users").RequireAuthorization(MatFilePolicies.ManageUsers);

        group.MapGet("/", async (
            string? search, string? sort, string? dir, int? page, int? pageSize, bool? active,
            UserService users) =>
        {
            var query = new ListQuery
            {
                Search = search,
                Sort = sort,
                Dir = dir,
                Page = page ?? 1,
                PageSize = pageSize ?? ListQuery.DefaultPageSize
            };

            var result = await users.ListAsync(query, active);
            var roles = await users.GetAllRolesAsync();

            return Results.Ok(new
            {
                items = result.Items.Select(u => new
                {
                    u.Id,
                    u.Username,
                    u.DisplayName,
                    u.Email,
                    u.IsActive,
                    u.IsLocked,
                    u.LastLoginDate,
                    u.Roles
                }),
                total = result.TotalCount,
                page = result.Page,
                pageSize = result.PageSize,
                pageCount = result.PageCount,
                roles = roles.Select(r => new { r.Id, r.Name })
            });
        });

        group.MapGet("/{id:long}", async (long id, UserService users) =>
        {
            var user = await users.GetAsync(id);

            return user is null
                ? Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound)
                : Results.Ok(new
                {
                    user.Id,
                    user.Username,
                    user.DisplayName,
                    user.Email,
                    user.IsActive,
                    user.MustChangePassword,
                    IsLocked = user.LockoutUntil is { } until && until > DateTime.UtcNow,
                    RoleIds = user.UserRoles.Select(ur => ur.RoleId).ToList()
                });
        });

        group.MapPost("/", async (UserRequest request, UserService users, ClaimsPrincipal principal) =>
            await SaveUserAsync(null, request, users, principal));

        group.MapPut("/{id:long}", async (long id, UserRequest request, UserService users, ClaimsPrincipal principal) =>
            await SaveUserAsync(id, request, users, principal));

        group.MapDelete("/{id:long}", async (long id, UserService users, ClaimsPrincipal principal) =>
        {
            if (principal.GetUserId() is not { } currentUserId)
            {
                return Results.Unauthorized();
            }

            var result = await users.DeleteAsync(id, currentUserId);
            return ToResult(result);
        });

        group.MapPost("/{id:long}/unlock", async (long id, UserService users) => ToResult(await users.UnlockAsync(id)));
    }

    private static async Task<IResult> SaveUserAsync(
        long? id, UserRequest request, UserService users, ClaimsPrincipal principal)
    {
        if (principal.GetUserId() is not { } currentUserId)
        {
            return Results.Unauthorized();
        }

        var username = (request.Username ?? string.Empty).Trim();

        if (username.Length == 0)
        {
            return Results.Json(new { error = "name_required" }, statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await users.SaveAsync(
            id,
            username,
            request.DisplayName ?? string.Empty,
            request.Email,
            request.IsActive,
            request.NewPassword,
            request.MustChangePassword,
            request.RoleIds ?? [],
            currentUserId);

        return ToResult(result);
    }

    // ---------------------------------------------------------------- Rollen

    private static void MapRoles(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/roles").RequireAuthorization(MatFilePolicies.ManageRoles);

        group.MapGet("/", async (
            string? search, string? sort, string? dir, int? page, int? pageSize,
            RoleService roles, IStringLocalizer<SharedResource> text) =>
        {
            var query = new ListQuery
            {
                Search = search,
                Sort = sort,
                Dir = dir,
                Page = page ?? 1,
                PageSize = pageSize ?? ListQuery.DefaultPageSize
            };

            var result = await roles.ListAsync(query);

            return Results.Ok(new
            {
                items = result.Items.Select(r => new
                {
                    r.Id,
                    r.Name,
                    r.Description,
                    r.IsSystem,
                    r.UserCount,
                    Permissions = NamesOf(r.Permissions)
                }),
                total = result.TotalCount,
                page = result.Page,
                pageSize = result.PageSize,
                pageCount = result.PageCount,
                // Rechtekatalog für die Auswahl im Dialog
                catalog = RoleService.AllPermissions.Select(p => new
                {
                    Name = p.ToString(),
                    Label = text["Permission." + p].Value
                })
            });
        });

        group.MapGet("/{id:long}", async (long id, RoleService roles) =>
        {
            var role = await roles.GetAsync(id);

            return role is null
                ? Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound)
                : Results.Ok(new
                {
                    role.Id,
                    role.Name,
                    role.Description,
                    role.IsSystem,
                    Permissions = NamesOf(role.Permissions)
                });
        });

        group.MapPost("/", async (RoleRequest request, RoleService roles) => await SaveRoleAsync(null, request, roles));

        group.MapPut("/{id:long}", async (long id, RoleRequest request, RoleService roles) =>
            await SaveRoleAsync(id, request, roles));

        group.MapDelete("/{id:long}", async (long id, RoleService roles) => ToResult(await roles.DeleteAsync(id)));
    }

    private static async Task<IResult> SaveRoleAsync(long? id, RoleRequest request, RoleService roles)
    {
        var name = (request.Name ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            return Results.Json(new { error = "name_required" }, statusCode: StatusCodes.Status400BadRequest);
        }

        var permissions = Permission.None;

        foreach (var entry in request.Permissions ?? [])
        {
            if (Enum.TryParse<Permission>(entry, ignoreCase: true, out var permission))
            {
                permissions |= permission;
            }
        }

        return ToResult(await roles.SaveAsync(id, name, request.Description, permissions));
    }

    // ---------------------------------------------------------------- Gemeinsam

    private static IReadOnlyList<string> NamesOf(Permission permissions)
        => MatFilePolicies.SinglePermissions
            .Where(p => permissions.HasFlag(p))
            .Select(p => p.ToString())
            .ToList();

    private static IResult ToResult(SaveResult result)
        => result.Success
            ? Results.Ok(new { id = result.Id })
            : Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status400BadRequest);
}
