using System.Security.Claims;
using MatFile.Web.Security;
using MatFile.Web.Services;

namespace MatFile.Web.Api;

/// <summary>Zustand des Explorers: Einstellungen, Favoriten und angeheftete Tabs.</summary>
public static class ExplorerEndpoints
{
    public record BookmarkRequest(long RootId, string? Path, string? Name);

    public static IEndpointRouteBuilder MapExplorerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization(MatFilePolicies.UseExplorer);

        group.MapGet("/settings", async (ExplorerStateService state, ClaimsPrincipal user) =>
        {
            if (user.GetUserId() is not { } userId)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await state.GetSettingsAsync(userId));
        });

        group.MapPut("/settings", async (ExplorerSettings settings, ExplorerStateService state, ClaimsPrincipal user) =>
        {
            if (user.GetUserId() is not { } userId)
            {
                return Results.Unauthorized();
            }

            await state.SaveSettingsAsync(userId, settings);
            return Results.NoContent();
        });

        group.MapGet("/bookmarks", async (ExplorerStateService state, ClaimsPrincipal user)
            => Results.Ok(await state.GetBookmarksAsync(user)));

        group.MapPost("/bookmarks", async (BookmarkRequest request, ExplorerStateService state, ClaimsPrincipal user) =>
        {
            var bookmark = await state.AddBookmarkAsync(user, request.RootId, request.Path, request.Name);

            return bookmark is null
                ? Results.Json(new { error = "root_not_found" }, statusCode: StatusCodes.Status404NotFound)
                : Results.Ok(bookmark);
        });

        group.MapDelete("/bookmarks/{id:long}", async (long id, ExplorerStateService state, ClaimsPrincipal user)
            => await state.DeleteBookmarkAsync(user, id) ? Results.NoContent() : Results.NotFound());

        group.MapGet("/tabs", async (ExplorerStateService state, ClaimsPrincipal user)
            => Results.Ok(await state.GetTabsAsync(user)));

        group.MapPut("/tabs", async (List<TabDto> tabs, ExplorerStateService state, ClaimsPrincipal user) =>
        {
            await state.SaveTabsAsync(user, tabs);
            return Results.NoContent();
        });

        return app;
    }
}
