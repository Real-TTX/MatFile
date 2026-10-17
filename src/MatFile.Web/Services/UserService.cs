using System.Text.Json;
using MatFile.Web.Controls;
using MatFile.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatFile.Web.Services;

public record SaveResult(bool Success, string? Error = null, long Id = 0);

/// <summary>Eine Person, die MatIdentity angemeldet hat: Kennung (sub), E-Mail, Name, Benutzername und - wenn MatIdentity die Rolle bestimmt - die Rolle.</summary>
public record ExternalIdentity(string Subject, string? Email, string? Name, string? PreferredUserName, string? Role);

/// <summary>
/// Ergebnis von <see cref="UserService.SignInExternalAsync"/>: das Konto (<see cref="UserId"/>), oder das Konto, das erst nach seinem Kennwort
/// verknüpft wird (<see cref="LinkUserId"/>), oder eine Ablehnung (<see cref="Error"/>).
/// </summary>
public record ExternalSignIn(long? UserId, long? LinkUserId = null, string? Error = null)
{
    public static ExternalSignIn Refused(string error) => new(null, null, error);
}

/// <summary>Benutzerverwaltung für den Administrationsbereich und die Konten der Anmeldung über MatIdentity.</summary>
public sealed class UserService
{
    /// <summary>Die Rollen, die MatIdentity im Claim <c>role</c> vergibt (MatApiDefinition, roles): admin und user.</summary>
    public const string IdentityAdminRole = "admin";
    public const string IdentityUserRole = "user";

    private readonly MatFileDbContext _db;
    private readonly PasswordHasher _hasher;
    private readonly MatFileConfigStore _config;
    private readonly IStringLocalizer<SharedResource> _text;
    private readonly ILogger<UserService> _logger;

    public UserService(
        MatFileDbContext db,
        PasswordHasher hasher,
        MatFileConfigStore config,
        IStringLocalizer<SharedResource> text,
        ILogger<UserService> logger)
    {
        _db = db;
        _hasher = hasher;
        _config = config;
        _text = text;
        _logger = logger;
    }

    // ---------------------------------------------------------------- Anmeldung über MatIdentity

    /// <summary>
    /// Das Konto zu einer Person von MatIdentity: (1) das Konto mit ihrer Kennung (sub); (2) sonst, wenn genau ein aktives, noch nicht verknüpftes
    /// Konto mit Kennwort dieselbe E-Mail-Adresse hat (ohne Treffer: denselben Benutzernamen), wird es NICHT verknüpft: die Adresse beweist nichts,
    /// die Person meldet sich einmal mit dem Kennwort dieses Kontos an (<see cref="LinkExternalAsync"/>); (3) sonst ein neues Konto ohne Kennwort.
    /// Abgelehnt: ein deaktiviertes Konto, mehrere Treffer, ein Treffer, der schon mit einer anderen Kennung verknüpft ist.
    /// Die Rolle folgt dem Claim, wenn MatIdentity sie bestimmt (<see cref="ApplyIdentityRoleAsync"/>).
    /// </summary>
    public async Task<ExternalSignIn> SignInExternalAsync(ExternalIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (string.IsNullOrWhiteSpace(identity.Subject))
        {
            return ExternalSignIn.Refused("MatIdentity sent no subject (sub).");
        }

        var subject = identity.Subject.Trim();
        var user = await _db.User.FirstOrDefaultAsync(u => u.ExternalSubject == subject, cancellationToken);

        if (user is not null)
        {
            if (!user.IsActive)
            {
                return ExternalSignIn.Refused("The account is disabled in MatFile.");
            }

            await ApplyIdentityRoleAsync(user, identity.Role, false, cancellationToken);
            return new ExternalSignIn(user.Id);
        }

        var email = Normalize(identity.Email);
        var preferredName = Normalize(identity.PreferredUserName);

        var matches = email is null
            ? []
            : await _db.User.Where(u => u.Email != null && u.Email.ToLower() == email).ToListAsync(cancellationToken);

        if (matches.Count == 0 && preferredName is not null)
        {
            matches = await _db.User.Where(u => u.Username.ToLower() == preferredName).ToListAsync(cancellationToken);
        }

        if (matches.Count > 0)
        {
            var active = matches.Where(u => u.IsActive).ToList();

            if (active.Count == 0)
            {
                return ExternalSignIn.Refused("The matching account is disabled in MatFile.");
            }

            if (active.Count > 1)
            {
                _logger.LogWarning("MatIdentity-Kennung {Subject} passt zu {Count} Konten - keine Anmeldung", subject, active.Count);
                return ExternalSignIn.Refused("Several accounts match this person.");
            }

            var match = active[0];
            if (match.ExternalSubject is not null)
            {
                _logger.LogWarning("MatIdentity-Kennung {Subject} passt zu Konto {UserId}, das schon mit einer anderen Kennung verknüpft ist", subject, match.Id);
                return ExternalSignIn.Refused("The matching account is linked with another person.");
            }

            if (!string.IsNullOrEmpty(match.PasswordHash))
            {
                // Nicht über die Adresse verknüpfen: die Person beweist das Konto erst mit seinem Kennwort.
                _logger.LogInformation("MatIdentity-Kennung {Subject} passt zu Konto {UserId}; die Verknüpfung wartet auf dessen Kennwort", subject, match.Id);
                return new ExternalSignIn(null, match.Id);
            }

            // Ein Konto ohne Kennwort kann sich nicht beweisen: ein eigenes Konto daneben, die Adresse bleibt beim alten.
        }

        var emailTaken = email is not null && await _db.User.AnyAsync(u => u.Email != null && u.Email.ToLower() == email, cancellationToken);
        var username = await UniqueUsernameAsync(identity.PreferredUserName ?? email?.Split('@')[0] ?? identity.Name ?? "user", cancellationToken);
        var displayName = FirstNonEmpty(identity.Name, identity.PreferredUserName, username)!;

        user = new User
        {
            Username = username,
            DisplayName = displayName.Length > 128 ? displayName[..128] : displayName,
            Email = emailTaken || email is null ? null : identity.Email!.Trim(),
            // Kein Kennwort: die Anmeldung geht über MatIdentity (ein Administrator kann eines vergeben).
            PasswordHash = string.Empty,
            IsActive = true,
            ExternalSubject = subject
        };

        _db.User.Add(user);
        await _db.SaveChangesAsync(cancellationToken);
        await ApplyIdentityRoleAsync(user, identity.Role, true, cancellationToken);

        _db.ActivityLog.Add(new ActivityLog
        {
            UserId = user.Id,
            ActionType = "UserCreatedMatIdentity",
            DetailsJson = JsonSerializer.Serialize(new { user.Username }),
            CreateUserId = user.Id,
            UpdateUserId = user.Id
        });
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Konto '{Username}' für MatIdentity-Kennung {Subject} angelegt", user.Username, subject);
        return new ExternalSignIn(user.Id);
    }

    /// <summary>
    /// Verknüpft ein Konto mit MatIdentity, nachdem sich genau dieses Konto mit seinem Kennwort angemeldet hat (die wartende Verknüpfung von
    /// <see cref="SignInExternalAsync"/>): setzt die Kennung, folgt dem Claim der Rolle wie bei einer Anmeldung über MatIdentity und schreibt
    /// "LinkedMatIdentity" ins Protokoll. Abgelehnt, wenn das Konto fehlt, deaktiviert oder inzwischen verknüpft ist oder ein anderes die Kennung hat.
    /// </summary>
    public async Task<SaveResult> LinkExternalAsync(long userId, ExternalIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (string.IsNullOrWhiteSpace(identity.Subject))
        {
            return new SaveResult(false, "MatIdentity sent no subject (sub).");
        }

        var subject = identity.Subject.Trim();
        var user = await _db.User.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return new SaveResult(false, "The account is disabled in MatFile.");
        }

        if (user.ExternalSubject is not null || await _db.User.AnyAsync(u => u.ExternalSubject == subject, cancellationToken))
        {
            _logger.LogWarning("Konto {UserId} nicht mit MatIdentity-Kennung {Subject} verknüpft: eines von beiden ist schon verknüpft", userId, subject);
            return new SaveResult(false, "The account is already linked with MatIdentity.");
        }

        user.ExternalSubject = subject;
        await _db.SaveChangesAsync(cancellationToken);
        await ApplyIdentityRoleAsync(user, identity.Role, false, cancellationToken);

        _db.ActivityLog.Add(new ActivityLog
        {
            UserId = user.Id,
            ActionType = "LinkedMatIdentity",
            DetailsJson = JsonSerializer.Serialize(new { message = "linked with MatIdentity after the password of the account" }),
            CreateUserId = user.Id,
            UpdateUserId = user.Id
        });
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Konto {UserId} nach seinem Kennwort mit MatIdentity-Kennung {Subject} verknüpft", user.Id, subject);
        return new SaveResult(true, null, user.Id);
    }

    /// <summary>
    /// Der Claim <c>role</c> von MatIdentity: <c>admin</c> gibt die Systemrolle Administrator, <c>user</c> nimmt sie (nie dem letzten aktiven
    /// Administrator) und gibt die Systemrolle User. Andere Rollen des Kontos bleiben. Ohne Claim bestimmt MatFile selbst; ein neues Konto bekommt User.
    /// </summary>
    private async Task ApplyIdentityRoleAsync(User user, string? claim, bool created, CancellationToken cancellationToken)
    {
        var wanted = claim?.Trim().ToLowerInvariant() switch
        {
            IdentityAdminRole => IdentityAdminRole,
            IdentityUserRole => IdentityUserRole,
            _ => created ? IdentityUserRole : null
        };

        if (wanted is null)
        {
            return;
        }

        var admin = await _db.Role.FirstAsync(r => r.Name == DbSeeder.AdministratorRole, cancellationToken);
        var plain = await _db.Role.FirstAsync(r => r.Name == DbSeeder.UserRole, cancellationToken);
        var current = await _db.UserRole.Where(ur => ur.UserId == user.Id).ToListAsync(cancellationToken);

        if (wanted == IdentityAdminRole)
        {
            if (current.All(ur => ur.RoleId != admin.Id))
            {
                _db.UserRole.Add(new UserRole { UserId = user.Id, RoleId = admin.Id });
            }
        }
        else
        {
            var adminEntry = current.FirstOrDefault(ur => ur.RoleId == admin.Id);
            if (adminEntry is not null)
            {
                if (await IsLastAdministratorAsync(user.Id))
                {
                    _logger.LogWarning("MatIdentity verlangt die Rolle user für Konto {UserId}, den letzten Administrator - die Rolle bleibt", user.Id);
                }
                else
                {
                    _db.UserRole.Remove(adminEntry);
                }
            }

            if (current.All(ur => ur.RoleId != plain.Id))
            {
                _db.UserRole.Add(new UserRole { UserId = user.Id, RoleId = plain.Id });
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Ein freier Benutzername aus <paramref name="wish"/>: höchstens 64 Zeichen, bei Bedarf mit -2, -3 ...</summary>
    private async Task<string> UniqueUsernameAsync(string wish, CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(wish) ? "user" : wish.Trim();
        name = name.Length > 56 ? name[..56] : name;

        var candidate = name;
        for (var number = 2; await _db.User.AnyAsync(u => u.Username.ToLower() == candidate.ToLower(), cancellationToken); number++)
        {
            candidate = $"{name}-{number}";
        }

        return candidate;
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

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
