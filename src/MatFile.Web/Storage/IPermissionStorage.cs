namespace MatFile.Web.Storage;

/// <summary>
/// Zugriffsrechte eines Eintrags im POSIX-Stil. <paramref name="Mode"/> ist die
/// gewohnte Oktalzahl (0..777), also drei Bit je Besitzer, Gruppe und Andere.
/// </summary>
public sealed record StoragePermissions(int Mode, string? Owner = null, string? Group = null);

/// <summary>
/// Rechte lesen und setzen. Das können längst nicht alle Protokolle: lokal (Linux),
/// SFTP und FTP mit SITE CHMOD ja, SMB und WebDAV nicht. Wer es kann, bietet
/// zusätzlich diese Schnittstelle an - der Explorer fragt danach.
/// </summary>
public interface IPermissionStorage
{
    /// <summary>Rechte eines Eintrags oder null, wenn die Gegenstelle keine liefert.</summary>
    Task<StoragePermissions?> GetPermissionsAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>Setzt die Rechte. <paramref name="mode"/> ist oktal gedacht, also 0..0777.</summary>
    Task SetPermissionsAsync(string relativePath, int mode, CancellationToken cancellationToken = default);
}

/// <summary>Umrechnung zwischen Oktalzahl und Einzelrechten.</summary>
public static class PermissionMode
{
    /// <summary>Nur die neun rwx-Bits - Sonderbits wie setuid bleiben unangetastet.</summary>
    public const int Mask = 0b111_111_111;

    public static bool IsValid(int mode) => mode >= 0 && mode <= Mask;

    /// <summary>"755" statt 493 - so steht es auch im Dialog.</summary>
    public static string ToText(int mode) => Convert.ToString(mode & Mask, 8).PadLeft(3, '0');

    public static int FromParts(
        bool ownerRead, bool ownerWrite, bool ownerExecute,
        bool groupRead, bool groupWrite, bool groupExecute,
        bool otherRead, bool otherWrite, bool otherExecute)
    {
        var mode = 0;

        if (ownerRead) mode |= 1 << 8;
        if (ownerWrite) mode |= 1 << 7;
        if (ownerExecute) mode |= 1 << 6;
        if (groupRead) mode |= 1 << 5;
        if (groupWrite) mode |= 1 << 4;
        if (groupExecute) mode |= 1 << 3;
        if (otherRead) mode |= 1 << 2;
        if (otherWrite) mode |= 1 << 1;
        if (otherExecute) mode |= 1;

        return mode;
    }
}
