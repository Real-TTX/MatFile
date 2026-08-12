namespace MatFile.Web.Storage;

public enum PreviewKind
{
    None,
    Image,
    Video,
    Audio,
    Pdf,
    Text
}

/// <summary>
/// Legt fest, was direkt im Browser angezeigt werden darf.
/// Nur diese Inhaltstypen werden inline ausgeliefert - alles andere geht als Download raus.
/// Damit kann kein HTML oder SVG mit Skript auf unserer Domain ausgeführt werden.
/// </summary>
public static class PreviewPolicy
{
    private static readonly Dictionary<string, (PreviewKind Kind, string ContentType)> Types =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = (PreviewKind.Image, "image/jpeg"),
            [".jpeg"] = (PreviewKind.Image, "image/jpeg"),
            [".png"] = (PreviewKind.Image, "image/png"),
            [".gif"] = (PreviewKind.Image, "image/gif"),
            [".webp"] = (PreviewKind.Image, "image/webp"),
            [".avif"] = (PreviewKind.Image, "image/avif"),
            [".bmp"] = (PreviewKind.Image, "image/bmp"),
            [".ico"] = (PreviewKind.Image, "image/x-icon"),

            [".mp4"] = (PreviewKind.Video, "video/mp4"),
            [".m4v"] = (PreviewKind.Video, "video/mp4"),
            [".webm"] = (PreviewKind.Video, "video/webm"),
            [".ogv"] = (PreviewKind.Video, "video/ogg"),
            [".mov"] = (PreviewKind.Video, "video/quicktime"),
            [".mkv"] = (PreviewKind.Video, "video/x-matroska"),

            [".mp3"] = (PreviewKind.Audio, "audio/mpeg"),
            [".m4a"] = (PreviewKind.Audio, "audio/mp4"),
            [".wav"] = (PreviewKind.Audio, "audio/wav"),
            [".ogg"] = (PreviewKind.Audio, "audio/ogg"),
            [".oga"] = (PreviewKind.Audio, "audio/ogg"),
            [".flac"] = (PreviewKind.Audio, "audio/flac"),
            [".aac"] = (PreviewKind.Audio, "audio/aac"),

            [".pdf"] = (PreviewKind.Pdf, "application/pdf")
        };

    /// <summary>Dateien, deren Inhalt als Text angezeigt wird - inklusive Auszeichnungssprachen.</summary>
    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".json", ".xml", ".yml", ".yaml", ".csv", ".tsv",
        ".ini", ".conf", ".cfg", ".env", ".properties", ".sql",
        ".cs", ".js", ".mjs", ".ts", ".css", ".html", ".htm", ".svg",
        ".sh", ".ps1", ".bat", ".py", ".rb", ".go", ".rs", ".java", ".php",
        ".gitignore", ".dockerignore", ".editorconfig"
    };

    /// <summary>Höchstgröße für die Textvorschau.</summary>
    public const int MaxTextBytes = 512 * 1024;

    public static PreviewKind KindOf(string fileName)
    {
        var extension = Path.GetExtension(fileName);

        if (Types.TryGetValue(extension, out var entry))
        {
            return entry.Kind;
        }

        return TextTypes.Contains(extension) || TextTypes.Contains(fileName) ? PreviewKind.Text : PreviewKind.None;
    }

    /// <summary>Inhaltstyp für die Inline-Auslieferung, oder null wenn nicht erlaubt.</summary>
    public static string? InlineContentType(string fileName)
        => Types.TryGetValue(Path.GetExtension(fileName), out var entry) ? entry.ContentType : null;
}
