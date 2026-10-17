using System.Text.Json;
using MatFile.Web.Data;
using MatFile.Web.Storage;
using Microsoft.EntityFrameworkCore;

namespace MatFile.Web.Services;

/// <summary>Kennzahlen der ganzen Installation (für die Übersicht in Mattiverse, /api/state).</summary>
public record SystemStatus(
    int Roots,
    int ActiveUsers,
    int RunningJobs,
    int FailedJobs24Hours,
    DateTimeOffset? LastActivity);

/// <summary>
/// Erreichbarkeit und Belegung aller aktiven Speicherorte. <see cref="Total"/> und <see cref="Free"/> zählen jedes Dateisystem einmal
/// (Speicherorte auf demselben Volume melden dieselben Zahlen) und sind null, wenn kein Speicherort Auskunft gibt.
/// </summary>
public record StorageSurvey(int Checked, int Offline, long? Total, long? Free, DateTimeOffset Time)
{
    public long? Used => Total is { } total && Free is { } free ? Math.Max(0, total - free) : null;
}

/// <summary>Was der Zustand einer Installation ist (Datenbank, Datenverzeichnis) - schnell, ohne Gegenstellen zu fragen.</summary>
public record SystemHealth(bool DatabaseReady, bool DataWritable, StorageSurvey? LastSurvey);

/// <summary>Die wenigen Einstellungen, die Mattiverse ändern darf; null = bleibt, wie sie ist.</summary>
public record SystemSettingsChange(
    int? SessionLifetimeDays = null,
    int? LoginMaxFailedAttempts = null,
    int? LoginLockoutMinutes = null,
    int? MinPasswordLength = null,
    int? MaxUploadMegabytes = null,
    bool? RequireSecureCookie = null);

/// <summary>Hält die letzte Prüfung der Speicherorte, damit nicht jede Abfrage alle Gegenstellen anspricht.</summary>
public sealed class StorageSurveyCache
{
    public StorageSurvey? Last { get; set; }

    public SemaphoreSlim Gate { get; } = new(1, 1);
}

/// <summary>
/// Zustand, Kennzahlen und Verwaltungsaktionen der ganzen Installation - die Werte, die MatFile über den Plattformvertrag
/// (/api/state, /api/health, /api/configure, /api/actions) an Mattiverse gibt. Die Weboberfläche fragt nur diesen Dienst.
/// </summary>
public sealed class SystemStatusService
{
    /// <summary>So lange gilt eine Prüfung der Speicherorte.</summary>
    public static readonly TimeSpan SurveyLifetime = TimeSpan.FromMinutes(2);

    /// <summary>So lange darf ein einzelner Speicherort für seine Antwort brauchen.</summary>
    public static readonly TimeSpan RootTimeout = TimeSpan.FromSeconds(5);

    private readonly MatFileDbContext _db;
    private readonly MatFilePaths _paths;
    private readonly MatFileConfigStore _config;
    private readonly StorageProviderFactory _providers;
    private readonly JobService _jobs;
    private readonly StorageSurveyCache _cache;
    private readonly ILogger<SystemStatusService> _logger;

    public SystemStatusService(
        MatFileDbContext db,
        MatFilePaths paths,
        MatFileConfigStore config,
        StorageProviderFactory providers,
        JobService jobs,
        StorageSurveyCache cache,
        ILogger<SystemStatusService> logger)
    {
        _db = db;
        _paths = paths;
        _config = config;
        _providers = providers;
        _jobs = jobs;
        _cache = cache;
        _logger = logger;
    }

    // ---------------------------------------------------------------- Kennzahlen

    public async Task<SystemStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var since = DateTime.UtcNow.AddHours(-24);

        var roots = await _db.StorageRoot.CountAsync(r => r.IsEnabled, cancellationToken);
        var users = await _db.User.CountAsync(u => u.IsActive, cancellationToken);
        var running = await _db.JobTask.CountAsync(j => j.Status == JobStatus.Running || j.Status == JobStatus.Pending, cancellationToken);
        var failed = await _db.JobTask.CountAsync(
            j => j.Status == JobStatus.Failed && j.FinishedDate != null && j.FinishedDate >= since, cancellationToken);
        var last = await _db.ActivityLog.OrderByDescending(a => a.Id).Select(a => (DateTime?)a.CreateDate).FirstOrDefaultAsync(cancellationToken);

        return new SystemStatus(roots, users, running, failed, last is { } date ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)) : null);
    }

    /// <summary>Die Prüfung der Speicherorte: die gespeicherte, solange sie gilt, sonst (oder mit <paramref name="fresh"/>) eine neue.</summary>
    public async Task<StorageSurvey> GetStorageAsync(bool fresh, CancellationToken cancellationToken)
    {
        if (!fresh && _cache.Last is { } cached && DateTimeOffset.UtcNow - cached.Time < SurveyLifetime)
        {
            return cached;
        }

        await _cache.Gate.WaitAsync(cancellationToken);
        try
        {
            if (!fresh && _cache.Last is { } again && DateTimeOffset.UtcNow - again.Time < SurveyLifetime)
            {
                return again;
            }

            var survey = await SurveyAsync(cancellationToken);
            _cache.Last = survey;
            return survey;
        }
        finally
        {
            _cache.Gate.Release();
        }
    }

    private async Task<StorageSurvey> SurveyAsync(CancellationToken cancellationToken)
    {
        var roots = await _db.StorageRoot.AsNoTracking().Where(r => r.IsEnabled).ToListAsync(cancellationToken);
        var answers = await Task.WhenAll(roots.Select(root => CheckRootAsync(root, cancellationToken)));

        // Mehrere Speicherorte auf demselben Dateisystem melden dieselben Zahlen: jedes nur einmal zählen.
        var spaces = answers.Where(a => a.Space is not null).Select(a => a.Space!).Distinct().ToList();

        return new StorageSurvey(
            roots.Count,
            answers.Count(a => !a.Available),
            spaces.Count == 0 ? null : spaces.Sum(s => s.Total),
            spaces.Count == 0 ? null : spaces.Sum(s => s.Free),
            DateTimeOffset.UtcNow);
    }

    private async Task<(bool Available, StorageSpace? Space)> CheckRootAsync(StorageRoot root, CancellationToken cancellationToken)
    {
        try
        {
            var provider = _providers.Create(root);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RootTimeout);

            var available = await provider.IsAvailableAsync(timeout.Token).WaitAsync(RootTimeout, cancellationToken);
            if (!available)
            {
                return (false, null);
            }

            var space = provider is IStorageSpace measurable
                ? await measurable.GetSpaceAsync(timeout.Token).WaitAsync(RootTimeout, cancellationToken)
                : null;
            return (true, space);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Eine Gegenstelle, die nicht antwortet, ist nicht erreichbar - kein Fehler der Übersicht.
            _logger.LogDebug(exception, "Speicherort {RootId} antwortet nicht", root.Id);
            return (false, null);
        }
    }

    // ---------------------------------------------------------------- Zustand

    /// <summary>Datenbank und Datenverzeichnis, dazu die letzte Prüfung der Speicherorte (keine neue: das muss schnell gehen).</summary>
    public async Task<SystemHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        bool database;
        try
        {
            database = await _db.Database.CanConnectAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Die Datenbank ist nicht erreichbar");
            database = false;
        }

        return new SystemHealth(database, _paths.CanWrite(), _cache.Last);
    }

    // ---------------------------------------------------------------- Aktionen

    /// <summary>Bricht alle wartenden und laufenden Aufträge aller Benutzer ab und schreibt das ins Protokoll. Antwortet mit der Zahl.</summary>
    public async Task<int> CancelAllJobsAsync(string caller, long? callerUserId, CancellationToken cancellationToken)
    {
        var open = await _db.JobTask
            .Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running)
            .ToListAsync(cancellationToken);

        foreach (var job in open)
        {
            if (job.Status == JobStatus.Pending)
            {
                job.Status = JobStatus.Cancelled;
                job.FinishedDate = DateTime.UtcNow;
            }
            else
            {
                _jobs.Cancel(job.Id);
            }
        }

        if (open.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
            await LogAsync("JobsCancelled", new { caller, count = open.Count }, callerUserId, cancellationToken);
        }

        return open.Count;
    }

    // ---------------------------------------------------------------- Einstellungen

    public MatFileSettings GetSettings() => _config.Load();

    /// <summary>
    /// Übernimmt geänderte Einstellungen in /data/config/matfile.json (in denselben Grenzen wie der Systemdialog) und schreibt ins Protokoll,
    /// wer sie geändert hat. Einstellungen, die hier nicht vorkommen, bleiben stehen.
    /// </summary>
    public async Task ChangeSettingsAsync(SystemSettingsChange change, string caller, long? callerUserId, CancellationToken cancellationToken)
    {
        // Frisch von der Platte lesen: Einstellungen, die hier nicht vorkommen, bleiben stehen.
        var settings = _config.Load();
        var keys = new List<string>();

        if (change.SessionLifetimeDays is { } lifetime)
        {
            settings.SessionLifetimeDays = Math.Clamp(lifetime, 1, 365);
            keys.Add(nameof(settings.SessionLifetimeDays));
        }

        if (change.LoginMaxFailedAttempts is { } attempts)
        {
            settings.LoginMaxFailedAttempts = Math.Clamp(attempts, 1, 50);
            keys.Add(nameof(settings.LoginMaxFailedAttempts));
        }

        if (change.LoginLockoutMinutes is { } lockout)
        {
            settings.LoginLockoutMinutes = Math.Clamp(lockout, 1, 1440);
            keys.Add(nameof(settings.LoginLockoutMinutes));
        }

        if (change.MinPasswordLength is { } length)
        {
            settings.MinPasswordLength = Math.Clamp(length, 4, 128);
            keys.Add(nameof(settings.MinPasswordLength));
        }

        if (change.MaxUploadMegabytes is { } upload)
        {
            // 0 heißt: keine Grenze.
            settings.MaxUploadMegabytes = Math.Clamp(upload, 0, 1_048_576);
            keys.Add(nameof(settings.MaxUploadMegabytes));
        }

        if (change.RequireSecureCookie is { } secure)
        {
            settings.RequireSecureCookie = secure;
            keys.Add(nameof(settings.RequireSecureCookie));
        }

        if (keys.Count == 0)
        {
            return;
        }

        _config.Save(settings);
        await LogAsync("SettingsChanged", new { caller, keys }, callerUserId, cancellationToken);
        _logger.LogInformation("Einstellungen über die Plattform-API geändert von {Caller}: {Keys}", caller, string.Join(", ", keys));
    }

    /// <summary>Das Konto, das zu einer Kennung von MatIdentity gehört (für das Protokoll), oder null.</summary>
    public async Task<long?> FindUserBySubjectAsync(string? subject, CancellationToken cancellationToken)
        => string.IsNullOrEmpty(subject)
            ? null
            : await _db.User.Where(u => u.ExternalSubject == subject).Select(u => (long?)u.Id).FirstOrDefaultAsync(cancellationToken);

    private async Task LogAsync(string action, object details, long? userId, CancellationToken cancellationToken)
    {
        _db.ActivityLog.Add(new ActivityLog
        {
            UserId = userId,
            ActionType = action,
            DetailsJson = JsonSerializer.Serialize(details),
            CreateUserId = userId,
            UpdateUserId = userId
        });

        await _db.SaveChangesAsync(cancellationToken);
    }
}
