using MatFile.Web.Data;

namespace MatFile.Web.Storage;

/// <summary>
/// Zugriff auf einen lokalen Pfad im Container - der Normalfall für ein per
/// Bind-Mount eingehängtes NAS-Volume.
/// </summary>
public sealed class LocalStorageProvider : IStorageProvider
{
    private readonly string _basePath;

    public LocalStorageProvider(StorageRoot root)
    {
        _basePath = root.BasePath;
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

        if (!PathGuard.TryResolve(_basePath, normalized, out var fullPath))
        {
            throw StorageException.InvalidPath(normalized);
        }

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

        if (!PathGuard.TryResolve(_basePath, normalized, out var fullPath))
        {
            throw StorageException.InvalidPath(normalized);
        }

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

        if (!PathGuard.TryResolve(_basePath, normalized, out var fullPath))
        {
            throw StorageException.InvalidPath(normalized);
        }

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

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Stream stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, useAsync: true);

        return Task.FromResult(stream);
    }

    public Task<StorageEntry> CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);
        var fullPath = Resolve(normalized);

        if (Directory.Exists(fullPath) || File.Exists(fullPath))
        {
            throw StorageException.Conflict(normalized);
        }

        var info = Directory.CreateDirectory(fullPath);
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
            Directory.Move(source, target);
            return Task.FromResult(ToEntry(new DirectoryInfo(target), targetPath));
        }

        if (File.Exists(source))
        {
            File.Move(source, target);
            return Task.FromResult(ToEntry(new FileInfo(target), targetPath));
        }

        throw StorageException.NotFound(normalized);
    }

    public Task MoveAsync(string sourcePath, string targetPath, bool overwrite, CancellationToken cancellationToken = default)
    {
        var source = Resolve(sourcePath);
        var target = Resolve(targetPath);

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (Directory.Exists(source))
        {
            if (Directory.Exists(target))
            {
                throw StorageException.Conflict(PathGuard.Normalize(targetPath));
            }

            Directory.Move(source, target);
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

        File.Move(source, target, overwrite);
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
            Directory.Delete(fullPath, recursive: true);
            return Task.CompletedTask;
        }

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            return Task.CompletedTask;
        }

        throw StorageException.NotFound(normalized);
    }

    private string Resolve(string? relativePath)
    {
        if (!PathGuard.TryResolve(_basePath, relativePath, out var fullPath))
        {
            throw StorageException.InvalidPath(PathGuard.Normalize(relativePath));
        }

        return fullPath;
    }

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
