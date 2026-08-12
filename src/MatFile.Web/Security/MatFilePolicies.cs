using MatFile.Web.Data;
using Microsoft.AspNetCore.Authorization;

namespace MatFile.Web.Security;

/// <summary>
/// Autorisierung: pro Recht aus <see cref="Permission"/> gibt es eine Policy "Perm.&lt;Name&gt;".
/// Verwendung: [Authorize(Policy = MatFilePolicies.ManageUsers)]
/// </summary>
public static class MatFilePolicies
{
    public const string Prefix = "Perm.";

    public const string UseExplorer = Prefix + nameof(Permission.UseExplorer);
    public const string ManageUsers = Prefix + nameof(Permission.ManageUsers);
    public const string ManageRoles = Prefix + nameof(Permission.ManageRoles);
    public const string ManageRoots = Prefix + nameof(Permission.ManageRoots);
    public const string ManageMounts = Prefix + nameof(Permission.ManageMounts);
    public const string ManageSettings = Prefix + nameof(Permission.ManageSettings);
    public const string ViewActivityLog = Prefix + nameof(Permission.ViewActivityLog);

    /// <summary>Alle Einzelrechte (ohne None/All) - Basis für Policies und Claims.</summary>
    public static IEnumerable<Permission> SinglePermissions =>
        Enum.GetValues<Permission>().Where(p => p is not (Permission.None or Permission.All));

    public static AuthorizationBuilder AddMatFilePolicies(this AuthorizationBuilder builder)
    {
        foreach (var permission in SinglePermissions)
        {
            builder.AddPolicy(Prefix + permission, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(MatFileClaims.PermissionClaim, permission.ToString()));
        }

        return builder;
    }
}
