using System.IO.Compression;
using System.Text.Json;
using MatFile.Web.Data;
using MatFile.Web.Storage;

namespace MatFile.Web.Services;

/// <summary>
/// Dateioperationen mit Rechteprüfung. Kurze Aktionen laufen sofort,
/// länger dauernde (Kopieren, Verschieben, Löschen, Packen, Entpacken) als Job.
/// </summary>
public sealed class FileOperationService
{
    private const int ProgressChunk = 1;

    private readonly MatFileDbContext _db;
    private readonly StorageRootService _roots;
    private readonly JobService _jobs;
    private readonly ILogger<FileOperationService> _logger;

    public FileOperationService(
        MatFileDbContext db,
        StorageRootService roots,
        JobService jobs,
        ILogger<FileOperationService> logger)
    {
        _db = db;
        _roots = roots;
        _jobs = jobs;
        _logger = logger;
    }

    private record Access(StorageRoot Root, IStorageProvider Provider, RootPermissions Permissions);

    private async Task<Access> RequireAsync(long userId, long rootId, Func<RootPermissions, bool> check)
    {
        var opened = await _roots.OpenAsync(userId, rootId);
        if (opened is not { } value)
        {
            throw new StorageException("root_not_found", "Storage location not found.");
        }

        if (!check(value.Permissions))
        {
            throw StorageException.AccessDenied();
        }

        return new Access(value.Root, value.Provider, value.Permissions);
    }

    // ---------------------------------------------------------------- Sofortige Aktionen

    public async Task<StorageEntry> CreateFolderAsync(long userId, long rootId, string? parentPath, string name)
    {
        var access = await RequireAsync(userId, rootId, p => p.CanWrite);

        if (!PathGuard.IsValidName(name))
        {
            throw StorageException.InvalidName(name);
        }

        var entry = await access.Provider.CreateDirectoryAsync(PathGuard.Combine(parentPath, name.Trim()));
        await LogAsync(userId, "CreateFolder", rootId, entry.Path);
        return entry;
    }

    public async Task<StorageEntry> RenameAsync(long userId, long rootId, string path, string newName)
    {
        var access = await RequireAsync(userId, rootId, p => p.CanWrite);

        var entry = await access.Provider.RenameAsync(path, newName.Trim());
        await LogAsync(userId, "Rename", rootId, entry.Path);
        return entry;
    }

    // ---------------------------------------------------------------- Jobs anlegen

    public async Task<JobTask> EnqueueDeleteAsync(long userId, long rootId, string[] paths)
    {
        await RequireAsync(userId, rootId, p => p.CanDelete);
        return await _jobs.CreateAsync(_db, userId, JobType.Delete, new JobPayload(rootId, paths));
    }

    public async Task<JobTask> EnqueueTransferAsync(
        long userId, long sourceRootId, string[] paths, long targetRootId, string targetPath, bool move, bool overwrite)
    {
        await RequireAsync(userId, sourceRootId, p => move ? p.CanRead && p.CanDelete : p.CanRead);
        await RequireAsync(userId, targetRootId, p => p.CanWrite);

        return await _jobs.CreateAsync(_db, userId, move ? JobType.Move : JobType.Copy,
            new JobPayload(sourceRootId, paths, targetRootId, targetPath, null, overwrite));
    }

    public async Task<JobTask> EnqueueCompressAsync(
        long userId, long rootId, string[] paths, string targetPath, string targetName)
    {
        await RequireAsync(userId, rootId, p => p.CanRead && p.CanWrite);

        if (!PathGuard.IsValidName(targetName))
        {
            throw StorageException.InvalidName(targetName);
        }

        return await _jobs.CreateAsync(_db, userId, JobType.Zip,
            new JobPayload(rootId, paths, rootId, targetPath, targetName));
    }

    public async Task<JobTask> EnqueueExtractAsync(long userId, long rootId, string path, string targetPath)
    {
        await RequireAsync(userId, rootId, p => p.CanRead && p.CanWrite);
        return await _jobs.CreateAsync(_db, userId, JobType.Unzip,
            new JobPayload(rootId, [path], rootId, targetPath));
    }

    // ---------------------------------------------------------------- Jobs ausführen

    public async Task ExecuteAsync(JobTask job, Func<int, int, Task> report, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<JobPayload>(job.PayloadJson ?? "{}")
                      ?? throw new StorageException("invalid_job", "Job payload is missing.");

        switch (job.JobType)
        {
            case JobType.Delete:
                await RunDeleteAsync(job.UserId, payload, report, cancellationToken);
                break;
            case JobType.Copy:
            case JobType.Move:
                await RunTransferAsync(job, payload, job.JobType == JobType.Move, report, cancellationToken);
                break;
            case JobType.Zip:
                job.ResultPath = await RunCompressAsync(job.UserId, payload, report, cancellationToken);
                break;
            case JobType.Unzip:
                job.ResultPath = await RunExtractAsync(job.UserId, payload, report, cancellationToken);
                break;
            default:
                throw new StorageException("unsupported_job", $"Job type {job.JobType} is not supported.");
        }
    }

    private async Task RunDeleteAsync(long userId, JobPayload payload, Func<int, int, Task> report, CancellationToken ct)
    {
        var access = await RequireAsync(userId, payload.SourceRootId, p => p.CanDelete);

        var total = payload.Paths.Length;
        var done = 0;

        foreach (var path in payload.Paths)
        {
            ct.ThrowIfCancellationRequested();
            await access.Provider.DeleteAsync(path, ct);
            await LogAsync(userId, "Delete", payload.SourceRootId, path);
            await report(++done, total);
        }
    }

    private async Task RunTransferAsync(
        JobTask job, JobPayload payload, bool move, Func<int, int, Task> report, CancellationToken ct)
    {
        var source = await RequireAsync(job.UserId, payload.SourceRootId, p => move ? p.CanRead && p.CanDelete : p.CanRead);
        var target = await RequireAsync(job.UserId, payload.TargetRootId, p => p.CanWrite);

        // Erst zählen, damit der Fortschritt stimmt.
        var items = new List<(StorageEntry Entry, string TargetPath)>();

        foreach (var path in payload.Paths)
        {
            ct.ThrowIfCancellationRequested();

            var entry = await source.Provider.GetAsync(path, ct)
                        ?? throw StorageException.NotFound(path);

            var name = entry.Name;
            var destination = PathGuard.Combine(payload.TargetPath, name);

            // In denselben Ordner kopieren erzeugt eine Kopie mit neuem Namen.
            if (!payload.Overwrite)
            {
                destination = await UniquePathAsync(target.Provider, destination, ct);
            }

            if (IsInsideItself(source.Root.Id, entry.Path, target.Root.Id, destination))
            {
                throw new StorageException("target_inside_source", "The target folder is inside the source folder.");
            }

            items.Add((entry, destination));
        }

        var plan = new List<Func<CancellationToken, Task>>();
        var sameRoot = source.Root.Id == target.Root.Id;

        foreach (var (entry, destination) in items)
        {
            if (move && sameRoot)
            {
                var from = entry.Path;
                var to = destination;
                plan.Add(token => source.Provider.MoveAsync(from, to, payload.Overwrite, token));
                continue;
            }

            await BuildCopyPlanAsync(source.Provider, target.Provider, entry, destination, payload.Overwrite, plan, ct);
        }

        var total = Math.Max(1, plan.Count);
        var done = 0;

        foreach (var step in plan)
        {
            ct.ThrowIfCancellationRequested();
            await step(ct);

            done++;
            if (done % ProgressChunk == 0 || done == plan.Count)
            {
                await report(done, total);
            }
        }

        // Beim Verschieben über Speicherorte hinweg wird die Quelle erst nach dem Kopieren entfernt.
        if (move && !sameRoot)
        {
            foreach (var (entry, _) in items)
            {
                await source.Provider.DeleteAsync(entry.Path, ct);
            }
        }

        foreach (var (entry, destination) in items)
        {
            await LogAsync(job.UserId, move ? "Move" : "Copy", target.Root.Id, destination, entry.Path);
        }
    }

    private async Task<string> RunCompressAsync(long userId, JobPayload payload, Func<int, int, Task> report, CancellationToken ct)
    {
        var access = await RequireAsync(userId, payload.SourceRootId, p => p.CanRead && p.CanWrite);

        var name = payload.TargetName!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? payload.TargetName!
            : payload.TargetName + ".zip";

        var archivePath = await UniquePathAsync(access.Provider, PathGuard.Combine(payload.TargetPath, name), ct);

        var files = new List<(string Path, string EntryName)>();

        foreach (var path in payload.Paths)
        {
            ct.ThrowIfCancellationRequested();
            var entry = await access.Provider.GetAsync(path, ct) ?? throw StorageException.NotFound(path);
            await CollectFilesAsync(access.Provider, entry, entry.Name, files, ct);
        }

        var total = Math.Max(1, files.Count);
        var done = 0;

        await using (var output = await access.Provider.OpenWriteAsync(archivePath, overwrite: false, ct))
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
        {
            foreach (var (path, entryName) in files)
            {
                ct.ThrowIfCancellationRequested();

                var zipEntry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                await using var entryStream = zipEntry.Open();
                await using var input = await access.Provider.OpenReadAsync(path, ct);
                await input.CopyToAsync(entryStream, ct);

                await report(++done, total);
            }
        }

        await LogAsync(userId, "Compress", payload.SourceRootId, archivePath);
        return archivePath;
    }

    private async Task<string> RunExtractAsync(long userId, JobPayload payload, Func<int, int, Task> report, CancellationToken ct)
    {
        var access = await RequireAsync(userId, payload.SourceRootId, p => p.CanRead && p.CanWrite);

        var archivePath = payload.Paths[0];
        var archiveEntry = await access.Provider.GetAsync(archivePath, ct) ?? throw StorageException.NotFound(archivePath);

        var folderName = Path.GetFileNameWithoutExtension(archiveEntry.Name);
        var targetRoot = await UniquePathAsync(access.Provider, PathGuard.Combine(payload.TargetPath, folderName), ct);

        await access.Provider.CreateDirectoryAsync(targetRoot, ct);

        await using var stream = await access.Provider.OpenReadAsync(archivePath, ct);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var total = Math.Max(1, archive.Entries.Count);
        var done = 0;

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            // Schutz gegen Zip-Slip: Einträge dürfen das Zielverzeichnis nicht verlassen.
            var relative = PathGuard.Normalize(entry.FullName);
            if (relative.Length == 0 || relative.Split('/').Any(segment => segment == "..") || Path.IsPathRooted(relative))
            {
                _logger.LogWarning("Eintrag im Archiv übersprungen: {Entry}", entry.FullName);
                await report(++done, total);
                continue;
            }

            var destination = PathGuard.Combine(targetRoot, relative);

            if (entry.Length == 0 && entry.FullName.EndsWith('/'))
            {
                await access.Provider.CreateDirectoryAsync(destination, ct);
                await report(++done, total);
                continue;
            }

            await using var source = entry.Open();
            await using var output = await access.Provider.OpenWriteAsync(destination, overwrite: true, ct);
            await source.CopyToAsync(output, ct);

            await report(++done, total);
        }

        await LogAsync(userId, "Extract", payload.SourceRootId, targetRoot, archivePath);
        return targetRoot;
    }

    // ---------------------------------------------------------------- Hilfen

    /// <summary>Baut die Einzelschritte für das Kopieren eines Eintrags (Ordner rekursiv).</summary>
    private async Task BuildCopyPlanAsync(
        IStorageProvider source,
        IStorageProvider target,
        StorageEntry entry,
        string destination,
        bool overwrite,
        List<Func<CancellationToken, Task>> plan,
        CancellationToken ct)
    {
        if (!entry.IsDirectory)
        {
            plan.Add(async token =>
            {
                await using var input = await source.OpenReadAsync(entry.Path, token);
                await using var output = await target.OpenWriteAsync(destination, overwrite, token);
                await input.CopyToAsync(output, token);
            });

            return;
        }

        plan.Add(async token =>
        {
            try
            {
                await target.CreateDirectoryAsync(destination, token);
            }
            catch (StorageException exception) when (exception.Code == "conflict")
            {
                // Zielordner existiert bereits - Inhalte werden hineinkopiert.
            }
        });

        var listing = await source.ListAsync(entry.Path, ct);

        foreach (var child in listing.Entries)
        {
            ct.ThrowIfCancellationRequested();
            await BuildCopyPlanAsync(source, target, child, PathGuard.Combine(destination, child.Name), overwrite, plan, ct);
        }
    }

    private static async Task CollectFilesAsync(
        IStorageProvider provider, StorageEntry entry, string entryName, List<(string, string)> files, CancellationToken ct)
    {
        if (!entry.IsDirectory)
        {
            files.Add((entry.Path, entryName));
            return;
        }

        var listing = await provider.ListAsync(entry.Path, ct);

        foreach (var child in listing.Entries)
        {
            ct.ThrowIfCancellationRequested();
            await CollectFilesAsync(provider, child, $"{entryName}/{child.Name}", files, ct);
        }
    }

    /// <summary>Hängt " (2)", " (3)" an, bis der Pfad frei ist.</summary>
    private static async Task<string> UniquePathAsync(IStorageProvider provider, string path, CancellationToken ct)
    {
        if (await provider.GetAsync(path, ct) is null)
        {
            return path;
        }

        var parent = PathGuard.GetParent(path) ?? string.Empty;
        var name = path[(path.LastIndexOf('/') + 1)..];
        var extension = Path.GetExtension(name);
        var stem = extension.Length > 0 ? name[..^extension.Length] : name;

        for (var i = 2; i < 1000; i++)
        {
            var candidate = PathGuard.Combine(parent, $"{stem} ({i}){extension}");
            if (await provider.GetAsync(candidate, ct) is null)
            {
                return candidate;
            }
        }

        throw StorageException.Conflict(path);
    }

    private static bool IsInsideItself(long sourceRootId, string sourcePath, long targetRootId, string targetPath)
    {
        if (sourceRootId != targetRootId)
        {
            return false;
        }

        var source = PathGuard.Normalize(sourcePath);
        var target = PathGuard.Normalize(targetPath);

        return target == source || target.StartsWith(source + "/", StringComparison.Ordinal);
    }

    private async Task LogAsync(long userId, string action, long rootId, string path, string? source = null)
    {
        _db.ActivityLog.Add(new ActivityLog
        {
            UserId = userId,
            ActionType = action,
            StorageRootId = rootId,
            TargetPath = path,
            DetailsJson = source is null ? null : JsonSerializer.Serialize(new { source }),
            CreateUserId = userId,
            UpdateUserId = userId
        });

        await _db.SaveChangesAsync();
    }
}
