using System.Security.Claims;
using System.Text.Json;
using MatFile.Web.Data;
using MatFile.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace MatFile.Web.Services;

/// <summary>Anzeigeeinstellungen des Explorers je Benutzer.</summary>
public class ExplorerSettings
{
    public string ViewMode { get; set; } = "list";
    public string SortKey { get; set; } = "name";
    public string SortDir { get; set; } = "asc";
    public bool ShowHidden { get; set; }
    public int SidebarWidth { get; set; } = 260;
}

public record BookmarkDto(long Id, long RootId, string Name, string Path, string? Icon, int SortOrder);

public record TabDto(long RootId, string Path, string Title, int SortOrder, bool IsActive);

/// <summary>
/// Zustand des Explorers: Einstellungen, Favoriten und angeheftete Tabs.
/// Alles hängt am Benutzer und übersteht damit Neuanmeldung und Neustart.
/// </summary>
public sealed class ExplorerStateService
{
    private const string SettingsKey = "Explorer";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly MatFileDbContext _db;
    private readonly StorageRootService _roots;

    public ExplorerStateService(MatFileDbContext db, StorageRootService roots)
    {
        _db = db;
        _roots = roots;
    }

    // ---------------------------------------------------------------- Einstellungen

    public async Task<ExplorerSettings> GetSettingsAsync(long userId)
    {
        var value = await _db.UserSetting.AsNoTracking()
            .Where(s => s.UserId == userId && s.SettingKey == SettingsKey)
            .Select(s => s.SettingValue)
            .FirstOrDefaultAsync();

        if (string.IsNullOrWhiteSpace(value))
        {
            return new ExplorerSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<ExplorerSettings>(value, JsonOptions) ?? new ExplorerSettings();
        }
        catch (JsonException)
        {
            return new ExplorerSettings();
        }
    }

    public async Task SaveSettingsAsync(long userId, ExplorerSettings settings)
    {
        settings.ViewMode = settings.ViewMode == "grid" ? "grid" : "list";
        settings.SortKey = settings.SortKey is "size" or "modified" ? settings.SortKey : "name";
        settings.SortDir = settings.SortDir == "desc" ? "desc" : "asc";
        settings.SidebarWidth = Math.Clamp(settings.SidebarWidth, 180, 520);

        var json = JsonSerializer.Serialize(settings, JsonOptions);

        var entry = await _db.UserSetting
            .FirstOrDefaultAsync(s => s.UserId == userId && s.SettingKey == SettingsKey);

        if (entry is null)
        {
            _db.UserSetting.Add(new UserSetting { UserId = userId, SettingKey = SettingsKey, SettingValue = json });
        }
        else
        {
            entry.SettingValue = json;
        }

        await _db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------- Favoriten

    public async Task<List<BookmarkDto>> GetBookmarksAsync(ClaimsPrincipal user)
    {
        if (user.GetUserId() is not { } userId)
        {
            return [];
        }

        var allowed = (await _roots.GetAccessibleRootsAsync(user)).Select(r => r.Id).ToHashSet();

        var bookmarks = await _db.Bookmark.AsNoTracking()
            .Where(b => b.UserId == userId)
            .OrderBy(b => b.SortOrder).ThenBy(b => b.Name)
            .ToListAsync();

        return bookmarks
            .Where(b => allowed.Contains(b.StorageRootId))
            .Select(b => new BookmarkDto(b.Id, b.StorageRootId, b.Name, b.RelativePath, b.DisplayIcon, b.SortOrder))
            .ToList();
    }

    public async Task<BookmarkDto?> AddBookmarkAsync(ClaimsPrincipal user, long rootId, string? path, string? name)
    {
        if (user.GetUserId() is not { } userId)
        {
            return null;
        }

        var opened = await _roots.OpenAsync(user, rootId);
        if (opened is null)
        {
            return null;
        }

        var relativePath = Storage.PathGuard.Normalize(path);
        var title = string.IsNullOrWhiteSpace(name)
            ? relativePath.Length == 0 ? opened.Value.Root.Name : relativePath[(relativePath.LastIndexOf('/') + 1)..]
            : name.Trim();

        var existing = await _db.Bookmark
            .FirstOrDefaultAsync(b => b.UserId == userId && b.StorageRootId == rootId && b.RelativePath == relativePath);

        if (existing is not null)
        {
            return new BookmarkDto(existing.Id, existing.StorageRootId, existing.Name, existing.RelativePath, existing.DisplayIcon, existing.SortOrder);
        }

        var maxOrder = await _db.Bookmark.Where(b => b.UserId == userId).MaxAsync(b => (int?)b.SortOrder) ?? 0;

        var bookmark = new Bookmark
        {
            UserId = userId,
            StorageRootId = rootId,
            Name = title.Length > 128 ? title[..128] : title,
            RelativePath = relativePath,
            DisplayIcon = relativePath.Length == 0 ? opened.Value.Root.DisplayIcon : "folder",
            SortOrder = maxOrder + 1
        };

        _db.Bookmark.Add(bookmark);
        await _db.SaveChangesAsync();

        return new BookmarkDto(bookmark.Id, bookmark.StorageRootId, bookmark.Name, bookmark.RelativePath, bookmark.DisplayIcon, bookmark.SortOrder);
    }

    public async Task<bool> DeleteBookmarkAsync(ClaimsPrincipal user, long id)
    {
        if (user.GetUserId() is not { } userId)
        {
            return false;
        }

        var bookmark = await _db.Bookmark.FirstOrDefaultAsync(b => b.Id == id && b.UserId == userId);
        if (bookmark is null)
        {
            return false;
        }

        _db.Bookmark.Remove(bookmark);
        await _db.SaveChangesAsync();
        return true;
    }

    // ---------------------------------------------------------------- Angeheftete Tabs

    public async Task<List<TabDto>> GetTabsAsync(ClaimsPrincipal user)
    {
        if (user.GetUserId() is not { } userId)
        {
            return [];
        }

        var allowed = (await _roots.GetAccessibleRootsAsync(user)).Select(r => r.Id).ToHashSet();

        var tabs = await _db.ExplorerTab.AsNoTracking()
            .Where(t => t.UserId == userId && t.IsPersistent)
            .OrderBy(t => t.SortOrder)
            .ToListAsync();

        return tabs
            .Where(t => t.StorageRootId is { } rootId && allowed.Contains(rootId))
            .Select(t => new TabDto(t.StorageRootId!.Value, t.RelativePath, t.Title, t.SortOrder, t.IsActive))
            .ToList();
    }

    /// <summary>Ersetzt die angehefteten Tabs des Benutzers vollständig.</summary>
    public async Task SaveTabsAsync(ClaimsPrincipal user, IReadOnlyList<TabDto> tabs)
    {
        if (user.GetUserId() is not { } userId)
        {
            return;
        }

        var allowed = (await _roots.GetAccessibleRootsAsync(user)).Select(r => r.Id).ToHashSet();

        var existing = await _db.ExplorerTab.Where(t => t.UserId == userId).ToListAsync();
        _db.ExplorerTab.RemoveRange(existing);

        var order = 0;

        foreach (var tab in tabs.Where(t => allowed.Contains(t.RootId)).Take(30))
        {
            _db.ExplorerTab.Add(new ExplorerTab
            {
                UserId = userId,
                StorageRootId = tab.RootId,
                RelativePath = Storage.PathGuard.Normalize(tab.Path),
                Title = string.IsNullOrWhiteSpace(tab.Title) ? "/" : tab.Title.Trim()[..Math.Min(128, tab.Title.Trim().Length)],
                SortOrder = order++,
                IsPersistent = true,
                IsActive = tab.IsActive
            });
        }

        await _db.SaveChangesAsync();
    }
}
