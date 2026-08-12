using MatFile.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace MatFile.Web.Data;

/// <summary>Legt Systemrollen und - bei leerer Datenbank - den ersten Administrator an.</summary>
public static class DbSeeder
{
    // Rollennamen sind Daten und werden nicht übersetzt.
    public const string AdministratorRole = "Administrator";
    public const string UserRole = "User";

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
        var generated = string.IsNullOrWhiteSpace(configured);
        var password = generated ? PasswordHasher.GenerateRandomPassword() : configured!;

        var admin = new User
        {
            Username = "admin",
            DisplayName = "Administrator",
            PasswordHash = hasher.Hash(password),
            IsActive = true,
            MustChangePassword = true
        };

        db.User.Add(admin);
        await db.SaveChangesAsync();

        db.UserRole.Add(new UserRole { UserId = admin.Id, RoleId = adminRole.Id });
        await db.SaveChangesAsync();

        if (generated)
        {
            logger.LogWarning(
                "Erster Start: Benutzer 'admin' wurde angelegt. Startkennwort: {Password} - bitte nach der ersten Anmeldung ändern.",
                password);
        }
        else
        {
            logger.LogInformation("Erster Start: Benutzer 'admin' mit dem Kennwort aus MATFILE_ADMIN_PASSWORD angelegt.");
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
