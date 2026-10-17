using System.Text.Json;
using MatFile.Web.Services;
using MatPlatform;

namespace MatFile.Web.Platform;

/// <summary>
/// MatFile als App des Plattformvertrags (MatDesign docs/PLATFORM.md), auf dem gemeinsamen Paket MatPlatform (Ordner <c>MatPlatform/</c>, eine Kopie):
/// wie Mattiverse MatFile zeigt (<see cref="Describe"/>: Kacheln, Einstellungen, Knöpfe), welche Werte dahinter stehen und was die Knöpfe tun.
/// Die Werte kommen aus <see cref="SystemStatusService"/> (kein DbContext hier: das ist die Webschicht); die Einstellungen sind ein Teil des
/// Systemdialogs (/data/config/matfile.json), in denselben Grenzen. Texte deutsch, englisch mit <c>Accept-Language: en</c>.
/// </summary>
public sealed class MatFilePlatform(SystemStatusService system, ILogger<MatFilePlatform> logger)
    : IMatPlatformState, IMatPlatformConfiguration, IMatPlatformActions, IMatPlatformHealth
{
    /// <summary>Die Id von MatFile in der Plattform (MatDesign AppRegistry.cs) und die Vorgabe der Client-Id in MatIdentity.</summary>
    public const string AppId = "matfile";

    /// <summary>Die Akzentfarbe aus MatDesign AppRegistry.cs.</summary>
    public const string Accent = "#3472d8";

    /// <summary>Die MatApiDefinition von MatFile (<c>GET /api</c>); <c>auth</c> und <c>endpoints</c> füllt MatPlatform.</summary>
    public static void Describe(MatPlatformBuilder o)
    {
        var T = o.T;

        o.App(
            AppId,
            "MatFile",
            T("Datei-Explorer für das NAS im Browser", "A file explorer for your NAS in the browser"),
            AppInfo.CurrentVersion,
            "/icons/matfile.svg",
            Accent);

        o.Page("overview", T("Übersicht", "Overview"), "dashboard",
            MatSection.Tiles("roots", "rootsOffline", "storageUsed", "storageFree", "runningJobs", "failedJobs24h", "users", "lastActivity"),
            MatSection.Actions("open", "activity", "checkStorage", "cancelJobs"));
        o.Page("settings", T("Einstellungen", "Settings"), "settings",
            MatSection.Form(T("Anmeldung", "Sign-in"), "sessionLifetimeDays", "loginMaxFailedAttempts", "loginLockoutMinutes", "minPasswordLength", "requireSecureCookie"),
            MatSection.Form(T("Dateien", "Files"), "maxUploadMegabytes"),
            MatSection.Actions("openSettings"));

        o.Tile("roots", T("Speicherorte", "Storage locations"), icon: "hard-drive");
        o.Tile(new MatStateItem("rootsOffline", T("Nicht erreichbar", "Not reachable"), "number") { Icon = "alert", WarnAbove = 0 });
        o.Tile(new MatStateItem("storageUsed", T("Belegter Speicher", "Storage used"), "meter")
        {
            Icon = "database",
            Unit = "bytes",
            Max = JsonSerializer.SerializeToElement("storageTotal"),
        });
        o.Tile("storageFree", T("Freier Speicher", "Storage free"), icon: "box", unit: "bytes");
        o.Tile("runningJobs", T("Laufende Aufträge", "Running jobs"), icon: "progress-play");
        o.Tile(new MatStateItem("failedJobs24h", T("Fehlgeschlagen (24 h)", "Failed (24 h)"), "number") { Icon = "x-circle", ErrorAbove = 0 });
        o.Tile("users", T("Aktive Benutzer", "Active users"), icon: "users");
        o.Tile("lastActivity", T("Letzte Aktivität", "Last activity"), "time", icon: "activity");

        o.Field(new MatField("sessionLifetimeDays", T("Sitzungsdauer (Tage)", "Session lifetime (days)"), "number")
        {
            Required = true,
            Min = 1,
            Max = 365,
            Hint = T("So lange bleibt eine Anmeldung gültig; jede Nutzung verlängert sie.", "How long a sign-in stays valid; every use extends it."),
        });
        o.Field(new MatField("loginMaxFailedAttempts", T("Fehlversuche bis zur Sperre", "Failed attempts before lock-out"), "number")
        {
            Required = true,
            Min = 1,
            Max = 50,
        });
        o.Field(new MatField("loginLockoutMinutes", T("Dauer der Sperre (Minuten)", "Lock-out duration (minutes)"), "number")
        {
            Required = true,
            Min = 1,
            Max = 1440,
        });
        o.Field(new MatField("minPasswordLength", T("Mindestlänge des Kennworts", "Minimum password length"), "number")
        {
            Required = true,
            Min = 4,
            Max = 128,
        });
        o.Field(new MatField("requireSecureCookie", T("Anmelde-Cookie nur über HTTPS", "Sign-in cookie over HTTPS only"), "bool")
        {
            Hint = T("Einschalten, wenn MatFile nur über HTTPS erreichbar ist.", "Turn on when MatFile is reached over HTTPS only."),
            Restart = true,
        });
        o.Field(new MatField("maxUploadMegabytes", T("Höchstgröße eines Uploads (MB)", "Maximum upload size (MB)"), "number")
        {
            Required = true,
            Min = 0,
            Max = 1_048_576,
            Hint = T("0 heißt: keine Grenze.", "0 means no limit."),
        });

        o.LinkAction("open", T("MatFile öffnen", "Open MatFile"), "external", "/");
        o.LinkAction("activity", T("Aktivitätsprotokoll", "Activity log"), "activity", "/?admin=activity");
        o.PostAction("checkStorage", T("Speicherorte prüfen", "Check storage locations"), "refresh");
        o.PostAction("cancelJobs", T("Alle Aufträge abbrechen", "Cancel all jobs"), "stop",
            T("Alle wartenden und laufenden Aufträge (Kopieren, Verschieben, Löschen, Packen) aller Benutzer abbrechen?",
                "Cancel every waiting and running job (copy, move, delete, zip) of every user?"),
            danger: true);
        o.LinkAction("openSettings", T("Alle Einstellungen", "All settings"), "settings", "/?admin=system");

        o.Role(UserService.IdentityAdminRole, T("Administrator", "Administrator"));
        o.Role(UserService.IdentityUserRole, T("Benutzer", "User"));
    }

    public async Task<IReadOnlyList<MatHealthCheck>> CheckHealthAsync(MatPlatformCall call, CancellationToken ct)
    {
        var health = await system.GetHealthAsync(ct);
        var checks = new List<MatHealthCheck>
        {
            health.DatabaseReady
                ? new MatHealthCheck("database", "ok")
                : new MatHealthCheck("database", "down", call.T("Die Datenbank ist nicht erreichbar.", "The database cannot be reached.")),
            health.DataWritable
                ? new MatHealthCheck("data", "ok")
                : new MatHealthCheck("data", "down", call.T("Das Datenverzeichnis ist nicht beschreibbar.", "The data directory is not writable.")),
        };

        // Nur die letzte Prüfung (/api/state, "Speicherorte prüfen"): der Zustand fragt keine Gegenstelle.
        if (health.LastSurvey is { } survey)
        {
            checks.Add(survey.Offline > 0
                ? new MatHealthCheck("storage", "degraded", call.T(
                    $"{survey.Offline} von {survey.Checked} Speicherorten nicht erreichbar.",
                    $"{survey.Offline} of {survey.Checked} storage locations not reachable."))
                : new MatHealthCheck("storage", "ok"));
        }

        return checks;
    }

    public async Task<IReadOnlyDictionary<string, MatStateValue>> GetStateAsync(MatPlatformCall call, CancellationToken ct)
    {
        var status = await system.GetStatusAsync(ct);
        var storage = await system.GetStorageAsync(false, ct);

        return new Dictionary<string, MatStateValue>
        {
            ["roots"] = new(status.Roots),
            ["rootsOffline"] = new(storage.Offline, storage.Offline > 0 ? "warn" : "ok"),
            ["storageUsed"] = new(storage.Used),
            ["storageTotal"] = new(storage.Total),
            ["storageFree"] = new(storage.Free),
            ["runningJobs"] = new(status.RunningJobs),
            ["failedJobs24h"] = new(status.FailedJobs24Hours, status.FailedJobs24Hours > 0 ? "error" : "ok"),
            ["users"] = new(status.ActiveUsers),
            ["lastActivity"] = new(status.LastActivity),
        };
    }

    public Task<IReadOnlyDictionary<string, object?>> GetConfigurationAsync(MatPlatformCall call, CancellationToken ct)
    {
        var settings = system.GetSettings();
        IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?>
        {
            ["sessionLifetimeDays"] = settings.SessionLifetimeDays,
            ["loginMaxFailedAttempts"] = settings.LoginMaxFailedAttempts,
            ["loginLockoutMinutes"] = settings.LoginLockoutMinutes,
            ["minPasswordLength"] = settings.MinPasswordLength,
            ["requireSecureCookie"] = settings.RequireSecureCookie,
            ["maxUploadMegabytes"] = settings.MaxUploadMegabytes,
        };
        return Task.FromResult(values);
    }

    /// <summary>
    /// Speichert die geänderten Einstellungen. MatPlatform hat sie schon gegen die Beschreibung geprüft (Typ, Pflicht, Grenzen);
    /// hier fehlt nur noch: ganze Zahlen. Nichts wird gespeichert, wenn ein Wert nicht passt.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ConfigureAsync(IReadOnlyDictionary<string, JsonElement> values, MatPlatformCall call, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();

        int? Whole(string key)
        {
            if (!values.TryGetValue(key, out var value))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }

            errors[key] = call.T("Bitte eine ganze Zahl angeben.", "Send a whole number.");
            return null;
        }

        var change = new SystemSettingsChange(
            Whole("sessionLifetimeDays"),
            Whole("loginMaxFailedAttempts"),
            Whole("loginLockoutMinutes"),
            Whole("minPasswordLength"),
            Whole("maxUploadMegabytes"),
            values.TryGetValue("requireSecureCookie", out var secure) && secure.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? secure.ValueKind == JsonValueKind.True
                : null);

        if (errors.Count > 0)
        {
            return errors;
        }

        await system.ChangeSettingsAsync(change, call.Caller, await system.FindUserBySubjectAsync(call.Caller, ct), ct);
        return errors;
    }

    public async Task<MatActionResult?> RunActionAsync(string id, MatPlatformCall call, CancellationToken ct)
    {
        switch (id)
        {
            case "checkStorage":
                var survey = await system.GetStorageAsync(true, ct);
                logger.LogInformation("Speicherorte über die Plattform-API geprüft von {Caller}: {Offline} von {Checked} nicht erreichbar",
                    call.Caller, survey.Offline, survey.Checked);
                return survey.Checked == 0
                    ? new MatActionResult(false, call.T("Es gibt noch keinen Speicherort.", "There is no storage location yet."))
                    : new MatActionResult(true, call.T(
                        $"{survey.Checked - survey.Offline} von {survey.Checked} Speicherorten erreichbar.",
                        $"{survey.Checked - survey.Offline} of {survey.Checked} storage locations reachable."));

            case "cancelJobs":
                var cancelled = await system.CancelAllJobsAsync(call.Caller, await system.FindUserBySubjectAsync(call.Caller, ct), ct);
                logger.LogInformation("Aufträge über die Plattform-API abgebrochen von {Caller}: {Count}", call.Caller, cancelled);
                return cancelled == 0
                    ? new MatActionResult(false, call.T("Es läuft gerade kein Auftrag.", "No job is running."))
                    : new MatActionResult(true, call.T($"{cancelled} Auftrag/Aufträge abgebrochen.", $"{cancelled} job(s) cancelled."));

            default:
                return null;
        }
    }
}
