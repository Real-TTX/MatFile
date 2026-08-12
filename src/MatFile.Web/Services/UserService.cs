using MatFile.Web.Controls;
using MatFile.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatFile.Web.Services;

public record SaveResult(bool Success, string? Error = null, long Id = 0);

/// <summary>Benutzerverwaltung für den Administrationsbereich.</summary>
public sealed class UserService
{
    private readonly MatFileDbContext _db;
    private readonly PasswordHasher _hasher;
    private readonly MatFileConfigStore _config;
    private readonly IStringLocalizer<SharedResource> _text;

    public UserService(
        MatFileDbContext db,
        PasswordHasher hasher,
        MatFileConfigStore config,
        IStringLocalizer<SharedResource> text)
    {
        _db = db;
        _hasher = hasher;
        _config = config;
        _text = text;
    }

    public record UserListItem(
        long Id,
        string Username,
        string DisplayName,
        string? Email,
        bool IsActive,
        bool IsLocked,
        DateTime? LastLoginDate,
        IReadOnlyList<string> Roles);

    public async Task<PagedResult<UserListItem>> ListAsync(ListQuery query, bool? isActive)
    {
        var users = _db.User.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            users = users.Where(u => EF.Functions.Like(u.Username, $"%{search}%")
                                     || EF.Functions.Like(u.DisplayName, $"%{search}%")
                                     || (u.Email != null && EF.Functions.Like(u.Email, $"%{search}%")));
        }

        if (isActive is { } active)
        {
            users = users.Where(u => u.IsActive == active);
        }

        users = query.Sort?.ToLowerInvariant() switch
        {
            "displayname" => query.Descending ? users.OrderByDescending(u => u.DisplayName) : users.OrderBy(u => u.DisplayName),
            "email" => query.Descending ? users.OrderByDescending(u => u.Email) : users.OrderBy(u => u.Email),
            "active" => query.Descending ? users.OrderByDescending(u => u.IsActive) : users.OrderBy(u => u.IsActive),
            "lastlogin" => query.Descending ? users.OrderByDescending(u => u.LastLoginDate) : users.OrderBy(u => u.LastLoginDate),
            _ => query.Descending ? users.OrderByDescending(u => u.Username) : users.OrderBy(u => u.Username)
        };

        var total = await users.CountAsync();
        var now = DateTime.UtcNow;

        var page = await users
            .Skip(query.Skip)
            .Take(query.NormalizedPageSize)
            .Select(u => new
            {
                u.Id,
                u.Username,
                u.DisplayName,
                u.Email,
                u.IsActive,
                IsLocked = u.LockoutUntil != null && u.LockoutUntil > now,
                u.LastLoginDate,
                Roles = u.UserRoles.Select(r => r.Role!.Name).ToList()
            })
            .ToListAsync();

        return new PagedResult<UserListItem>
        {
            Items = page.Select(u => new UserListItem(
                u.Id, u.Username, u.DisplayName, u.Email, u.IsActive, u.IsLocked, u.LastLoginDate, u.Roles)).ToList(),
            Page = query.NormalizedPage,
            PageSize = query.NormalizedPageSize,
            TotalCount = total
        };
    }

    public Task<User?> GetAsync(long id)
        => _db.User.Include(u => u.UserRoles).FirstOrDefaultAsync(u => u.Id == id);

    public Task<List<Role>> GetAllRolesAsync()
        => _db.Role.AsNoTracking().OrderBy(r => r.Name).ToListAsync();

    public async Task<SaveResult> SaveAsync(
        long? id,
        string username,
        string displayName,
        string? email,
        bool isActive,
        string? newPassword,
        bool mustChangePassword,
        IReadOnlyList<long> roleIds,
        long currentUserId)
    {
        username = username.Trim();
        displayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim();

        if (await _db.User.AnyAsync(u => u.Username == username && u.Id != id))
        {
            return new SaveResult(false, _text["User.Error.UsernameTaken"]);
        }

        if (!string.IsNullOrEmpty(newPassword) && newPassword.Length < _config.Settings.MinPasswordLength)
        {
            return new SaveResult(false, _text["ChangePassword.TooShort", _config.Settings.MinPasswordLength]);
        }

        User user;

        if (id is { } userId)
        {
            var existing = await GetAsync(userId);
            if (existing is null)
            {
                return new SaveResult(false, _text["Common.NotFound"]);
            }

            // Sich selbst nicht aussperren, und der letzte Administrator bleibt handlungsfähig.
            if (userId == currentUserId && !isActive)
            {
                return new SaveResult(false, _text["User.Error.SelfDeactivate"]);
            }

            var keepsAdminRole = await KeepsAdministratorAsync(roleIds);
            if (!keepsAdminRole && await IsLastAdministratorAsync(userId))
            {
                return new SaveResult(false, _text["User.Error.LastAdministrator"]);
            }

            if (!isActive && await IsLastAdministratorAsync(userId))
            {
                return new SaveResult(false, _text["User.Error.LastAdministrator"]);
            }

            user = existing;
        }
        else
        {
            if (string.IsNullOrEmpty(newPassword))
            {
                return new SaveResult(false, _text["User.Error.PasswordRequired"]);
            }

            user = new User();
            _db.User.Add(user);
        }

        user.Username = username;
        user.DisplayName = displayName;
        user.Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        user.IsActive = isActive;

        if (!string.IsNullOrEmpty(newPassword))
        {
            user.PasswordHash = _hasher.Hash(newPassword);
            user.MustChangePassword = mustChangePassword;
            user.FailedLoginCount = 0;
            user.LockoutUntil = null;
        }
        else if (id is not null)
        {
            user.MustChangePassword = mustChangePassword;
        }

        await _db.SaveChangesAsync();

        await UpdateRolesAsync(user, roleIds);

        // Wird ein Benutzer deaktiviert, sollen laufende Sitzungen sofort enden.
        if (!isActive)
        {
            await RevokeSessionsAsync(user.Id);
        }

        return new SaveResult(true, null, user.Id);
    }

    public async Task<SaveResult> DeleteAsync(long id, long currentUserId)
    {
        if (id == currentUserId)
        {
            return new SaveResult(false, _text["User.Error.SelfDelete"]);
        }

        var user = await _db.User.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null)
        {
            return new SaveResult(false, _text["Common.NotFound"]);
        }

        if (await IsLastAdministratorAsync(id))
        {
            return new SaveResult(false, _text["User.Error.LastAdministrator"]);
        }

        _db.User.Remove(user);
        await _db.SaveChangesAsync();
        return new SaveResult(true);
    }

    public async Task<SaveResult> UnlockAsync(long id)
    {
        var user = await _db.User.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null)
        {
            return new SaveResult(false, _text["Common.NotFound"]);
        }

        user.LockoutUntil = null;
        user.FailedLoginCount = 0;
        await _db.SaveChangesAsync();
        return new SaveResult(true, null, id);
    }

    private async Task UpdateRolesAsync(User user, IReadOnlyList<long> roleIds)
    {
        var current = await _db.UserRole.Where(ur => ur.UserId == user.Id).ToListAsync();

        var removed = current.Where(ur => !roleIds.Contains(ur.RoleId)).ToList();
        if (removed.Count > 0)
        {
            _db.UserRole.RemoveRange(removed);
        }

        foreach (var roleId in roleIds.Where(roleId => current.All(ur => ur.RoleId != roleId)))
        {
            _db.UserRole.Add(new UserRole { UserId = user.Id, RoleId = roleId });
        }

        await _db.SaveChangesAsync();
    }

    private async Task RevokeSessionsAsync(long userId)
    {
        var sessions = await _db.UserSession.Where(s => s.UserId == userId && !s.IsRevoked).ToListAsync();
        if (sessions.Count == 0)
        {
            return;
        }

        sessions.ForEach(s => s.IsRevoked = true);
        await _db.SaveChangesAsync();
    }

    private async Task<bool> KeepsAdministratorAsync(IReadOnlyList<long> roleIds)
    {
        if (roleIds.Count == 0)
        {
            return false;
        }

        return await _db.Role
            .Where(r => roleIds.Contains(r.Id))
            .AnyAsync(r => (r.Permissions & Permission.ManageUsers) == Permission.ManageUsers);
    }

    /// <summary>Prüft, ob der Benutzer der letzte aktive Administrator ist.</summary>
    private async Task<bool> IsLastAdministratorAsync(long userId)
    {
        var adminRoleIds = await _db.Role
            .Where(r => (r.Permissions & Permission.ManageUsers) == Permission.ManageUsers)
            .Select(r => r.Id)
            .ToListAsync();

        if (adminRoleIds.Count == 0)
        {
            return false;
        }

        var isAdmin = await _db.UserRole.AnyAsync(ur => ur.UserId == userId && adminRoleIds.Contains(ur.RoleId));
        if (!isAdmin)
        {
            return false;
        }

        var otherAdmins = await _db.UserRole
            .Where(ur => ur.UserId != userId && adminRoleIds.Contains(ur.RoleId) && ur.User!.IsActive)
            .Select(ur => ur.UserId)
            .Distinct()
            .CountAsync();

        return otherAdmins == 0;
    }
}
