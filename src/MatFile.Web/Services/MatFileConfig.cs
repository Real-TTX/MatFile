using System.Text.Json;

namespace MatFile.Web.Services;

/// <summary>Konfiguration aus /data/config/matfile.json. Reine Einstellungen, keine Logikdaten.</summary>
public sealed class MatFileSettings
{
    /// <summary>Lebensdauer einer Anmeldesitzung in Tagen (gleitend).</summary>
    public int SessionLifetimeDays { get; set; } = 14;

    /// <summary>Fehlversuche bis zur Sperre des Kontos.</summary>
    public int LoginMaxFailedAttempts { get; set; } = 5;

    /// <summary>Dauer der Kontosperre in Minuten.</summary>
    public int LoginLockoutMinutes { get; set; } = 15;

    /// <summary>Mindestlänge für Kennwörter.</summary>
    public int MinPasswordLength { get; set; } = 8;

    /// <summary>Cookie nur über HTTPS senden. Bei TLS-Terminierung im NAS-Proxy meist nicht nötig.</summary>
    public bool RequireSecureCookie { get; set; }
}

/// <summary>Lädt und speichert die JSON-Konfiguration.</summary>
public sealed class MatFileConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly MatFilePaths _paths;
    private readonly object _lock = new();

    public MatFileSettings Settings { get; private set; } = new();

    public MatFileConfigStore(MatFilePaths paths)
    {
        _paths = paths;
        Settings = Load();
    }

    public MatFileSettings Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_paths.ConfigFile))
            {
                var defaults = new MatFileSettings();
                File.WriteAllText(_paths.ConfigFile, JsonSerializer.Serialize(defaults, JsonOptions));
                return defaults;
            }

            try
            {
                var json = File.ReadAllText(_paths.ConfigFile);
                return JsonSerializer.Deserialize<MatFileSettings>(json, JsonOptions) ?? new MatFileSettings();
            }
            catch (JsonException)
            {
                // Kaputte Konfiguration darf den Start nicht verhindern.
                return new MatFileSettings();
            }
        }
    }

    public void Save(MatFileSettings settings)
    {
        lock (_lock)
        {
            File.WriteAllText(_paths.ConfigFile, JsonSerializer.Serialize(settings, JsonOptions));
            Settings = settings;
        }
    }
}
