using System.Security.Claims;
using MatFile.Web.Security;
using MatFile.Web.Services;

namespace MatFile.Web.Api;

/// <summary>
/// Rekursive Suche: starten, Fortschritt abfragen, abbrechen.
/// Die Treffer holt das Suchfenster stückweise nach, damit lange Läufe früh etwas zeigen.
/// </summary>
public static class SearchEndpoints
{
    public record SearchRequest(long RootId, string? Path, string? Query, bool Content, bool IncludeHidden);

    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/search").RequireAuthorization(MatFilePolicies.UseExplorer);

        group.MapPost("/", async (
            SearchRequest request,
            SearchService search,
            StorageRootService roots,
            ClaimsPrincipal user) =>
        {
            var query = (request.Query ?? string.Empty).Trim();

            if (query.Length < SearchService.MinQueryLength)
            {
                return Results.Json(new { error = "query_too_short" }, statusCode: StatusCodes.Status400BadRequest);
            }

            if (user.GetUserId() is not { } userId)
            {
                return Results.Unauthorized();
            }

            var opened = await roots.OpenAsync(user, request.RootId);
            if (opened is not { } access)
            {
                return Results.Json(new { error = "root_not_found" }, statusCode: StatusCodes.Status404NotFound);
            }

            // Wer den Inhalt durchsuchen will, muss die Dateien auch lesen dürfen.
            if (request.Content && !access.Permissions.CanDownload)
            {
                return Results.Json(new { error = "access_denied" }, statusCode: StatusCodes.Status403Forbidden);
            }

            var id = search.Start(userId, request.RootId, request.Path, query, request.Content, request.IncludeHidden);

            return Results.Ok(new { searchId = id, limit = SearchService.MaxHits });
        });

        group.MapGet("/{id}", (string id, int? from, SearchService search, ClaimsPrincipal user) =>
        {
            if (user.GetUserId() is not { } userId)
            {
                return Results.Unauthorized();
            }

            var snapshot = search.Snapshot(id, userId, from ?? 0);

            return snapshot is null
                ? Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound)
                : Results.Ok(snapshot);
        });

        group.MapPost("/{id}/cancel", (string id, SearchService search, ClaimsPrincipal user) =>
        {
            if (user.GetUserId() is not { } userId)
            {
                return Results.Unauthorized();
            }

            return search.Cancel(id, userId)
                ? Results.NoContent()
                : Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound);
        });

        return app;
    }
}
