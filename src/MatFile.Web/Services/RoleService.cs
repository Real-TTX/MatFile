using MatFile.Web.Controls;
using MatFile.Web.Data;
using MatFile.Web.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatFile.Web.Services;

/// <summary>Rollenverwaltung für den Administrationsbereich.</summary>
public sealed class RoleService
{
    private readonly MatFileDbContext _db;
    private readonly IStringLocalizer<SharedResource> _text;

    public RoleService(MatFileDbContext db, IStringLocalizer<SharedResource> text)
    {
        _db = db;
        _text = text;
    }

    public record RoleListItem(
        long Id,
        string Name,
        string? Description,
        bool IsSystem,
        Permission Permissions,
        int UserCount);

    public async Task<PagedResult<RoleListItem>> ListAsync(ListQuery query)
    {
        var roles = _db.Role.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            roles = roles.Where(r => EF.Functions.Like(r.Name, $"%{search}%")
                                     || (r.Description != null && EF.Functions.Like(r.Description, $"%{search}%")));
        }

        roles = query.Sort?.ToLowerInvariant() switch
        {
            "description" => query.Descending ? roles.OrderByDescending(r => r.Description) : roles.OrderBy(r => r.Description),
            "system" => query.Descending ? roles.OrderByDescending(r => r.IsSystem) : roles.OrderBy(r => r.IsSystem),
            _ => query.Descending ? roles.OrderByDescending(r => r.Name) : roles.OrderBy(r => r.Name)
        };

        var total = await roles.CountAsync();

        var page = await roles
            .Skip(query.Skip)
            .Take(query.NormalizedPageSize)
            .Select(r => new RoleListItem(
                r.Id, r.Name, r.Description, r.IsSystem, r.Permissions, r.UserRoles.Count()))
            .ToListAsync();

        return new PagedResult<RoleListItem>
        {
            Items = page,
            Page = query.NormalizedPage,
            PageSize = query.NormalizedPageSize,
            TotalCount = total
        };
    }

    public Task<Role?> GetAsync(long id) => _db.Role.FirstOrDefaultAsync(r => r.Id == id);

    public async Task<SaveResult> SaveAsync(long? id, string name, string? description, Permission permissions)
    {
        name = name.Trim();

        if (await _db.Role.AnyAsync(r => r.Name == name && r.Id != id))
        {
            return new SaveResult(false, _text["Role.Error.NameTaken"]);
        }

        Role role;

        if (id is { } roleId)
        {
            var existing = await GetAsync(roleId);
            if (existing is null)
            {
                return new SaveResult(false, _text["Common.NotFound"]);
            }

            if (existing.IsSystem)
            {
                // Systemrollen behalten Name und Rechte, damit niemand sich selbst aussperrt.
                existing.Description = description;
                await _db.SaveChangesAsync();
                return new SaveResult(true, null, existing.Id);
            }

            role = existing;
        }
        else
        {
            role = new Role();
            _db.Role.Add(role);
        }

        role.Name = name;
        role.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        role.Permissions = permissions;

        await _db.SaveChangesAsync();
        return new SaveResult(true, null, role.Id);
    }

    public async Task<SaveResult> DeleteAsync(long id)
    {
        var role = await _db.Role.FirstOrDefaultAsync(r => r.Id == id);
        if (role is null)
        {
            return new SaveResult(false, _text["Common.NotFound"]);
        }

        if (role.IsSystem)
        {
            return new SaveResult(false, _text["Role.Error.SystemRole"]);
        }

        if (await _db.UserRole.AnyAsync(ur => ur.RoleId == id))
        {
            return new SaveResult(false, _text["Role.Error.InUse"]);
        }

        _db.Role.Remove(role);
        await _db.SaveChangesAsync();
        return new SaveResult(true);
    }

    /// <summary>Übersetzter Anzeigename eines Rechts, z. B. für Checkbox-Listen.</summary>
    public string PermissionLabel(Permission permission) => _text["Permission." + permission];

    public static IReadOnlyList<Permission> AllPermissions => MatFilePolicies.SinglePermissions.ToList();
}
