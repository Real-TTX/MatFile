namespace MatFile.Web.Storage;

/// <summary>
/// Pfadhilfen für Gegenstellen. Der Basispfad der Freigabe ist die Wurzel,
/// darüber hinaus kommt niemand - dieselbe Regel wie beim lokalen <see cref="PathGuard"/>.
/// </summary>
public static class RemotePath
{
    /// <summary>Basispfad in einheitlicher Form: mit führendem Schrägstrich, ohne abschließenden.</summary>
    public static string NormalizeBase(string? basePath)
    {
        var value = (basePath ?? string.Empty).Replace('\\', '/').Trim();

        while (value.EndsWith('/'))
        {
            value = value[..^1];
        }

        if (value.Length == 0)
        {
            return string.Empty;
        }

        return value.StartsWith('/') ? value : "/" + value;
    }

    /// <summary>
    /// Setzt Basispfad und relativen Pfad zusammen. Liefert false, sobald der
    /// relative Pfad ausbrechen würde.
    /// </summary>
    public static bool TryResolve(string? basePath, string? relativePath, out string fullPath)
    {
        fullPath = string.Empty;

        var normalized = PathGuard.Normalize(relativePath);

        if (normalized.Split('/').Any(segment => segment is ".." or "."))
        {
            return false;
        }

        var root = NormalizeBase(basePath);
        fullPath = normalized.Length == 0
            ? (root.Length == 0 ? "/" : root)
            : $"{root}/{normalized}";

        return true;
    }
}
