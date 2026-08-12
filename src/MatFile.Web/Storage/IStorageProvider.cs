using MatFile.Web.Data;

namespace MatFile.Web.Storage;

/// <summary>
/// Einheitlicher Zugriff auf einen Speicherort. Lokale Pfade und später SMB, NFS,
/// SFTP, FTP und WebDAV arbeiten hinter derselben Schnittstelle.
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

    // ---------------------------------------------------------------- Schreibend

    /// <summary>Öffnet eine Datei zum Schreiben. Fehlende Ordner werden angelegt.</summary>
    Task<Stream> OpenWriteAsync(string relativePath, bool overwrite, CancellationToken cancellationToken = default);

    Task<StorageEntry> CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>Benennt einen Eintrag im selben Ordner um.</summary>
    Task<StorageEntry> RenameAsync(string relativePath, string newName, CancellationToken cancellationToken = default);

    /// <summary>Verschiebt innerhalb desselben Speicherorts.</summary>
    Task MoveAsync(string sourcePath, string targetPath, bool overwrite, CancellationToken cancellationToken = default);

    Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default);
}
