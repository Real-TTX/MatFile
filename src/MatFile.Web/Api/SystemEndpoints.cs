using MatFile.Web.Security;
using MatFile.Web.Services;

namespace MatFile.Web.Api;

/// <summary>
/// Systemeinstellungen aus /data/config/matfile.json - lesbar und änderbar
/// mit dem Recht „Einstellungen verwalten".
/// </summary>
public static class SystemEndpoints
{
    public record SystemRequest(
        int SessionLifetimeDays,
        int LoginMaxFailedAttempts,
        int LoginLockoutMinutes,
        int LoginAttemptsPerMinute,
        int MinPasswordLength,
        bool RequireSecureCookie,
        bool RequirePasswordChangeOnFirstLogin);

    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/system").RequireAuthorization(MatFilePolicies.ManageSettings);

        group.MapGet("/", (MatFileConfigStore config, AppInfo info, MatFilePaths paths) =>
        {
            var settings = config.Settings;

            return Results.Ok(new
            {
                settings.SessionLifetimeDays,
                settings.LoginMaxFailedAttempts,
                settings.LoginLockoutMinutes,
                settings.LoginAttemptsPerMinute,
                settings.MinPasswordLength,
                settings.RequireSecureCookie,
                settings.RequirePasswordChangeOnFirstLogin,

                // Zur Einordnung im Dialog - nicht änderbar.
                info.Version,
                info.Channel,
                info.StartedAt,
                paths.DataDirectory,
                configFile = paths.ConfigFile
            });
        });

        group.MapPut("/", (SystemRequest request, MatFileConfigStore config) =>
        {
            // Frisch von der Platte lesen: Einstellungen, die dieser Dialog nicht kennt, bleiben stehen.
            var settings = config.Load();

            // Werte werden eingegrenzt, damit sich niemand aussperrt.
            settings.SessionLifetimeDays = Math.Clamp(request.SessionLifetimeDays, 1, 365);
            settings.LoginMaxFailedAttempts = Math.Clamp(request.LoginMaxFailedAttempts, 1, 50);
            settings.LoginLockoutMinutes = Math.Clamp(request.LoginLockoutMinutes, 1, 1440);
            settings.LoginAttemptsPerMinute = Math.Clamp(request.LoginAttemptsPerMinute, 3, 1000);
            settings.MinPasswordLength = Math.Clamp(request.MinPasswordLength, 4, 128);
            settings.RequireSecureCookie = request.RequireSecureCookie;
            settings.RequirePasswordChangeOnFirstLogin = request.RequirePasswordChangeOnFirstLogin;

            config.Save(settings);

            return Results.Ok(new
            {
                settings.SessionLifetimeDays,
                settings.LoginMaxFailedAttempts,
                settings.LoginLockoutMinutes,
                settings.LoginAttemptsPerMinute,
                settings.MinPasswordLength,
                settings.RequireSecureCookie,
                settings.RequirePasswordChangeOnFirstLogin
            });
        });

        return app;
    }
}
