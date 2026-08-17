using System.Net;
using MatFile.Web.Data;
using SMBLibrary;
using SMBLibrary.Client;
using FileAttributes = SMBLibrary.FileAttributes;

namespace MatFile.Web.Storage;

/// <summary>
/// Zugriff auf eine SMB-Freigabe über SMBLibrary - ohne OS-Mount und damit
/// ohne zusätzliche Rechte im Container.
/// Pfade innerhalb der Freigabe verwenden Backslashes ohne führenden Trenner.
/// </summary>
public sealed class SmbStorageProvider : IStorageProvider
{
    private readonly ConnectionSettings _settings;
    private readonly string _basePath;
    private readonly string? _password;

    public SmbStorageProvider(StorageRoot root, ConnectionSettings settings, string? password)
    {
        _settings = settings;
        _basePath = PathGuard.Normalize(root.BasePath);
        _password = password;
    }

    public StorageType StorageType => StorageType.Smb;

    /// <summary>Verbindung samt angebundener Freigabe. Wird je Operation aufgebaut.</summary>
    private sealed class Session : IDisposable
    {
        public SMB2Client Client { get; init; } = default!;
        public ISMBFileStore Store { get; init; } = default!;

        /// <summary>Handle schließen, ohne an einer bereits beendeten Verbindung zu scheitern.</summary>
        public void CloseQuietly(object handle)
        {
            try
            {
                Store.CloseFile(handle);
            }
            catch (Exception)
            {
                // Verbindung ist weg - dann ist auch das Handle weg.
            }
        }

        public void Dispose()
        {
            try
            {
                Store.Disconnect();
                Client.Logoff();
            }
            catch (Exception)
            {
                // Verbindung ist ohnehin am Ende.
            }
            finally
            {
                Client.Disconnect();
            }
        }
    }

    /// <summary>
    /// Ohne eingetragene Freigabe steht der ganze Server als Speicherort da:
    /// die oberste Ebene zeigt dann die Freigaben als Ordner, und der erste
    /// Abschnitt eines Pfades ist die Freigabe.
    /// </summary>
    private bool ServerMode => string.IsNullOrWhiteSpace(_settings.Share);

    /// <summary>Zerlegt einen Pfad in Freigabe und den Rest innerhalb der Freigabe.</summary>
    private (string Share, string Path) Split(string? relativePath)
    {
        var normalized = PathGuard.Normalize(relativePath);

        if (normalized.Split('/').Any(segment => segment is ".." or "."))
        {
            throw StorageException.InvalidPath(normalized);
        }

        if (!ServerMode)
        {
            return (_settings.Share!, normalized);
        }

        var cut = normalized.IndexOf('/');

        return cut < 0
            ? (normalized, string.Empty)
            : (normalized[..cut], normalized[(cut + 1)..]);
    }

    /// <summary>
    /// Auf der obersten Ebene stehen im Servermodus die Freigaben des Servers.
    /// Dort lässt sich nichts anlegen, umbenennen oder löschen - das entscheidet
    /// die Freigabeverwaltung des Servers, nicht MatFile.
    /// </summary>
    private void RequireInsideShare(string? relativePath)
    {
        if (!ServerMode)
        {
            return;
        }

        var (share, path) = Split(relativePath);

        if (share.Length == 0 || path.Length == 0)
        {
            throw new StorageException("share_level",
                "This level lists the shares of the server; entries can only be changed inside a share.");
        }
    }

    private Session Connect(string? relativePath = null)
    {
        var (share, _) = Split(relativePath);

        if (string.IsNullOrWhiteSpace(share))
        {
            // Auf der Ebene der Freigaben selbst gibt es nichts anzubinden.
            throw new StorageException("share_missing", "No share name configured.");
        }

        var client = Login();
        var store = client.TreeConnect(share, out var status);

        if (status != NTStatus.STATUS_SUCCESS || store is null)
        {
            client.Logoff();
            client.Disconnect();
            throw new StorageException("share_not_found", status.ToString());
        }

        return new Session { Client = client, Store = store };
    }

    /// <summary>Anmeldung ohne Freigabe - reicht, um die Freigaben aufzulisten.</summary>
    private SMB2Client Login()
    {
        var client = new SMB2Client();
        IPAddress address;

        try
        {
            address = Dns.GetHostAddresses(_settings.Host)
                          .FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                      ?? throw new StorageException("connection_failed", $"Host not found: {_settings.Host}");
        }
        catch (System.Net.Sockets.SocketException exception)
        {
            throw new StorageException("connection_failed", exception.Message);
        }

        if (!client.Connect(address, SMBTransportType.DirectTCPTransport))
        {
            throw new StorageException("connection_failed", $"No connection to {_settings.Host}.");
        }

        var login = client.Login(string.Empty, _settings.Username ?? string.Empty, _password ?? string.Empty);

        if (login != NTStatus.STATUS_SUCCESS)
        {
            client.Disconnect();
            throw login is NTStatus.STATUS_LOGON_FAILURE or NTStatus.STATUS_ACCESS_DENIED
                ? StorageException.AccessDenied()
                : new StorageException("connection_failed", login.ToString());
        }

        return client;
    }

    /// <summary>
    /// Freigaben des Servers - für die Auswahl im Verbindungsdialog.
    /// Administrative Freigaben (C$, IPC$) bleiben außen vor.
    /// </summary>
    public Task<IReadOnlyList<string>> ListSharesAsync()
    {
        var client = Login();

        try
        {
            var shares = client.ListShares(out var status);

            if (status != NTStatus.STATUS_SUCCESS || shares is null)
            {
                throw new StorageException("share_list_failed", status.ToString());
            }

            return Task.FromResult<IReadOnlyList<string>>(
                shares.Where(share => !share.EndsWith('$')).OrderBy(share => share).ToList());
        }
        finally
        {
            try
            {
                client.Logoff();
            }
            catch (Exception)
            {
                // Verbindung ist ohnehin am Ende.
            }

            client.Disconnect();
        }
    }

    /// <summary>
    /// Interner Pfad in die Schreibweise innerhalb der Freigabe übersetzen.
    /// Im Servermodus ist die Freigabe schon abgeschnitten, sonst gilt der Basispfad.
    /// </summary>
    private string Resolve(string? relativePath)
    {
        var (_, path) = Split(relativePath);

        // Im Servermodus steht jede Freigabe für sich - ein gemeinsamer Basispfad ergäbe dort keinen Sinn.
        var basePath = ServerMode ? string.Empty : _basePath;

        var combined = basePath.Length == 0
            ? path
            : path.Length == 0 ? basePath : $"{basePath}/{path}";

        return combined.Replace('/', '\\');
    }

    private static void Check(NTStatus status, string path)
    {
        switch (status)
        {
            case NTStatus.STATUS_SUCCESS:
            case NTStatus.STATUS_END_OF_FILE:
                return;
            case NTStatus.STATUS_OBJECT_NAME_NOT_FOUND:
            case NTStatus.STATUS_OBJECT_PATH_NOT_FOUND:
            case NTStatus.STATUS_NO_SUCH_FILE:
                throw StorageException.NotFound(path);
            case NTStatus.STATUS_OBJECT_NAME_COLLISION:
                throw StorageException.Conflict(path);
            case NTStatus.STATUS_ACCESS_DENIED:
            case NTStatus.STATUS_LOGON_FAILURE:
                throw StorageException.AccessDenied();
            default:
                throw new StorageException("remote_error", status.ToString());
        }
    }

    private static bool IsMissing(NTStatus status)
        => status is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND
            or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND
            or NTStatus.STATUS_NO_SUCH_FILE;

    // ---------------------------------------------------------------- Lesen

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Ohne feste Freigabe reicht die Anmeldung: erreichbar ist der Server.
            if (ServerMode)
            {
                var client = Login();
                client.Logoff();
                client.Disconnect();
                return Task.FromResult(true);
            }

            using var session = Connect(string.Empty);
            return Task.FromResult(Exists(session, Resolve(string.Empty), out _));
        }
        catch (StorageException)
        {
            return Task.FromResult(false);
        }
    }

    private static bool Exists(Session session, string remote, out bool isDirectory)
    {
        isDirectory = false;

        var status = session.Store.CreateFile(out var handle, out _, remote,
            AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

        if (status != NTStatus.STATUS_SUCCESS)
        {
            return false;
        }

        try
        {
            if (session.Store.GetFileInformation(out var info, handle, FileInformationClass.FileBasicInformation)
                == NTStatus.STATUS_SUCCESS && info is FileBasicInformation basic)
            {
                isDirectory = basic.FileAttributes.HasFlag(FileAttributes.Directory);
            }

            return true;
        }
        finally
        {
            session.Store.CloseFile(handle);
        }
    }

    public Task<StorageListing> ListAsync(string? relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);

        // Oberste Ebene ohne feste Freigabe: die Freigaben selbst sind die Ordner.
        if (ServerMode && normalized.Length == 0)
        {
            return ListSharesAsync().ContinueWith(task =>
            {
                var entries = task.Result
                    .Select(share => new StorageEntry(share, share, true, 0, DateTime.UtcNow))
                    .ToList();

                return new StorageListing(string.Empty, null, entries);
            }, cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        var remote = Resolve(normalized);

        using var session = Connect(normalized);

        var status = session.Store.CreateFile(out var handle, out _, remote,
            AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);

        Check(status, normalized);

        try
        {
            status = session.Store.QueryDirectory(out var files, handle, "*", FileInformationClass.FileDirectoryInformation);

            if (status is not (NTStatus.STATUS_SUCCESS or NTStatus.STATUS_NO_MORE_FILES))
            {
                Check(status, normalized);
            }

            var entries = new List<StorageEntry>();

            foreach (var file in files.OfType<FileDirectoryInformation>())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (file.FileName is "." or "..")
                {
                    continue;
                }

                var isDirectory = file.FileAttributes.HasFlag(FileAttributes.Directory);

                entries.Add(new StorageEntry(
                    file.FileName,
                    PathGuard.Combine(normalized, file.FileName),
                    isDirectory,
                    isDirectory ? 0 : file.EndOfFile,
                    file.LastWriteTime,
                    file.FileName.StartsWith('.')));
            }

            var sorted = entries
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Task.FromResult(new StorageListing(normalized, PathGuard.GetParent(normalized), sorted));
        }
        finally
        {
            session.Store.CloseFile(handle);
        }
    }

    public Task<StorageEntry?> GetAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);

        if (normalized.Length == 0)
        {
            return Task.FromResult<StorageEntry?>(new StorageEntry(string.Empty, string.Empty, true, 0, DateTime.UtcNow));
        }

        // Eine Freigabe selbst ist ein Ordner - nachsehen lässt sie sich nur in der Liste.
        if (ServerMode && !normalized.Contains('/'))
        {
            var vorhanden = ListSharesAsync().GetAwaiter().GetResult()
                .Any(share => string.Equals(share, normalized, StringComparison.OrdinalIgnoreCase));

            return Task.FromResult<StorageEntry?>(vorhanden
                ? new StorageEntry(normalized, normalized, true, 0, DateTime.UtcNow)
                : null);
        }

        var remote = Resolve(normalized);
        using var session = Connect(normalized);

        var status = session.Store.CreateFile(out var handle, out _, remote,
            AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.Read | ShareAccess.Write,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

        if (IsMissing(status))
        {
            return Task.FromResult<StorageEntry?>(null);
        }

        Check(status, normalized);

        try
        {
            session.Store.GetFileInformation(out var standard, handle, FileInformationClass.FileStandardInformation);
            session.Store.GetFileInformation(out var basic, handle, FileInformationClass.FileBasicInformation);

            var isDirectory = basic is FileBasicInformation b && b.FileAttributes.HasFlag(FileAttributes.Directory);
            var size = standard is FileStandardInformation s ? s.EndOfFile : 0;
            var modified = basic is FileBasicInformation m ? m.LastWriteTime.Time ?? DateTime.UtcNow : DateTime.UtcNow;
            var name = normalized[(normalized.LastIndexOf('/') + 1)..];

            return Task.FromResult<StorageEntry?>(new StorageEntry(
                name, normalized, isDirectory, isDirectory ? 0 : size, modified, name.StartsWith('.')));
        }
        finally
        {
            session.Store.CloseFile(handle);
        }
    }

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);
        var remote = Resolve(normalized);

        var session = Connect(normalized);

        try
        {
            var status = session.Store.CreateFile(out var handle, out _, remote,
                AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.Read,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

            Check(status, normalized);

            session.Store.GetFileInformation(out var info, handle, FileInformationClass.FileStandardInformation);
            var length = info is FileStandardInformation standard ? standard.EndOfFile : 0;

            return Task.FromResult<Stream>(new SmbReadStream(session, handle, length, (int)session.Client.MaxReadSize));
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    // ---------------------------------------------------------------- Schreiben

    public Task<Stream> OpenWriteAsync(string relativePath, bool overwrite, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);
        RequireInsideShare(normalized);
        var remote = Resolve(normalized);

        var session = Connect(normalized);

        try
        {
            if (!overwrite && Exists(session, remote, out _))
            {
                throw StorageException.Conflict(normalized);
            }

            EnsureParent(session, normalized);

            var status = session.Store.CreateFile(out var handle, out _, remote,
                AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None,
                CreateDisposition.FILE_OVERWRITE_IF,
                CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

            Check(status, normalized);

            return Task.FromResult<Stream>(new SmbWriteStream(session, handle, (int)session.Client.MaxWriteSize));
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public Task<StorageEntry> CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);
        RequireInsideShare(normalized);

        using var session = Connect(normalized);

        if (Exists(session, Resolve(normalized), out _))
        {
            throw StorageException.Conflict(normalized);
        }

        EnsureParent(session, normalized);
        CreateDirectory(session, Resolve(normalized), normalized);

        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        return Task.FromResult(new StorageEntry(name, normalized, true, 0, DateTime.UtcNow));
    }

    private static void CreateDirectory(Session session, string remote, string path)
    {
        var status = session.Store.CreateFile(out var handle, out _, remote,
            AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Directory, ShareAccess.None,
            CreateDisposition.FILE_CREATE, CreateOptions.FILE_DIRECTORY_FILE, null);

        Check(status, path);
        session.Store.CloseFile(handle);
    }

    private void EnsureParent(Session session, string normalized)
    {
        var parent = PathGuard.GetParent(normalized);

        if (string.IsNullOrEmpty(parent))
        {
            return;
        }

        var current = string.Empty;

        foreach (var segment in parent.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Length == 0 ? segment : $"{current}/{segment}";
            var remote = Resolve(current);

            if (!Exists(session, remote, out _))
            {
                CreateDirectory(session, remote, current);
            }
        }
    }

    public Task<StorageEntry> RenameAsync(string relativePath, string newName, CancellationToken cancellationToken = default)
    {
        if (!PathGuard.IsValidName(newName))
        {
            throw StorageException.InvalidName(newName);
        }

        var normalized = PathGuard.Normalize(relativePath);
        var targetPath = PathGuard.Combine(PathGuard.GetParent(normalized), newName);

        Move(normalized, targetPath, overwrite: false);

        var name = targetPath[(targetPath.LastIndexOf('/') + 1)..];
        return Task.FromResult(new StorageEntry(name, targetPath, false, 0, DateTime.UtcNow));
    }

    public Task MoveAsync(string sourcePath, string targetPath, bool overwrite, CancellationToken cancellationToken = default)
    {
        Move(PathGuard.Normalize(sourcePath), PathGuard.Normalize(targetPath), overwrite);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verschieben in einem Zug klappt nur innerhalb einer Freigabe - ein Server
    /// kennt keinen Weg, eine Datei von einer Freigabe in die andere umzuhängen.
    /// </summary>
    public bool CanMove(string sourcePath, string targetPath)
        => !ServerMode || string.Equals(Split(sourcePath).Share, Split(targetPath).Share, StringComparison.OrdinalIgnoreCase);

    private void Move(string sourcePath, string targetPath, bool overwrite)
    {
        RequireInsideShare(sourcePath);
        RequireInsideShare(targetPath);

        // Umbenennen geht nur innerhalb derselben Freigabe; über Freigaben hinweg
        // kopiert und löscht der Auftrag stattdessen (siehe CanMove).
        if (!CanMove(sourcePath, targetPath))
        {
            throw StorageException.InvalidPath(targetPath);
        }

        using var session = Connect(sourcePath);

        var source = Resolve(sourcePath);

        if (!Exists(session, source, out var isDirectory))
        {
            throw StorageException.NotFound(sourcePath);
        }

        if (!overwrite && Exists(session, Resolve(targetPath), out _))
        {
            throw StorageException.Conflict(targetPath);
        }

        EnsureParent(session, targetPath);

        var status = session.Store.CreateFile(out var handle, out _, source,
            AccessMask.GENERIC_ALL | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None,
            CreateDisposition.FILE_OPEN,
            isDirectory ? CreateOptions.FILE_DIRECTORY_FILE : CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

        Check(status, sourcePath);

        try
        {
            var rename = new FileRenameInformationType2
            {
                FileName = Resolve(targetPath),
                ReplaceIfExists = overwrite
            };

            Check(session.Store.SetFileInformation(handle, rename), targetPath);
        }
        finally
        {
            session.Store.CloseFile(handle);
        }
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);

        if (normalized.Length == 0)
        {
            throw StorageException.InvalidPath(normalized);
        }

        RequireInsideShare(normalized);

        using var session = Connect(normalized);
        Remove(session, normalized);

        return Task.CompletedTask;
    }

    private void Remove(Session session, string normalized)
    {
        var remote = Resolve(normalized);

        if (!Exists(session, remote, out var isDirectory))
        {
            throw StorageException.NotFound(normalized);
        }

        if (isDirectory)
        {
            // SMB löscht nur leere Ordner, also erst den Inhalt.
            foreach (var child in ListAsync(normalized).GetAwaiter().GetResult().Entries)
            {
                Remove(session, child.Path);
            }
        }

        var status = session.Store.CreateFile(out var handle, out _, remote,
            AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None,
            CreateDisposition.FILE_OPEN,
            isDirectory ? CreateOptions.FILE_DIRECTORY_FILE : CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

        Check(status, normalized);

        try
        {
            Check(session.Store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true }), normalized);
        }
        finally
        {
            session.Store.CloseFile(handle);
        }
    }

    // ---------------------------------------------------------------- Ströme

    private sealed class SmbReadStream : Stream
    {
        private readonly Session _session;
        private readonly object _handle;
        private readonly int _chunk;
        private long _position;

        public SmbReadStream(Session session, object handle, long length, int chunk)
        {
            _session = session;
            _handle = handle;
            Length = length;
            _chunk = chunk > 0 ? chunk : 64 * 1024;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length { get; }

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // Nicht über das Dateiende hinaus lesen - manche Server beenden dabei die Verbindung.
            var remaining = Length - _position;

            if (remaining <= 0)
            {
                return 0;
            }

            var wanted = (int)Math.Min(Math.Min(count, _chunk), remaining);
            var status = _session.Store.ReadFile(out var data, _handle, _position, wanted);

            if (status == NTStatus.STATUS_END_OF_FILE || data is null || data.Length == 0)
            {
                return 0;
            }

            Check(status, string.Empty);

            Buffer.BlockCopy(data, 0, buffer, offset, data.Length);
            _position += data.Length;

            return data.Length;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _session.CloseQuietly(_handle);
                _session.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class SmbWriteStream : Stream
    {
        private readonly Session _session;
        private readonly object _handle;
        private readonly int _chunk;
        private long _position;

        public SmbWriteStream(Session session, object handle, int chunk)
        {
            _session = session;
            _handle = handle;
            _chunk = chunk > 0 ? chunk : 64 * 1024;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _position;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            var written = 0;

            while (written < count)
            {
                var size = Math.Min(_chunk, count - written);
                var slice = new byte[size];
                Buffer.BlockCopy(buffer, offset + written, slice, 0, size);

                Check(_session.Store.WriteFile(out var bytes, _handle, _position, slice), string.Empty);

                if (bytes <= 0)
                {
                    throw new StorageException("remote_error", "The remote share accepted no data.");
                }

                _position += bytes;
                written += bytes;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _session.CloseQuietly(_handle);
                _session.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
