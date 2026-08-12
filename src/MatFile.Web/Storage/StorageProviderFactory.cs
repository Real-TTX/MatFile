using MatFile.Web.Data;

namespace MatFile.Web.Storage;

/// <summary>
/// Liefert den passenden Provider zu einem Speicherort.
/// SMB, NFS, SFTP, FTP und WebDAV kommen mit den Remote-Mounts (M7) dazu.
/// </summary>
public sealed class StorageProviderFactory
{
    public IStorageProvider Create(StorageRoot root) => root.StorageType switch
    {
        StorageType.Local => new LocalStorageProvider(root),
        _ => throw new StorageException("provider_not_supported",
            $"Storage type {root.StorageType} is not supported yet.")
    };

    public static bool IsSupported(StorageType type) => type is StorageType.Local;
}
