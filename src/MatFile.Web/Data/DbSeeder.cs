using MatFile.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace MatFile.Web.Data;

/// <summary>Legt Systemrollen und - bei leerer Datenbank - den ersten Administrator an.</summary>
public static class DbSeeder
{
    // Rollennamen sind Daten und werden nicht übersetzt.
    public const string AdministratorRole = "Administrator";
    public const string UserRole = "User";

    public const string DefaultAdminUsername = "admin";
    public const string DefaultAdminPassword = "admin";

    public static async Task SeedAsync(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatFileDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        await db.Database.MigrateAsync();

        var adminRole = await EnsureRoleAsync(db, AdministratorRole, "Full access to all areas", Permission.All);
        await EnsureRoleAsync(db, UserRole, "Access to the file explorer", Permission.UseExplorer);
        await db.SaveChangesAsync();

        if (await db.User.AnyAsync())
        {
            return;
        }

        var configured = configuration["MATFILE_ADMIN_PASSWORD"];
        var useDefault = string.IsNullOrWhiteSpace(configured);
        var password = useDefault ? DefaultAdminPassword : configured!;

        var settings = scope.ServiceProvider.GetRequiredService<Services.MatFileConfigStore>().Settings;

        var admin = new User
        {
            Username = DefaultAdminUsername,
            DisplayName = "Administrator",
            PasswordHash = hasher.Hash(password),
            IsActive = true,
            MustChangePassword = settings.RequirePasswordChangeOnFirstLogin
        };

        db.User.Add(admin);
        await db.SaveChangesAsync();

        db.UserRole.Add(new UserRole { UserId = admin.Id, RoleId = adminRole.Id });
        await db.SaveChangesAsync();

        if (useDefault)
        {
            logger.LogWarning(
                "Erster Start: Benutzer '{Username}' wurde mit dem Standardkennwort '{Password}' angelegt. "
                + "Kennwortwechsel erzwungen: {Forced}. Für den produktiven Betrieb bitte ändern.",
                DefaultAdminUsername, DefaultAdminPassword, settings.RequirePasswordChangeOnFirstLogin);
        }
        else
        {
            logger.LogInformation(
                "Erster Start: Benutzer '{Username}' mit dem Kennwort aus MATFILE_ADMIN_PASSWORD angelegt.",
                DefaultAdminUsername);
        }
    }

    private static async Task<Role> EnsureRoleAsync(MatFileDbContext db, string name, string description, Permission permissions)
    {
        var role = await db.Role.FirstOrDefaultAsync(r => r.Name == name);
        if (role is not null)
        {
            return role;
        }

        role = new Role
        {
            Name = name,
            Description = description,
            IsSystem = true,
            Permissions = permissions
        };

        db.Role.Add(role);
        return role;
    }
}
