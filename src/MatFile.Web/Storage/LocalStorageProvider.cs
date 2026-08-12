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
