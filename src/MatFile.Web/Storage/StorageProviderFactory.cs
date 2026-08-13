using MatFile.Web.Data;
using MatFile.Web.Services;

namespace MatFile.Web.Storage;

/// <summary>
/// Liefert den passenden Provider zu einem Speicherort und entschlüsselt dabei
/// die hinterlegten Zugangsdaten.
/// </summary>
public sealed class StorageProviderFactory
{
    private readonly SecretProtector _protector;
    private readonly IHttpClientFactory _httpClients;

    public StorageProviderFactory(SecretProtector protector, IHttpClientFactory httpClients)
    {
        _protector = protector;
        _httpClients = httpClients;
    }

    public const string WebDavClient = "webdav";

    public IStorageProvider Create(StorageRoot root)
        => Create(root, ConnectionSettings.Parse(root.SettingsJson), _protector.Unprotect(root.SecretEncrypted));

    /// <summary>Erzeugt einen Provider mit ausdrücklich übergebenen Verbindungsdaten - für den Verbindungstest.</summary>
    public IStorageProvider Create(StorageRoot root, ConnectionSettings settings, string? password) => root.StorageType switch
    {
        StorageType.Local => new LocalStorageProvider(root),
        StorageType.Sftp => new SftpStorageProvider(root, settings, password),
        StorageType.Ftp => new FtpStorageProvider(root, settings, password),
        StorageType.WebDav => new WebDavStorageProvider(root, settings, password, _httpClients.CreateClient(WebDavClient)),
        _ => throw new StorageException("provider_not_supported",
            $"Storage type {root.StorageType} is not supported yet.")
    };

    public static bool IsSupported(StorageType type)
        => type is StorageType.Local or StorageType.Sftp or StorageType.Ftp or StorageType.WebDav;

    /// <summary>Speichertypen, die Verbindungsdaten brauchen.</summary>
    public static bool NeedsConnection(StorageType type) => type is not StorageType.Local;
}
