using System.Security.Claims;
using MatFile.Web.Data;

namespace MatFile.Web.Security;

/// <summary>Claim-Typen und Zugriffshelfer für den angemeldeten Benutzer.</summary>
public static class MatFileClaims
{
    public const string AuthenticationScheme = "MatFile";

    public const string UserIdClaim = "matfile:uid";
    public const string SessionTokenClaim = "matfile:sid";
    public const string PermissionClaim = "matfile:perm";
    public const string MustChangePasswordClaim = "matfile:pwchange";

    public static long? GetUserId(this ClaimsPrincipal? principal)
    {
        var value = principal?.FindFirstValue(UserIdClaim);
        return long.TryParse(value, out var id) ? id : null;
    }

    public static Guid? GetSessionToken(this ClaimsPrincipal? principal)
    {
        var value = principal?.FindFirstValue(SessionTokenClaim);
        return Guid.TryParse(value, out var token) ? token : null;
    }

    public static string GetDisplayName(this ClaimsPrincipal? principal)
        => principal?.FindFirstValue(ClaimTypes.GivenName)
           ?? principal?.Identity?.Name
           ?? string.Empty;

    public static bool RequiresPasswordChange(this ClaimsPrincipal? principal)
        => principal?.HasClaim(MustChangePasswordClaim, "true") == true;

    public static bool HasPermission(this ClaimsPrincipal? principal, Permission permission)
        => principal?.HasClaim(PermissionClaim, permission.ToString()) == true;
}

/// <summary>Stellt die Id des angemeldeten Benutzers für die Audit-Felder bereit.</summary>
public interface ICurrentUser
{
    long? UserId { get; }
}

public sealed class HttpCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public HttpCurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    public long? UserId => _accessor.HttpContext?.User.GetUserId();
}
