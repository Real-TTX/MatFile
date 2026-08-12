using MatFile.Web.Data;
using MatFile.Web.Storage;
using Microsoft.EntityFrameworkCore;

namespace MatFile.Web.Services;

/// <summary>Arbeitet die Jobs aus der Warteschlange nacheinander ab.</summary>
public sealed class JobRunner : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly JobService _jobs;
    private readonly ILogger<JobRunner> _logger;

    public JobRunner(IServiceScopeFactory scopes, JobService jobs, ILogger<JobRunner> logger)
    {
        _scopes = scopes;
        _jobs = jobs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        await foreach (var jobId in _jobs.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunAsync(jobId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Job {JobId} konnte nicht ausgeführt werden", jobId);
            }
        }
    }

    /// <summary>
    /// Nach einem Neustart: laufende Jobs gelten als abgebrochen, wartende kommen zurück in die Schlange.
    /// </summary>
    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatFileDbContext>();

        var open = await db.JobTask
            .Where(j => j.Status == JobStatus.Running || j.Status == JobStatus.Pending)
            .ToListAsync(cancellationToken);

        foreach (var job in open)
        {
            if (job.Status == JobStatus.Running)
            {
                job.Status = JobStatus.Failed;
                job.ErrorMessage = "interrupted";
                job.FinishedDate = DateTime.UtcNow;
            }
            else
            {
                _jobs.Enqueue(job.Id);
            }
        }

        if (open.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task RunAsync(long jobId, CancellationToken stoppingToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatFileDbContext>();
        var operations = scope.ServiceProvider.GetRequiredService<FileOperationService>();

        var job = await db.JobTask.FirstOrDefaultAsync(j => j.Id == jobId, stoppingToken);
        if (job is null || job.Status != JobStatus.Pending)
        {
            return;
        }

        using var cancellation = _jobs.Track(jobId, stoppingToken);

        job.Status = JobStatus.Running;
        job.StartedDate = DateTime.UtcNow;
        await db.SaveChangesAsync(stoppingToken);

        var lastReport = DateTime.UtcNow;

        async Task Report(int done, int total)
        {
            var percent = total <= 0 ? 100 : (int)Math.Round(done * 100.0 / total);
            percent = Math.Clamp(percent, 0, 100);

            if (percent == job.ProgressPercent || DateTime.UtcNow - lastReport < TimeSpan.FromMilliseconds(400))
            {
                return;
            }

            job.ProgressPercent = percent;
            lastReport = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }

        try
        {
            await operations.ExecuteAsync(job, Report, cancellation.Token);

            job.Status = JobStatus.Completed;
            job.ProgressPercent = 100;
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
        }
        catch (StorageException exception)
        {
            job.Status = JobStatus.Failed;
            job.ErrorMessage = exception.Code;
            _logger.LogWarning("Job {JobId} fehlgeschlagen: {Code}", jobId, exception.Code);
        }
        catch (Exception exception)
        {
            job.Status = JobStatus.Failed;
            job.ErrorMessage = "error";
            _logger.LogError(exception, "Job {JobId} fehlgeschlagen", jobId);
        }
        finally
        {
            job.FinishedDate = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            _jobs.Release(jobId);
        }
    }
}
