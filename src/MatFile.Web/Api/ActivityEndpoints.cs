using MatFile.Web.Controls;
using MatFile.Web.Data;
using MatFile.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace MatFile.Web.Api;

/// <summary>
/// Das Protokoll über alle Speicherorte hinweg - Grundlage der Ansicht im
/// Administrationsbereich. Wer nur einen einzelnen Eintrag sehen will, nimmt
/// weiterhin /api/fs/activity.
/// </summary>
public static class ActivityEndpoints
{
    public static IEndpointRouteBuilder MapActivityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/activity").RequireAuthorization(MatFilePolicies.ViewActivityLog);

        group.MapGet("/", async (
            string? search, string? action, long? rootId, int? page, int? pageSize,
            MatFileDbContext db,
            CancellationToken cancellationToken) =>
        {
            var query = new ListQuery { Search = search, Page = page ?? 1, PageSize = pageSize ?? ListQuery.DefaultPageSize };

            var entries = db.ActivityLog.AsNoTracking();

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
                .Select(a => new
                {
                    a.Id,
                    date = a.CreateDate,
                    action = a.ActionType,
                    user = a.User != null ? a.User.DisplayName : null,
                    username = a.User != null ? a.User.Username : null,
                    rootId = a.StorageRootId,
                    path = a.TargetPath,
                    details = a.DetailsJson,
                    ip = a.IpAddress
                })
                .ToListAsync(cancellationToken);

            // Für die Filterleiste: vorhandene Aktionen und Speicherorte mit Einträgen.
            var actions = await db.ActivityLog.AsNoTracking()
                .Select(a => a.ActionType)
                .Distinct()
                .OrderBy(a => a)
                .ToListAsync(cancellationToken);

            var roots = await db.StorageRoot.AsNoTracking()
                .OrderBy(r => r.Name)
                .Select(r => new { r.Id, r.Name })
                .ToListAsync(cancellationToken);

            return Results.Ok(new
            {
                items,
                total,
                page = query.NormalizedPage,
                pageSize = query.NormalizedPageSize,
                pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)query.NormalizedPageSize)),
                actions,
                roots
            });
        });

        return app;
    }
}
