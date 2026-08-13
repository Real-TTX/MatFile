using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using MatFile.Web.Data;

namespace MatFile.Web.Storage;

/// <summary>
/// Zugriff auf eine WebDAV-Freigabe (Nextcloud, ownCloud, IIS, Apache).
/// Nutzt PROPFIND, GET, PUT, MKCOL, DELETE und MOVE.
/// </summary>
public sealed class WebDavStorageProvider : IStorageProvider
{
    private static readonly XNamespace Dav = "DAV:";

    private readonly HttpClient _client;
    private readonly Uri _baseUri;

    public WebDavStorageProvider(StorageRoot root, ConnectionSettings settings, string? password, HttpClient client)
    {
        _client = client;

        var url = (settings.Url ?? string.Empty).TrimEnd('/');

        if (url.Length == 0)
        {
            var scheme = settings.UseTls ? "https" : "http";
            url = $"{scheme}://{settings.Host}:{settings.EffectivePort(StorageType.WebDav)}";
        }

        var basePath = RemotePath.NormalizeBase(root.BasePath);
        _baseUri = new Uri(url + basePath + "/");

        if (!string.IsNullOrEmpty(settings.Username))
        {
            var token = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{settings.Username}:{password ?? string.Empty}"));
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
    }

    public StorageType StorageType => StorageType.WebDav;

    private Uri Resolve(string? relativePath, bool asDirectory = false)
    {
        var normalized = PathGuard.Normalize(relativePath);

        if (normalized.Split('/').Any(segment => segment is ".." or "."))
        {
            throw StorageException.InvalidPath(normalized);
        }

        var encoded = string.Join('/', normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));

        var suffix = asDirectory && encoded.Length > 0 ? "/" : string.Empty;
        return new Uri(_baseUri, encoded + suffix);
    }

    /// <summary>
    /// Sendet eine Anfrage und folgt Umleitungen selbst. Apache leitet Ordner ohne
    /// Schrägstrich per 301 um; HttpClient wirft dabei den Authorization-Header weg,
    /// deshalb bauen wir die Anfrage neu auf statt automatisch zu folgen.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, Uri uri, string? depth, Func<HttpContent>? content, CancellationToken cancellationToken)
    {
        var target = uri;

        for (var redirect = 0; redirect < 3; redirect++)
        {
            using var request = new HttpRequestMessage(method, target);

            if (depth is not null)
            {
                request.Headers.Add("Depth", depth);
            }

            if (content is not null)
            {
                request.Content = content();
            }

            HttpResponseMessage response;

            try
            {
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                throw new StorageException("connection_failed", exception.Message);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new StorageException("connection_failed", "Timeout");
            }

            var status = (int)response.StatusCode;

            if (status is 301 or 302 or 307 or 308 && response.Headers.Location is { } location)
            {
                target = new Uri(target, location);
                response.Dispose();
                continue;
            }

            return response;
        }

        throw new StorageException("remote_error", "Too many redirects.");
    }

    private static void EnsureSuccess(HttpResponseMessage response, string path)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound => StorageException.NotFound(path),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => StorageException.AccessDenied(),
            HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or (HttpStatusCode)423 =>
                StorageException.Conflict(path),
            _ => new StorageException("remote_error", $"{(int)response.StatusCode} {response.ReasonPhrase}")
        };
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(new HttpMethod("PROPFIND"), _baseUri, "0", PropfindBody, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (StorageException)
        {
            return false;
        }
    }

    private static StringContent PropfindBody() => new(
        """
        <?xml version="1.0" encoding="utf-8"?>
        <D:propfind xmlns:D="DAV:">
          <D:prop>
            <D:resourcetype/>
            <D:getcontentlength/>
            <D:getlastmodified/>
          </D:prop>
        </D:propfind>
        """, Encoding.UTF8, "application/xml");

    public async Task<StorageListing> ListAsync(string? relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);
        var uri = Resolve(normalized, asDirectory: true);

        using var response = await SendAsync(new HttpMethod("PROPFIND"), uri, "1", PropfindBody, cancellationToken);
        EnsureSuccess(response, normalized);

        var document = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var entries = new List<StorageEntry>();

        var selfPath = uri.AbsolutePath.TrimEnd('/');

        foreach (var element in document.Descendants(Dav + "response"))
        {
            var href = element.Element(Dav + "href")?.Value;
            if (string.IsNullOrEmpty(href))
            {
                continue;
            }

            var absolute = new Uri(_baseUri, href).AbsolutePath.TrimEnd('/');

            // Der Ordner selbst steht auch in der Antwort und wird übersprungen.
            if (string.Equals(absolute, selfPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Uri.UnescapeDataString(absolute[(absolute.LastIndexOf('/') + 1)..]);
            if (name.Length == 0)
            {
                continue;
            }

            entries.Add(ToEntry(element, name, PathGuard.Combine(normalized, name)));
        }

        var sorted = entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new StorageListing(normalized, PathGuard.GetParent(normalized), sorted);
    }

    public async Task<StorageEntry?> GetAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);

        if (normalized.Length == 0)
        {
            return new StorageEntry(string.Empty, string.Empty, true, 0, DateTime.UtcNow);
        }

        using var response = await SendAsync(new HttpMethod("PROPFIND"), Resolve(normalized), "0", PropfindBody, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureSuccess(response, normalized);

        var document = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var element = document.Descendants(Dav + "response").FirstOrDefault();

        if (element is null)
        {
            return null;
        }

        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        return ToEntry(element, name, normalized);
    }

    public async Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);
        var response = await SendAsync(HttpMethod.Get, Resolve(normalized), null, null, cancellationToken);
        EnsureSuccess(response, normalized);

        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    public async Task<Stream> OpenWriteAsync(string relativePath, bool overwrite, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);

        if (!overwrite && await GetAsync(normalized, cancellationToken) is not null)
        {
            throw StorageException.Conflict(normalized);
        }

        await EnsureParentAsync(normalized, cancellationToken);

        // Der Inhalt wird zwischengepuffert und beim Schließen als PUT gesendet.
        return new UploadStream(this, Resolve(normalized), normalized);
    }

    public async Task<StorageEntry> CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);

        if (await GetAsync(normalized, cancellationToken) is not null)
        {
            throw StorageException.Conflict(normalized);
        }

        await EnsureParentAsync(normalized, cancellationToken);

        using var response = await SendAsync(new HttpMethod("MKCOL"), Resolve(normalized, true), null, null, cancellationToken);
        EnsureSuccess(response, normalized);

        return new StorageEntry(normalized[(normalized.LastIndexOf('/') + 1)..], normalized, true, 0, DateTime.UtcNow);
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
        var source = PathGuard.Normalize(sourcePath);
        var target = PathGuard.Normalize(targetPath);

        await EnsureParentAsync(target, cancellationToken);

        using var request = new HttpRequestMessage(new HttpMethod("MOVE"), Resolve(source));
        request.Headers.Add("Destination", Resolve(target).AbsoluteUri);
        request.Headers.Add("Overwrite", overwrite ? "T" : "F");

        using var response = await _client.SendAsync(request, cancellationToken);
        EnsureSuccess(response, target);
    }

    public async Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.Normalize(relativePath);

        if (normalized.Length == 0)
        {
            throw StorageException.InvalidPath(normalized);
        }

        using var response = await SendAsync(HttpMethod.Delete, Resolve(normalized), null, null, cancellationToken);
        EnsureSuccess(response, normalized);
    }

    private async Task EnsureParentAsync(string normalized, CancellationToken cancellationToken)
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

            if (await GetAsync(current, cancellationToken) is null)
            {
                using var response = await SendAsync(new HttpMethod("MKCOL"), Resolve(current, true), null, null, cancellationToken);
                EnsureSuccess(response, current);
            }
        }
    }

    private static StorageEntry ToEntry(XElement response, string name, string relativePath)
    {
        var prop = response.Descendants(Dav + "prop").FirstOrDefault();
        var isDirectory = prop?.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null;

        long.TryParse(prop?.Element(Dav + "getcontentlength")?.Value, out var size);

        var modified = DateTime.UtcNow;
        var lastModified = prop?.Element(Dav + "getlastmodified")?.Value;

        if (!string.IsNullOrEmpty(lastModified)
            && DateTime.TryParse(lastModified, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            modified = parsed;
        }

        return new StorageEntry(name, relativePath, isDirectory, isDirectory ? 0 : size, modified, name.StartsWith('.'));
    }

    /// <summary>Puffert den Inhalt und schickt ihn beim Schließen als PUT.</summary>
    private sealed class UploadStream : Stream
    {
        private readonly WebDavStorageProvider _provider;
        private readonly Uri _uri;
        private readonly string _path;
        private readonly string _tempFile;
        private readonly FileStream _buffer;

        public UploadStream(WebDavStorageProvider provider, Uri uri, string path)
        {
            _provider = provider;
            _uri = uri;
            _path = path;
            _tempFile = Path.Combine(Path.GetTempPath(), $"matfile-dav-{Guid.NewGuid():N}.tmp");
            _buffer = new FileStream(_tempFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;

        public override long Position
        {
            get => _buffer.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _buffer.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _buffer.Write(buffer, offset, count);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _buffer.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _buffer.WriteAsync(buffer, cancellationToken);

        private bool _sent;

        public override async ValueTask DisposeAsync()
        {
            // Nur einmal senden: Dispose und DisposeAsync rufen sich sonst gegenseitig auf.
            if (_sent)
            {
                return;
            }

            _sent = true;

            await _buffer.FlushAsync();
            _buffer.Position = 0;

            try
            {
                // Bei einer Umleitung wird der Inhalt neu aufgebaut, deshalb eine Fabrik.
                HttpContent CreateContent()
                {
                    _buffer.Position = 0;
                    var content = new StreamContent(_buffer);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    return content;
                }

                using var response = await _provider.SendAsync(HttpMethod.Put, _uri, null, CreateContent, CancellationToken.None);
                EnsureSuccess(response, _path);
            }
            finally
            {
                await _buffer.DisposeAsync();
                File.Delete(_tempFile);
            }

            GC.SuppressFinalize(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_sent)
            {
                DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            base.Dispose(disposing);
        }
    }
}
