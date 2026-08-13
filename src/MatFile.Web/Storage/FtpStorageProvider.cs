using FluentFTP;
using FluentFTP.Exceptions;
using MatFile.Web.Data;

namespace MatFile.Web.Storage;

/// <summary>
/// Zugriff auf eine FTP- oder FTPS-Gegenstelle über FluentFTP.
/// Wie bei SFTP wird je Operation verbunden - unkompliziert und robust.
/// </summary>
public sealed class FtpStorageProvider : IStorageProvider
{
    private readonly ConnectionSettings _settings;
    private readonly string _basePath;
    private readonly string? _password;

    public FtpStorageProvider(StorageRoot root, ConnectionSettings settings, string? password)
    {
        _settings = settings;
        _basePath = RemotePath.NormalizeBase(root.BasePath);
        _password = password;
    }

    public StorageType StorageType => StorageType.Ftp;

    private async Task<AsyncFtpClient> ConnectAsync(CancellationToken cancellationToken)
    {
        var client = new AsyncFtpClient(
            _settings.Host,
            _settings.Username ?? "anonymous",
            _password ?? string.Empty,
            _settings.EffectivePort(StorageType.Ftp));

        client.Config.EncryptionMode = _settings.UseTls ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None;
        client.Config.ValidateAnyCertificate = _settings.IgnoreCertificateErrors;
        client.Config.ConnectTimeout = 15000;
        client.Config.ReadTimeout = 30000;

        // Im Containernetz meldet der Server im PASV-Modus oft eine unerreichbare Adresse.
        // EPSV liefert nur den Port, deshalb ist das hier die sichere Wahl.
        client.Config.DataConnectionType = _settings.Passive
            ? FtpDataConnectionType.EPSV
            : FtpDataConnectionType.AutoActive;

        try
        {
            await client.Connect(cancellationToken);
        }
        catch (Exception exception) when (exception is FtpException or System.Net.Sockets.SocketException or TimeoutException)
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

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var client = await ConnectAsync(cancellationToken);
            return await client.DirectoryExists(_basePath.Length == 0 ? "/" : _basePath, cancellationToken);
        }
        catch (StorageException)
        {
            return false;
        }
    }

    public async Task<StorageListing> ListAsync(string? relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);
        var remote = Resolve(normalized);

        await using var client = await ConnectAsync(cancellationToken);

        if (!await client.DirectoryExists(remote, cancellationToken))
        {
            throw StorageException.NotFound(normalized);
        }

        var items = await client.GetListing(remote, cancellationToken);

        var entries = items
            .Where(item => item.Name is not ("." or ".."))
            .Select(item => ToEntry(item, PathGuard.Combine(normalized, item.Name)))
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new StorageListing(normalized, PathGuard.GetParent(normalized), entries);
    }

    public async Task<StorageEntry?> GetAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);

        if (normalized.Length == 0)
        {
            return new StorageEntry(string.Empty, string.Empty, true, 0, DateTime.UtcNow);
        }

        var remote = Resolve(normalized);
        await using var client = await ConnectAsync(cancellationToken);

        if (await client.DirectoryExists(remote, cancellationToken))
        {
            return new StorageEntry(NameOf(normalized), normalized, true, 0,
                await SafeModifiedAsync(client, remote, cancellationToken));
        }

        if (!await client.FileExists(remote, cancellationToken))
        {
            return null;
        }

        var size = await client.GetFileSize(remote, -1, cancellationToken);

        return new StorageEntry(NameOf(normalized), normalized, false, Math.Max(0, size),
            await SafeModifiedAsync(client, remote, cancellationToken), NameOf(normalized).StartsWith('.'));
    }

    public async Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);
        var remote = Resolve(normalized);

        var client = await ConnectAsync(cancellationToken);

        try
        {
            if (!await client.FileExists(remote, cancellationToken))
            {
                throw StorageException.NotFound(normalized);
            }

            var stream = await client.OpenRead(remote, FtpDataType.Binary, 0, token: cancellationToken);
            return new ClientStream(client, stream);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task<Stream> OpenWriteAsync(string relativePath, bool overwrite, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);
        var remote = Resolve(normalized);

        var client = await ConnectAsync(cancellationToken);

        try
        {
            if (!overwrite && await client.FileExists(remote, cancellationToken))
            {
                throw StorageException.Conflict(normalized);
            }

            var parent = PathGuard.GetParent(normalized);
            if (!string.IsNullOrEmpty(parent))
            {
                await client.CreateDirectory(Resolve(parent), true, cancellationToken);
            }

            var stream = await client.OpenWrite(remote, FtpDataType.Binary, false, cancellationToken);
            return new ClientStream(client, stream);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task<StorageEntry> CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);
        var remote = Resolve(normalized);

        await using var client = await ConnectAsync(cancellationToken);

        if (await client.DirectoryExists(remote, cancellationToken) || await client.FileExists(remote, cancellationToken))
        {
            throw StorageException.Conflict(normalized);
        }

        await client.CreateDirectory(remote, true, cancellationToken);

        return new StorageEntry(NameOf(normalized), normalized, true, 0, DateTime.UtcNow);
    }

    public async Task<StorageEntry> RenameAsync(string relativePath, string newName, CancellationToken cancellationToken = default)
    {
        if (!PathGuard.IsValidName(newName))
        {
            throw StorageException.InvalidName(newName);
        }

        var normalized = PathGuard.Normalize(relativePath);
        var targetPath = PathGuard.Combine(PathGuard.GetParent(normalized), newName);

        await MoveAsync(normalized, targetPath, overwrite: false, cancellationToken);

        return await GetAsync(targetPath, cancellationToken) ?? throw StorageException.NotFound(targetPath);
    }

    public async Task MoveAsync(string sourcePath, string targetPath, bool overwrite, CancellationToken cancellationToken = default)
    {
        var source = Resolve(sourcePath);
        var target = Resolve(targetPath);

        await using var client = await ConnectAsync(cancellationToken);

        var isDirectory = await client.DirectoryExists(source, cancellationToken);

        if (!isDirectory && !await client.FileExists(source, cancellationToken))
        {
            throw StorageException.NotFound(PathGuard.Normalize(sourcePath));
        }

        var exists = isDirectory
            ? await client.DirectoryExists(target, cancellationToken)
            : await client.FileExists(target, cancellationToken);

        if (exists && !overwrite)
        {
            throw StorageException.Conflict(PathGuard.Normalize(targetPath));
        }

        var mode = overwrite ? FtpRemoteExists.Overwrite : FtpRemoteExists.NoCheck;

        var moved = isDirectory
            ? await client.MoveDirectory(source, target, mode, cancellationToken)
            : await client.MoveFile(source, target, mode, cancellationToken);

        if (!moved)
        {
            throw new StorageException("remote_error", "The entry could not be moved.");
        }
    }

    public async Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);

        if (normalized.Length == 0)
        {
            throw StorageException.InvalidPath(normalized);
        }

        var remote = Resolve(normalized);
        await using var client = await ConnectAsync(cancellationToken);

        if (await client.DirectoryExists(remote, cancellationToken))
        {
            await client.DeleteDirectory(remote, FtpListOption.Recursive, cancellationToken);
            return;
        }

        if (!await client.FileExists(remote, cancellationToken))
        {
            throw StorageException.NotFound(normalized);
        }

        await client.DeleteFile(remote, cancellationToken);
    }

    // ---------------------------------------------------------------- Hilfen

    private static string NameOf(string normalized) => normalized[(normalized.LastIndexOf('/') + 1)..];

    private static async Task<DateTime> SafeModifiedAsync(AsyncFtpClient client, string remote, CancellationToken cancellationToken)
    {
        try
        {
            var value = await client.GetModifiedTime(remote, cancellationToken);
            return value == DateTime.MinValue ? DateTime.UtcNow : value.ToUniversalTime();
        }
        catch (FtpException)
        {
            // Nicht jeder Server unterstützt MDTM.
            return DateTime.UtcNow;
        }
    }

    private static StorageEntry ToEntry(FtpListItem item, string relativePath) => new(
        item.Name,
        relativePath,
        item.Type == FtpObjectType.Directory,
        item.Type == FtpObjectType.Directory ? 0 : Math.Max(0, item.Size),
        item.Modified == DateTime.MinValue ? DateTime.UtcNow : item.Modified.ToUniversalTime(),
        item.Name.StartsWith('.'),
        item.Type == FtpObjectType.Link);

    /// <summary>Hält die FTP-Verbindung offen, solange der Strom gebraucht wird.</summary>
    private sealed class ClientStream : Stream
    {
        private readonly AsyncFtpClient _client;
        private readonly Stream _inner;

        public ClientStream(AsyncFtpClient client, Stream inner)
        {
            _client = client;
            _inner = inner;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(buffer, cancellationToken);

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            await _client.DisposeAsync();
        }

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
