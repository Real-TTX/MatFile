using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using MatFile.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace MatFile.Web.Services;

/// <summary>Auftragsdaten eines Hintergrundjobs.</summary>
public record JobPayload(
    long SourceRootId,
    string[] Paths,
    long TargetRootId = 0,
    string TargetPath = "",
    string? TargetName = null,
    bool Overwrite = false);

public record JobView(
    string Token,
    string JobType,
    string Status,
    int Progress,
    string? ResultPath,
    string? Error,
    DateTime CreateDate,
    DateTime? FinishedDate);

/// <summary>
/// Warteschlange für länger laufende Dateioperationen. Die Jobs stehen in der Tabelle
/// JobTask, der <see cref="JobRunner"/> arbeitet sie nacheinander ab.
/// </summary>
public sealed class JobService
{
    private readonly Channel<long> _queue = Channel.CreateUnbounded<long>();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _running = new();

    public ChannelReader<long> Reader => _queue.Reader;

    public void Enqueue(long jobId) => _queue.Writer.TryWrite(jobId);

    public CancellationTokenSource Track(long jobId, CancellationToken linked)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(linked);
        _running[jobId] = source;
        return source;
    }

    public void Release(long jobId)
    {
        if (_running.TryRemove(jobId, out var source))
        {
            source.Dispose();
        }
    }

    public bool Cancel(long jobId)
    {
        if (!_running.TryGetValue(jobId, out var source))
        {
            return false;
        }

        source.Cancel();
        return true;
    }

    /// <summary>Legt einen Job an und stellt ihn in die Warteschlange.</summary>
    public async Task<JobTask> CreateAsync(MatFileDbContext db, long userId, JobType type, JobPayload payload)
    {
        var job = new JobTask
        {
            Token = Guid.NewGuid(),
            UserId = userId,
            JobType = type,
            Status = JobStatus.Pending,
            ProgressPercent = 0,
            PayloadJson = JsonSerializer.Serialize(payload),
            CreateUserId = userId,
            UpdateUserId = userId
        };

        db.JobTask.Add(job);
        await db.SaveChangesAsync();

        Enqueue(job.Id);
        return job;
    }

    public static JobView ToView(JobTask job) => new(
        job.Token.ToString(),
        job.JobType.ToString(),
        job.Status.ToString(),
        job.ProgressPercent,
        job.ResultPath,
        job.ErrorMessage,
        job.CreateDate,
        job.FinishedDate);

    /// <summary>Jobs des Benutzers, neueste zuerst.</summary>
    public static Task<List<JobTask>> RecentAsync(MatFileDbContext db, long userId, int take = 20)
        => db.JobTask.AsNoTracking()
            .Where(j => j.UserId == userId)
            .OrderByDescending(j => j.Id)
            .Take(take)
            .ToListAsync();
}
