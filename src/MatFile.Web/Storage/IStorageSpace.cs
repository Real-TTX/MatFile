namespace MatFile.Web.Storage;

/// <summary>Belegung eines Speicherorts in Byte.</summary>
public sealed record StorageSpace(long Total, long Free)
{
    public long Used => Math.Max(0, Total - Free);

    /// <summary>Belegung in Prozent, oder null wenn die Gesamtgröße unbekannt ist.</summary>
    public int? UsedPercent => Total <= 0 ? null : (int)Math.Round(Used * 100d / Total);
}

/// <summary>
/// Wie viel Platz ist da? Das können längst nicht alle Protokolle sagen:
/// lokal, SFTP (statvfs) und SMB ja, FTP und WebDAV nicht.
/// Wer es kann, bietet zusätzlich diese Schnittstelle an.
/// </summary>
public interface IStorageSpace
{
    /// <summary>Belegung oder null, wenn die Gegenstelle keine Auskunft gibt.</summary>
    Task<StorageSpace?> GetSpaceAsync(CancellationToken cancellationToken = default);
}
