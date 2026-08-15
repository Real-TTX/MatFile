using System.Text.Json;
using MatFile.Web.Storage;

namespace MatFile.Web.Services;

public record UploadSession(
    Guid Id,
    long UserId,
    long RootId,
    string Path,
    string Name,
    long Size,
    bool Overwrite)
{
    public long Received { get; set; }
}

/// <summary>
/// Upload in Teilstücken. Die Teile landen unter /data/temp/uploads und werden erst
/// am Ende in den Zielordner geschrieben - ein abgebrochener Upload lässt sich fortsetzen.
/// </summary>
public sealed class UploadService
{
    private readonly MatFilePaths _paths;
    private readonly StorageRootService _roots;
    private readonly MatFileConfigStore _config;
    private readonly ILogger<UploadService> _logger;

    public UploadService(
        MatFilePaths paths,
        StorageRootService roots,
        MatFileConfigStore config,
        ILogger<UploadService> logger)
    {
        _paths = paths;
        _roots = roots;
        _config = config;
        _logger = logger;
        Directory.CreateDirectory(UploadDirectory);
    }

    /// <summary>Höchstgröße in Byte, oder 0 wenn keine Grenze gesetzt ist.</summary>
    private long MaxBytes => Math.Max(0, _config.Settings.MaxUploadMegabytes) * 1024L * 1024L;

    private string UploadDirectory => Path.Combine(_paths.TempDirectory, "uploads");

    private string PartFile(Guid id) => Path.Combine(UploadDirectory, $"{id:N}.part");

    private string MetaFile(Guid id) => Path.Combine(UploadDirectory, $"{id:N}.json");

    public async Task<UploadSession> BeginAsync(long userId, long rootId, string? path, string name, long size, bool overwrite)
    {
        var opened = await _roots.OpenAsync(userId, rootId)
                     ?? throw new StorageException("root_not_found", "Storage location not found.");

        if (!opened.Permissions.CanUpload)
        {
            throw StorageException.AccessDenied();
        }

        if (!PathGuard.IsValidName(name))
        {
            throw StorageException.InvalidName(name);
        }

        // Die angekündigte Größe wird gleich hier geprüft - sonst liegt erst alles
        // im Zwischenspeicher, bevor es scheitert.
        if (MaxBytes > 0 && size > MaxBytes)
        {
            throw new StorageException("upload_too_large",
                $"The file exceeds the limit of {_config.Settings.MaxUploadMegabytes} MB.");
        }

        var targetPath = PathGuard.Combine(path, name);

        if (!overwrite && await opened.Provider.GetAsync(targetPath) is not null)
        {
            throw StorageException.Conflict(targetPath);
        }

        var session = new UploadSession(Guid.NewGuid(), userId, rootId, PathGuard.Normalize(path), name, size, overwrite);

        await File.WriteAllTextAsync(MetaFile(session.Id), JsonSerializer.Serialize(session));
        await using (File.Create(PartFile(session.Id)))
        {
        }

        return session;
    }

    public async Task<long> AppendAsync(Guid uploadId, long userId, long offset, Stream body, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(uploadId, userId);
        var partFile = PartFile(uploadId);
        var length = new FileInfo(partFile).Length;

        if (offset != length)
        {
            // Der Client ist nicht synchron - er bekommt die tatsächliche Position zurück.
            return length;
        }

        await using (var stream = new FileStream(partFile, FileMode.Append, FileAccess.Write, FileShare.None,
                         bufferSize: 64 * 1024, useAsync: true))
        {
            await body.CopyToAsync(stream, cancellationToken);
        }

        var received = new FileInfo(partFile).Length;

        if (received > session.Size && session.Size > 0)
        {
            await AbortAsync(uploadId, userId);
            throw new StorageException("size_exceeded", "More data was sent than announced.");
        }

        // Wer eine kleine Größe ankündigt und dann weiterschickt, wird hier gestoppt.
        if (MaxBytes > 0 && received > MaxBytes)
        {
            await AbortAsync(uploadId, userId);
            throw new StorageException("upload_too_large",
                $"The file exceeds the limit of {_config.Settings.MaxUploadMegabytes} MB.");
        }

        return received;
    }

    public async Task<StorageEntry> FinishAsync(Guid uploadId, long userId, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(uploadId, userId);

        var opened = await _roots.OpenAsync(userId, session.RootId)
                     ?? throw new StorageException("root_not_found", "Storage location not found.");

        if (!opened.Permissions.CanUpload)
        {
            throw StorageException.AccessDenied();
        }

        var targetPath = PathGuard.Combine(session.Path, session.Name);
        var partFile = PartFile(uploadId);

        await using (var input = new FileStream(partFile, FileMode.Open, FileAccess.Read, FileShare.None,
                         bufferSize: 64 * 1024, useAsync: true))
        await using (var output = await opened.Provider.OpenWriteAsync(targetPath, session.Overwrite, cancellationToken))
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        Cleanup(uploadId);

        return await opened.Provider.GetAsync(targetPath, cancellationToken)
               ?? throw StorageException.NotFound(targetPath);
    }

    public async Task AbortAsync(Guid uploadId, long userId)
    {
        await LoadAsync(uploadId, userId);
        Cleanup(uploadId);
    }

    private async Task<UploadSession> LoadAsync(Guid uploadId, long userId)
    {
        var metaFile = MetaFile(uploadId);

        if (!File.Exists(metaFile))
        {
            throw new StorageException("upload_not_found", "Upload session not found.");
        }

        var session = JsonSerializer.Deserialize<UploadSession>(await File.ReadAllTextAsync(metaFile))
                      ?? throw new StorageException("upload_not_found", "Upload session not found.");

        if (session.UserId != userId)
        {
            throw StorageException.AccessDenied();
        }

        return session;
    }

    private void Cleanup(Guid uploadId)
    {
        foreach (var file in new[] { PartFile(uploadId), MetaFile(uploadId) })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "Temporäre Uploaddatei konnte nicht gelöscht werden: {File}", file);
            }
        }
    }
}
