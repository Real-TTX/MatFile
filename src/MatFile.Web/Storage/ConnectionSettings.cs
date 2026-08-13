using System.Text.Json;
using MatFile.Web.Data;

namespace MatFile.Web.Storage;

/// <summary>
/// Verbindungsdaten einer Freigabe. Liegt als JSON in StorageRoot.SettingsJson.
/// Das Kennwort steht nie hier drin, sondern verschlüsselt in SecretEncrypted.
/// </summary>
public sealed class ConnectionSettings
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? Username { get; set; }

    /// <summary>Freigabename bei SMB.</summary>
    public string? Share { get; set; }

    /// <summary>Vollständige Adresse bei WebDAV, z. B. https://host/remote.php/dav/files/user.</summary>
    public string? Url { get; set; }

    /// <summary>FTP im passiven Modus (Standard) oder aktiv.</summary>
    public bool Passive { get; set; } = true;

    /// <summary>Verschlüsselte Verbindung erzwingen (FTPS bzw. HTTPS).</summary>
    public bool UseTls { get; set; }

    /// <summary>Zertifikatsfehler übergehen - nur für Testumgebungen sinnvoll.</summary>
    public bool IgnoreCertificateErrors { get; set; }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static ConnectionSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ConnectionSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<ConnectionSettings>(json, Options) ?? new ConnectionSettings();
        }
        catch (JsonException)
        {
            return new ConnectionSettings();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static int DefaultPort(StorageType type, bool useTls) => type switch
    {
        StorageType.Sftp => 22,
        StorageType.Ftp => 21,
        StorageType.Smb => 445,
        StorageType.WebDav => useTls ? 443 : 80,
        _ => 0
    };

    public int EffectivePort(StorageType type) => Port > 0 ? Port : DefaultPort(type, UseTls);

    /// <summary>Kurze Beschreibung für Listen und die Seitenleiste.</summary>
    public string Describe(StorageType type) => type switch
    {
        StorageType.Local => string.Empty,
        StorageType.WebDav => Url ?? Host,
        StorageType.Smb => $"\\\\{Host}\\{Share}",
        _ => $"{Host}:{EffectivePort(type)}"
    };
}
