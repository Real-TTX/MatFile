using MatFile.Web.Controls;
using MatFile.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace MatFile.Web.Services;

/// <summary>Ein Eintrag im Protokoll über alle Speicherorte.</summary>
public record ActivityItem(
    long Id,
    DateTime Date,
    string Action,
    string? User,
    string? Username,
    long? RootId,
    string? Path,
    string? Details,
    string? Ip);

/// <summary>Speicherort für die Filterleiste des Protokolls.</summary>
public record ActivityRoot(long Id, string Name);

/// <summary>Eine Seite des Protokolls samt Gesamtzahl und den Werten für die Filterleiste.</summary>
public record ActivityPage(List<ActivityItem> Items, int Total, List<string> Actions, List<ActivityRoot> Roots);

/// <summary>Ein Protokolleintrag zu einer einzelnen Datei oder einem Ordner.</summary>
public record EntryActivity(string Action, DateTime Date, string? User);

/// <summary>Liest das Protokoll (Tabelle ActivityLog). Geschrieben wird es von den Diensten, die die Aktion ausführen.</summary>
public sealed class ActivityLogService
{
    private readonly MatFileDbContext _db;

    public ActivityLogService(MatFileDbContext db) => _db = db;

    /// <summary>Das Protokoll über alle Speicherorte, neueste zuerst, gefiltert und seitenweise.</summary>
    public async Task<ActivityPage> SearchAsync(
        string? search, string? action, long? rootId, ListQuery query, CancellationToken cancellationToken)
    {
        var entries = _db.ActivityLog.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            entries = entries.Where(a =>
                (a.TargetPath != null && EF.Functions.Like(a.TargetPath, $"%{term}%"))
                || EF.Functions.Like(a.ActionType, $"%{term}%")
                || (a.User != null && EF.Functions.Like(a.User.Username, $"%{term}%")));
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            entries = entries.Where(a => a.ActionType == action);
        }

        if (rootId is { } root)
        {
            entries = entries.Where(a => a.StorageRootId == root);
        }

        var total = await entries.CountAsync(cancellationToken);

        // Neueste zuerst - beim Nachschauen interessiert immer das Letzte.
        var items = await entries
            .OrderByDescending(a => a.CreateDate)
            .ThenByDescending(a => a.Id)
            .Skip(query.Skip)
            .Take(query.NormalizedPageSize)
            .Select(a => new ActivityItem(
                a.Id,
                a.CreateDate,
                a.ActionType,
                a.User != null ? a.User.DisplayName : null,
                a.User != null ? a.User.Username : null,
                a.StorageRootId,
                a.TargetPath,
                a.DetailsJson,
                a.IpAddress))
            .ToListAsync(cancellationToken);

        // Für die Filterleiste: vorhandene Aktionen und Speicherorte mit Einträgen.
        var actions = await _db.ActivityLog.AsNoTracking()
            .Select(a => a.ActionType)
            .Distinct()
            .OrderBy(a => a)
            .ToListAsync(cancellationToken);

        var roots = await _db.StorageRoot.AsNoTracking()
            .OrderBy(r => r.Name)
            .Select(r => new ActivityRoot(r.Id, r.Name))
            .ToListAsync(cancellationToken);

        return new ActivityPage(items, total, actions, roots);
    }

    /// <summary>Die letzten 20 Aktionen zu einem Eintrag. Wer das Protokoll nicht einsehen darf, sieht nur die eigenen.</summary>
    public Task<List<EntryActivity>> ForEntryAsync(long rootId, string path, long userId, bool seeAll)
    {
        var query = _db.ActivityLog.AsNoTracking()
            .Where(a => a.StorageRootId == rootId && a.TargetPath == path);

        if (!seeAll)
        {
            query = query.Where(a => a.UserId == userId);
        }

        return query
            .OrderByDescending(a => a.Id)
            .Take(20)
            .Select(a => new EntryActivity(a.ActionType, a.CreateDate, a.User!.DisplayName))
            .ToListAsync();
    }
}
