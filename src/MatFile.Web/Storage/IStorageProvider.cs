using MatFile.Web.Data;

namespace MatFile.Web.Storage;

/// <summary>
/// Einheitlicher Zugriff auf einen Speicherort. Lokale Pfade und später SMB, NFS,
/// SFTP, FTP und WebDAV arbeiten hinter derselben Schnittstelle.
/// Schreibende Operationen kommen mit den Dateioperationen (M5) dazu.
/// </summary>
public interface IStorageProvider
{
    StorageType StorageType { get; }

    /// <summary>Ist der Speicherort gerade erreichbar?</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>Inhalt eines Verzeichnisses, Ordner zuerst, danach Dateien - jeweils nach Namen.</summary>
    Task<StorageListing> ListAsync(string? relativePath, CancellationToken cancellationToken = default);

    /// <summary>Einzelner Eintrag oder null, wenn er nicht existiert.</summary>
    Task<StorageEntry?> GetAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>Öffnet eine Datei zum Lesen (Download, Vorschau, Streaming).</summary>
    Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default);
}
