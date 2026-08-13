using MatFile.Web.Data;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace MatFile.Web.Storage;

/// <summary>
/// Zugriff auf eine SFTP-Gegenstelle. Für jede Operation wird eine Verbindung
/// aufgebaut und wieder geschlossen - das ist unkompliziert und robust; ein
/// Verbindungspool wäre die nächste Optimierung.
/// </summary>
public sealed class SftpStorageProvider : IStorageProvider
{
    private readonly ConnectionSettings _settings;
    private readonly string _basePath;
    private readonly string? _password;

    public SftpStorageProvider(StorageRoot root, ConnectionSettings settings, string? password)
    {
        _settings = settings;
        _basePath = RemotePath.NormalizeBase(root.BasePath);
        _password = password;
    }

    public StorageType StorageType => StorageType.Sftp;

    private SftpClient Connect()
    {
        var info = new Renci.SshNet.ConnectionInfo(
            _settings.Host,
            _settings.EffectivePort(StorageType.Sftp),
            _settings.Username ?? string.Empty,
            new PasswordAuthenticationMethod(_settings.Username ?? string.Empty, _password ?? string.Empty))
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        var client = new SftpClient(info);

        try
        {
            client.Connect();
        }
        catch (Exception exception) when (exception is SshException or System.Net.Sockets.SocketException)
        {
            client.Dispose();
            throw new StorageException("connection_failed", exception.Message);
        }

        return client;
    }

    private string Resolve(string? relativePath)
    {
        if (!RemotePath.TryResolve(_basePath, relativePath, out var full))
        {
            throw StorageException.InvalidPath(PathGuard.Normalize(relativePath));
        }

        return full;
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = Connect();
            return Task.FromResult(client.Exists(_basePath.Length == 0 ? "/" : _basePath));
        }
        catch (StorageException)
        {
            return Task.FromResult(false);
        }
    }

    public Task<StorageListing> ListAsync(string? relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);
        var remote = Resolve(normalized);

        using var client = Connect();

        if (!client.Exists(remote))
        {
            throw StorageException.NotFound(normalized);
        }

        var entries = new List<StorageEntry>();

        foreach (var file in client.ListDirectory(remote))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (file.Name is "." or "..")
            {
                continue;
            }

            entries.Add(ToEntry(file, PathGuard.Combine(normalized, file.Name)));
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
        var remote = Resolve(normalized);

        using var client = Connect();

        if (!client.Exists(remote))
        {
            return Task.FromResult<StorageEntry?>(null);
        }

        return Task.FromResult<StorageEntry?>(ToEntry(client.Get(remote), normalized));
    }

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);
        var remote = Resolve(normalized);

        var client = Connect();

        if (!client.Exists(remote))
        {
            client.Dispose();
            throw StorageException.NotFound(normalized);
        }

        // Der Strom hält die Verbindung offen und schließt sie beim Dispose mit.
        return Task.FromResult<Stream>(new ClientStream(client, client.OpenRead(remote)));
    }

    public Task<Stream> OpenWriteAsync(string relativePath, bool overwrite, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);
        var remote = Resolve(normalized);

        var client = Connect();

        if (!overwrite && client.Exists(remote))
        {
            client.Dispose();
            throw StorageException.Conflict(normalized);
        }

        EnsureParent(client, normalized);

        return Task.FromResult<Stream>(new ClientStream(client, client.Create(remote)));
    }

    public Task<StorageEntry> CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);
        var remote = Resolve(normalized);

        using var client = Connect();

        if (client.Exists(remote))
        {
            throw StorageException.Conflict(normalized);
        }

        EnsureParent(client, normalized);
        client.CreateDirectory(remote);

        return Task.FromResult(ToEntry(client.Get(remote), normalized));
    }

    public Task<StorageEntry> RenameAsync(string relativePath, string newName, CancellationToken cancellationToken = default)
    {
        if (!PathGuard.IsValidName(newName))
        {
            throw StorageException.InvalidName(newName);
        }

        var normalized = PathGuard.Normalize(relativePath);
        var targetPath = PathGuard.Combine(PathGuard.GetParent(normalized), newName);

        using var client = Connect();

        var source = Resolve(normalized);
        var target = Resolve(targetPath);

        if (!client.Exists(source))
        {
            throw StorageException.NotFound(normalized);
        }

        if (client.Exists(target))
        {
            throw StorageException.Conflict(targetPath);
        }

        client.RenameFile(source, target);
        return Task.FromResult(ToEntry(client.Get(target), targetPath));
    }

    public Task MoveAsync(string sourcePath, string targetPath, bool overwrite, CancellationToken cancellationToken = default)
    {
        using var client = Connect();

        var source = Resolve(sourcePath);
        var target = Resolve(targetPath);

        if (!client.Exists(source))
        {
            throw StorageException.NotFound(PathGuard.Normalize(sourcePath));
        }

        if (client.Exists(target))
        {
            if (!overwrite)
            {
                throw StorageException.Conflict(PathGuard.Normalize(targetPath));
            }

            Remove(client, target);
        }

        EnsureParent(client, PathGuard.Normalize(targetPath));
        client.RenameFile(source, target);

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);

        if (normalized.Length == 0)
        {
            throw StorageException.InvalidPath(normalized);
        }

        using var client = Connect();
        var remote = Resolve(normalized);

        if (!client.Exists(remote))
        {
            throw StorageException.NotFound(normalized);
        }

        Remove(client, remote);
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- Hilfen

    private static void Remove(SftpClient client, string remote)
    {
        var entry = client.Get(remote);

        if (!entry.IsDirectory)
        {
            client.DeleteFile(remote);
            return;
        }

        foreach (var child in client.ListDirectory(remote))
        {
            if (child.Name is "." or "..")
            {
                continue;
            }

            Remove(client, child.FullName);
        }

        client.DeleteDirectory(remote);
    }

    /// <summary>Legt fehlende Ordner oberhalb des Ziels an.</summary>
    private void EnsureParent(SftpClient client, string normalized)
    {
        var parent = PathGuard.GetParent(normalized);

        if (string.IsNullOrEmpty(parent))
        {
            return;
        }

        var segments = parent.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;

        foreach (var segment in segments)
        {
            current = current.Length == 0 ? segment : $"{current}/{segment}";
            var remote = Resolve(current);

            if (!client.Exists(remote))
            {
                client.CreateDirectory(remote);
            }
        }
    }

    private static StorageEntry ToEntry(ISftpFile file, string relativePath) => new(
        file.Name,
        relativePath,
        file.IsDirectory,
        file.IsDirectory ? 0 : file.Length,
        file.LastWriteTimeUtc,
        file.Name.StartsWith('.'),
        file.IsSymbolicLink);

    /// <summary>Hält die SFTP-Verbindung so lange offen, wie der Strom gebraucht wird.</summary>
    private sealed class ClientStream : Stream
    {
        private readonly SftpClient _client;
        private readonly Stream _inner;

        public ClientStream(SftpClient client, Stream inner)
        {
            _client = client;
            _inner = inner;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _client.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
