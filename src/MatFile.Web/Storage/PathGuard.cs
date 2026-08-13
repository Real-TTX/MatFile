namespace MatFile.Web.Storage;

/// <summary>
/// Zentrale Pfadprüfung: jeder Pfad wird kanonisiert und muss innerhalb seines Roots liegen.
/// Ohne Ausnahme für alle Provider - hier entscheidet sich, ob ein Ausbruch möglich ist.
/// </summary>
public static class PathGuard
{
    private static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Bringt einen relativen Pfad in die interne Form: Schrägstriche, ohne führende/abschließende Trenner.</summary>
    public static string Normalize(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        var value = relativePath.Replace('\\', '/').Trim();

        while (value.StartsWith('/'))
        {
            value = value[1..];
        }

        while (value.EndsWith('/'))
        {
            value = value[..^1];
        }

        return value;
    }

    /// <summary>Elternpfad in interner Form, oder null auf oberster Ebene.</summary>
    public static string? GetParent(string? relativePath)
    {
        var normalized = Normalize(relativePath);
        if (normalized.Length == 0)
        {
            return null;
        }

        var index = normalized.LastIndexOf('/');
        return index < 0 ? string.Empty : normalized[..index];
    }

    public static string Combine(string? relativePath, string name)
    {
        var normalized = Normalize(relativePath);
        return normalized.Length == 0 ? Normalize(name) : $"{normalized}/{Normalize(name)}";
    }

    /// <summary>Ein einzelner Datei- oder Ordnername darf keine Trenner und keine Sonderformen enthalten.</summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            return false;
        }

        if (name.Contains('/') || name.Contains('\\'))
        {
            return false;
        }

        return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    /// <summary>
    /// Löst einen relativen Pfad gegen den Root auf. Liefert false, sobald der Pfad
    /// den Root verlassen würde - egal ob über "..", einen absoluten Pfad oder einen Symlink.
    /// </summary>
    public static bool TryResolve(string basePath, string? relativePath, out string fullPath, bool allowLinksOutside = false)
    {
        fullPath = string.Empty;

        var normalized = Normalize(relativePath);

        if (normalized.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || Path.IsPathRooted(normalized))
        {
            return false;
        }

        if (normalized.Split('/').Any(segment => segment is ".."))
        {
            return false;
        }

        string root;
        string candidate;

        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(basePath));
            candidate = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!IsInside(root, candidate))
        {
            return false;
        }

        if (!allowLinksOutside && !IsLinkTargetInside(root, candidate))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    public static bool IsInside(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);

        if (candidate.Equals(normalizedRoot, Comparison))
        {
            return true;
        }

        // Bei einem Wurzelpfad wie "/" oder "C:\" bleibt das Trennzeichen stehen -
        // ein weiteres anzuhängen ergäbe "//" und würde jeden Unterordner ablehnen.
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
                     || normalizedRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, Comparison);
    }

    /// <summary>
    /// Folgt Symlinks bis zum Ziel und prüft, ob dieses noch im Root liegt.
    /// Existiert der Pfad nicht, gilt er als unverdächtig (z. B. beim Anlegen).
    /// </summary>
    public static bool IsLinkTargetInside(string root, string fullPath)
    {
        try
        {
            FileSystemInfo? target = Directory.Exists(fullPath)
                ? new DirectoryInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true)
                : File.Exists(fullPath)
                    ? new FileInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true)
                    : null;

            if (target is null)
            {
                return true;
            }

            return IsInside(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), Path.GetFullPath(target.FullName));
        }
        catch (IOException)
        {
            // Kaputter Link: lieber nicht ausliefern.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
