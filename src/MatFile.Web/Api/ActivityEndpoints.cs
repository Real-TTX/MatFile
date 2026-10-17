using MatFile.Web.Controls;
using MatFile.Web.Security;
using MatFile.Web.Services;

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
            ActivityLogService activity,
            CancellationToken cancellationToken) =>
        {
            var query = new ListQuery { Search = search, Page = page ?? 1, PageSize = pageSize ?? ListQuery.DefaultPageSize };

            var result = await activity.SearchAsync(search, action, rootId, query, cancellationToken);

            return Results.Ok(new
            {
                items = result.Items,
                total = result.Total,
                page = query.NormalizedPage,
                pageSize = query.NormalizedPageSize,
                pageCount = Math.Max(1, (int)Math.Ceiling(result.Total / (double)query.NormalizedPageSize)),
                actions = result.Actions,
                roots = result.Roots
            });
        });

        return app;
    }
}
