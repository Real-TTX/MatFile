using MatFile.Web.Data;

namespace MatFile.Web.Storage;

/// <summary>Ein Eintrag im Verzeichnis. <paramref name="Path"/> ist relativ zum Root.</summary>
public record StorageEntry(
    string Name,
    string Path,
    bool IsDirectory,
    long Size,
    DateTime ModifiedDate,
    bool IsHidden = false,
    bool IsSymlink = false);

/// <summary>Inhalt eines Verzeichnisses inklusive Navigationsangaben.</summary>
public record StorageListing(
    string Path,
    string? ParentPath,
    IReadOnlyList<StorageEntry> Entries);

/// <summary>Effektive Rechte eines Benutzers auf einem Root.</summary>
public record RootPermissions(
    bool CanRead,
    bool CanWrite,
    bool CanDelete,
    bool CanUpload,
    bool CanDownload,
    bool CanShare)
{
    public static readonly RootPermissions None = new(false, false, false, false, false, false);
    public static readonly RootPermissions Full = new(true, true, true, true, true, true);

    public RootPermissions Merge(RootPermissions other) => new(
        CanRead || other.CanRead,
        CanWrite || other.CanWrite,
        CanDelete || other.CanDelete,
        CanUpload || other.CanUpload,
        CanDownload || other.CanDownload,
        CanShare || other.CanShare);

    public static RootPermissions From(RootAccess access) => new(
        access.CanRead, access.CanWrite, access.CanDelete,
        access.CanUpload, access.CanDownload, access.CanShare);
}

/// <summary>Ein für den Benutzer sichtbarer Root samt seiner effektiven Rechte.</summary>
public record AccessibleRoot(
    long Id,
    string Name,
    StorageType StorageType,
    string? DisplayIcon,
    int SortOrder,
    RootPermissions Permissions);

/// <summary>Fehler beim Zugriff auf einen Speicherort. Der Code eignet sich für die API-Antwort.</summary>
public class StorageException : Exception
{
    public StorageException(string code, string message) : base(message) => Code = code;

    public string Code { get; }

    public static StorageException NotFound(string path)
        => new("not_found", $"Path not found: {path}");

    public static StorageException InvalidPath(string path)
        => new("invalid_path", $"Path is not allowed: {path}");

    public static StorageException RootUnavailable(string basePath)
        => new("root_unavailable", $"Root path is not available: {basePath}");
}
