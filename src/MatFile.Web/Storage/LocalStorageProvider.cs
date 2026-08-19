using MatFile.Web.Data;

namespace MatFile.Web.Storage;

/// <summary>
/// Zugriff auf einen lokalen Pfad im Container - der Normalfall für ein per
/// Bind-Mount eingehängtes NAS-Volume.
/// </summary>
public sealed class LocalStorageProvider : IStorageProvider, IPermissionStorage, IStorageSpace
{
    private readonly string _basePath;
    private readonly string? _protectedPath;

    /// <param name="protectedPath">
    /// Verzeichnis, das nie ausgeliefert wird - MatFiles eigenes /data mit Datenbank und Schlüsseln.
    /// Liegt der Speicherort darüber (etwa auf "/"), bleibt es unsichtbar statt den Pfad zu verbieten.
    /// </param>
    public LocalStorageProvider(StorageRoot root, string? protectedPath = null)
    {
        _basePath = root.BasePath;
        _protectedPath = string.IsNullOrWhiteSpace(protectedPath)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedPath));
    }

    public StorageType StorageType => StorageType.Local;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Directory.Exists(_basePath));

    public Task<StorageListing> ListAsync(string? relativePath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_basePath))
        {
            throw StorageException.RootUnavailable(_basePath);
        }

        var normalized = PathGuard.Normalize(relativePath);
        var fullPath = Resolve(normalized);

        if (!Directory.Exists(fullPath))
        {
            throw StorageException.NotFound(normalized);
        }

        var entries = new List<StorageEntry>();
        var directory = new DirectoryInfo(fullPath);

        foreach (var info in directory.EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var childPath = PathGuard.Combine(normalized, info.Name);

            // Symlinks, die aus dem Root herausführen, werden nicht angezeigt.
            if (!PathGuard.IsLinkTargetInside(_basePath, info.FullName))
            {
                continue;
            }

            if (IsProtected(info.FullName))
            {
                continue;
            }

            entries.Add(ToEntry(info, childPath));
        }

        var sorted = entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult(new StorageListing(normalized, PathGuard.GetParent(normalized), sorted));
    }

    public Task<StorageEntry?> GetAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);
        var fullPath = Resolve(normalized);

        if (Directory.Exists(fullPath))
        {
            return Task.FromResult<StorageEntry?>(ToEntry(new DirectoryInfo(fullPath), normalized));
        }

        if (File.Exists(fullPath))
        {
            return Task.FromResult<StorageEntry?>(ToEntry(new FileInfo(fullPath), normalized));
        }

        return Task.FromResult<StorageEntry?>(null);
    }

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);
        var fullPath = Resolve(normalized);

        if (!File.Exists(fullPath))
        {
            throw StorageException.NotFound(normalized);
        }

        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);

        return Task.FromResult(stream);
    }

    // ---------------------------------------------------------------- Schreibend

    public Task<Stream> OpenWriteAsync(string relativePath, bool overwrite, CancellationToken cancellationToken = default)
    {
        var fullPath = Resolve(relativePath);

        if (!overwrite && File.Exists(fullPath))
        {
            throw StorageException.Conflict(PathGuard.Normalize(relativePath));
        }

        return Task.FromResult(Schreibend(() =>
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return (Stream)new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true);
        }));
    }

    public Task<StorageEntry> CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);
        var fullPath = Resolve(normalized);

        if (Directory.Exists(fullPath) || File.Exists(fullPath))
        {
            throw StorageException.Conflict(normalized);
        }

        var info = Schreibend(() => Directory.CreateDirectory(fullPath));
        return Task.FromResult(ToEntry(info, normalized));
    }

    public Task<StorageEntry> RenameAsync(string relativePath, string newName, CancellationToken cancellationToken = default)
    {
        if (!PathGuard.IsValidName(newName))
        {
            throw StorageException.InvalidName(newName);
        }

        var normalized = PathGuard.Normalize(relativePath);
        var source = Resolve(normalized);
        var targetPath = PathGuard.Combine(PathGuard.GetParent(normalized), newName);
        var target = Resolve(targetPath);

        if (Directory.Exists(target) || File.Exists(target))
        {
            throw StorageException.Conflict(targetPath);
        }

        if (Directory.Exists(source))
        {
            Schreibend(() => Directory.Move(source, target));
            return Task.FromResult(ToEntry(new DirectoryInfo(target), targetPath));
        }

        if (File.Exists(source))
        {
            Schreibend(() => File.Move(source, target));
            return Task.FromResult(ToEntry(new FileInfo(target), targetPath));
        }

        throw StorageException.NotFound(normalized);
    }

    public Task MoveAsync(string sourcePath, string targetPath, bool overwrite, CancellationToken cancellationToken = default)
    {
        var source = Resolve(sourcePath);
        var target = Resolve(targetPath);

        Schreibend(() =>
        {
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        });

        if (Directory.Exists(source))
        {
            if (Directory.Exists(target))
            {
                throw StorageException.Conflict(PathGuard.Normalize(targetPath));
            }

            Schreibend(() => Directory.Move(source, target));
            return Task.CompletedTask;
        }

        if (!File.Exists(source))
        {
            throw StorageException.NotFound(PathGuard.Normalize(sourcePath));
        }

        if (File.Exists(target) && !overwrite)
        {
            throw StorageException.Conflict(PathGuard.Normalize(targetPath));
        }

        Schreibend(() => File.Move(source, target, overwrite));
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);

        if (normalized.Length == 0)
        {
            // Der Root selbst wird nie gelöscht.
            throw StorageException.InvalidPath(normalized);
        }

        var fullPath = Resolve(normalized);

        if (Directory.Exists(fullPath))
        {
            Schreibend(() => Directory.Delete(fullPath, recursive: true));
            return Task.CompletedTask;
        }

        if (File.Exists(fullPath))
        {
            Schreibend(() => File.Delete(fullPath));
            return Task.CompletedTask;
        }

        throw StorageException.NotFound(normalized);
    }

    // ---------------------------------------------------------------- Platz

    public Task<StorageSpace?> GetSpaceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_basePath)) ?? _basePath);

            return Task.FromResult<StorageSpace?>(drive.IsReady
                ? new StorageSpace(drive.TotalSize, drive.AvailableFreeSpace)
                : null);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Ohne Auskunft bleibt die Spalte einfach leer.
            return Task.FromResult<StorageSpace?>(null);
        }
    }

    // ---------------------------------------------------------------- Rechte

    /// <summary>
    /// Unter Linux stehen die rwx-Bits direkt am Eintrag. Unter Windows gibt es sie nicht -
    /// dann meldet der Provider "keine Rechte" und der Dialog blendet den Abschnitt aus.
    /// </summary>
    public Task<StoragePermissions?> GetPermissionsAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);
        var fullPath = Resolve(normalized);

        if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
        {
            throw StorageException.NotFound(normalized);
        }

        if (OperatingSystem.IsWindows())
        {
            return Task.FromResult<StoragePermissions?>(null);
        }

        var mode = (int)File.GetUnixFileMode(fullPath) & PermissionMode.Mask;
        return Task.FromResult<StoragePermissions?>(new StoragePermissions(mode));
    }

    public Task SetPermissionsAsync(string relativePath, int mode, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);
        var fullPath = Resolve(normalized);

        if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
        {
            throw StorageException.NotFound(normalized);
        }

        if (OperatingSystem.IsWindows())
        {
            throw new StorageException("permissions_unsupported", "This storage type has no POSIX permissions.");
        }

        try
        {
            File.SetUnixFileMode(fullPath, (UnixFileMode)(mode & PermissionMode.Mask));
        }
        catch (UnauthorizedAccessException)
        {
            throw StorageException.AccessDenied();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Schreibversuche auf einem nur lesbaren Bind-Mount (":ro") oder ohne Rechte enden
    /// sonst als unbehandelte Ausnahme, also als 500 - obwohl nichts kaputt ist. Hier
    /// werden sie zu einer Antwort, die die Oberfläche erklären kann.
    /// </summary>
    private static T Schreibend<T>(Func<T> aktion)
    {
        try
        {
            return aktion();
        }
        catch (UnauthorizedAccessException)
        {
            throw StorageException.AccessDenied();
        }
        catch (IOException exception) when (IstNurLesbar(exception))
        {
            throw StorageException.ReadOnly();
        }
    }

    private static void Schreibend(Action aktion)
        => Schreibend<object?>(() => { aktion(); return null; });

    /// <summary>EROFS trägt die Nummer 30; die Meldung nennt es zusätzlich im Klartext.</summary>
    private static bool IstNurLesbar(IOException exception)
        => (exception.HResult & 0xFFFF) == 30
           || exception.Message.Contains("Read-only file system", StringComparison.OrdinalIgnoreCase);

    private string Resolve(string? relativePath)
    {
        if (!PathGuard.TryResolve(_basePath, relativePath, out var fullPath))
        {
            throw StorageException.InvalidPath(PathGuard.Normalize(relativePath));
        }

        // Das eigene Datenverzeichnis verhält sich, als gäbe es es nicht.
        if (IsProtected(fullPath))
        {
            throw StorageException.NotFound(PathGuard.Normalize(relativePath));
        }

        return fullPath;
    }

    private bool IsProtected(string fullPath)
        => _protectedPath is not null && PathGuard.IsInside(_protectedPath, fullPath);

    private static StorageEntry ToEntry(FileSystemInfo info, string relativePath)
    {
        var isDirectory = info is DirectoryInfo;
        var size = info is FileInfo file ? file.Length : 0;

        return new StorageEntry(
            info.Name,
            relativePath,
            isDirectory,
            size,
            info.LastWriteTimeUtc,
            info.Name.StartsWith('.'),
            info.LinkTarget is not null);
    }
}
