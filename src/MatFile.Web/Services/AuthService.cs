using System.Security.Claims;
using MatFile.Web.Data;
using MatFile.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatFile.Web.Services;

public record LoginResult(bool Success, string? Error = null, bool MustChangePassword = false);

public record PasswordChangeResult(bool Success, string? Error = null);

/// <summary>
/// Anmeldung, Abmeldung, Sitzungsprüfung und Kennwortänderung.
/// Sitzungen liegen in der Tabelle UserSession und überstehen einen Neustart des Containers.
/// </summary>
public sealed class AuthService
{
    private readonly MatFileDbContext _db;
    private readonly PasswordHasher _hasher;
    private readonly MatFileConfigStore _config;
    private readonly IStringLocalizer<SharedResource> _text;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        MatFileDbContext db,
        PasswordHasher hasher,
        MatFileConfigStore config,
        IStringLocalizer<SharedResource> text,
        ILogger<AuthService> logger)
    {
        _db = db;
        _hasher = hasher;
        _config = config;
        _text = text;
        _logger = logger;
    }

    public async Task<LoginResult> SignInAsync(HttpContext http, string username, string password)
    {
        var settings = _config.Settings;
        var now = DateTime.UtcNow;

        var user = await _db.User.FirstOrDefaultAsync(u => u.Username == username);
        if (user is null || !user.IsActive)
        {
            // Gleiche Meldung für alle Fälle, damit keine Benutzernamen ausgelesen werden können.
            _logger.LogWarning("Fehlgeschlagene Anmeldung für '{Username}' von {Ip}", username, http.Connection.RemoteIpAddress);
            return new LoginResult(false, _text["Login.Invalid"]);
        }

        if (user.LockoutUntil is { } lockout && lockout > now)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling((lockout - now).TotalMinutes));
            return new LoginResult(false, _text["Login.Locked", minutes]);
        }

        if (!_hasher.Verify(password, user.PasswordHash))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= settings.LoginMaxFailedAttempts)
            {
                user.LockoutUntil = now.AddMinutes(settings.LoginLockoutMinutes);
                user.FailedLoginCount = 0;
                _logger.LogWarning("Konto '{Username}' nach zu vielen Fehlversuchen gesperrt", username);
            }

            await _db.SaveChangesAsync();
            _logger.LogWarning("Fehlgeschlagene Anmeldung für '{Username}' von {Ip}", username, http.Connection.RemoteIpAddress);
            return new LoginResult(false, _text["Login.Invalid"]);
        }

        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        user.LastLoginDate = now;

        var session = new UserSession
        {
            Token = Guid.NewGuid(),
            UserId = user.Id,
            ExpiresDate = now.AddDays(settings.SessionLifetimeDays),
            LastActivityDate = now,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Truncate(http.Request.Headers.UserAgent.ToString(), 512),
            CreateUserId = user.Id,
            UpdateUserId = user.Id
        };

        _db.UserSession.Add(session);
        await _db.SaveChangesAsync();

        var principal = await BuildPrincipalAsync(user, session.Token);
        await http.SignInAsync(MatFileClaims.AuthenticationScheme, principal, new AuthenticationProperties
        {
            IsPersistent = true,
            IssuedUtc = now,
            ExpiresUtc = session.ExpiresDate
        });

        await LogAsync(user.Id, "Login", http);
        _logger.LogInformation("Benutzer '{Username}' angemeldet", user.Username);

        // Gespeicherte Sprachwahl des Benutzers übernehmen.
        var language = await _db.UserSetting
            .Where(s => s.UserId == user.Id && s.SettingKey == MatFileCultures.UserSettingKey)
            .Select(s => s.SettingValue)
            .FirstOrDefaultAsync();

        if (MatFileCultures.IsSupported(language))
        {
            MatFileCultures.SetCookie(http.Response, language!);
        }

        return new LoginResult(true, MustChangePassword: user.MustChangePassword);
    }

    public async Task SignOutAsync(HttpContext http)
    {
        var token = http.User.GetSessionToken();
        if (token is { } value)
        {
            var session = await _db.UserSession.FirstOrDefaultAsync(s => s.Token == value);
            if (session is not null)
            {
                session.IsRevoked = true;
                await _db.SaveChangesAsync();
                await LogAsync(session.UserId, "Logout", http);
            }
        }

        await http.SignOutAsync(MatFileClaims.AuthenticationScheme);
    }

    public async Task<PasswordChangeResult> ChangePasswordAsync(HttpContext http, string currentPassword, string newPassword)
    {
        if (http.User.GetUserId() is not { } userId)
        {
            return new PasswordChangeResult(false, _text["ChangePassword.NotSignedIn"]);
        }

        var user = await _db.User.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null)
        {
            return new PasswordChangeResult(false, _text["ChangePassword.UserNotFound"]);
        }

        if (!_hasher.Verify(currentPassword, user.PasswordHash))
        {
            return new PasswordChangeResult(false, _text["ChangePassword.CurrentWrong"]);
        }

        if (newPassword.Length < _config.Settings.MinPasswordLength)
        {
            return new PasswordChangeResult(false, _text["ChangePassword.TooShort", _config.Settings.MinPasswordLength]);
        }

        if (_hasher.Verify(newPassword, user.PasswordHash))
        {
            return new PasswordChangeResult(false, _text["ChangePassword.SameAsOld"]);
        }

        user.PasswordHash = _hasher.Hash(newPassword);
        user.MustChangePassword = false;

        // Andere Sitzungen beenden, die aktuelle bleibt bestehen.
        var sessionToken = http.User.GetSessionToken();
        var currentToken = sessionToken ?? Guid.Empty;
        var others = await _db.UserSession
            .Where(s => s.UserId == user.Id && !s.IsRevoked && s.Token != currentToken)
            .ToListAsync();
        others.ForEach(s => s.IsRevoked = true);

        await _db.SaveChangesAsync();
        await LogAsync(user.Id, "PasswordChanged", http);

        // Claims neu ausstellen, damit der Hinweis auf den Kennwortwechsel verschwindet.
        if (sessionToken is { } token)
        {
            var principal = await BuildPrincipalAsync(user, token);
            await http.SignInAsync(MatFileClaims.AuthenticationScheme, principal, new AuthenticationProperties
            {
                IsPersistent = true
            });
        }

        return new PasswordChangeResult(true);
    }

    public async Task<ClaimsPrincipal> BuildPrincipalAsync(User user, Guid sessionToken)
    {
        var roles = await _db.UserRole
            .Where(ur => ur.UserId == user.Id)
            .Select(ur => ur.Role!)
            .ToListAsync();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.GivenName, user.DisplayName),
            new(MatFileClaims.UserIdClaim, user.Id.ToString()),
            new(MatFileClaims.SessionTokenClaim, sessionToken.ToString())
        };

        if (user.MustChangePassword)
        {
            claims.Add(new Claim(MatFileClaims.MustChangePasswordClaim, "true"));
        }

        var permissions = Permission.None;
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role.Name));
            permissions |= role.Permissions;
        }

        foreach (var permission in MatFilePolicies.SinglePermissions.Where(p => permissions.HasFlag(p)))
        {
            claims.Add(new Claim(MatFileClaims.PermissionClaim, permission.ToString()));
        }

        var identity = new ClaimsIdentity(claims, MatFileClaims.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Prüft bei jeder Anfrage, ob die Sitzung in der Datenbank noch gültig ist,
    /// und verlängert sie gleitend.
    /// </summary>
    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var services = context.HttpContext.RequestServices;
        var db = services.GetRequiredService<MatFileDbContext>();
        var config = services.GetRequiredService<MatFileConfigStore>();

        if (context.Principal.GetSessionToken() is not { } token)
        {
            await Reject(context);
            return;
        }

        var session = await db.UserSession
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.Token == token);

        var now = DateTime.UtcNow;
        if (session is null || session.IsRevoked || session.ExpiresDate <= now
            || session.User is null || !session.User.IsActive)
        {
            await Reject(context);
            return;
        }

        var lifetime = TimeSpan.FromDays(config.Settings.SessionLifetimeDays);
        var changed = false;

        // Schreiblast klein halten: Aktivität höchstens einmal pro Minute festhalten.
        if (now - session.LastActivityDate > TimeSpan.FromMinutes(1))
        {
            session.LastActivityDate = now;
            changed = true;
        }

        // Gleitende Verlängerung, sobald die halbe Lebensdauer erreicht ist.
        if (session.ExpiresDate - now < lifetime / 2)
        {
            session.ExpiresDate = now.Add(lifetime);
            context.ShouldRenew = true;
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync();
        }
    }

    private static async Task Reject(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(MatFileClaims.AuthenticationScheme);
    }

    private async Task LogAsync(long? userId, string actionType, HttpContext http)
    {
        _db.ActivityLog.Add(new ActivityLog
        {
            UserId = userId,
            ActionType = actionType,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            CreateUserId = userId,
            UpdateUserId = userId
        });

        await _db.SaveChangesAsync();
    }

    private static string? Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];
}
