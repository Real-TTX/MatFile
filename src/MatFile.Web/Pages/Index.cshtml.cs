using System.Text.Json;
using MatFile.Web.Data;
using MatFile.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatFile.Web.Pages;

/// <summary>Der Explorer. Die Oberfläche wird hier gerahmt, die Interaktion läuft über /api.</summary>
public class IndexModel : PageModel
{
    private readonly IStringLocalizer<SharedResource> _text;

    public IndexModel(IStringLocalizer<SharedResource> text) => _text = text;

    /// <summary>Übersetzungen für das JavaScript des Explorers.</summary>
    public string TextBundleJson { get; private set; } = "{}";

    public IActionResult OnGet()
    {
        if (!User.HasPermission(Permission.UseExplorer))
        {
            return RedirectToPage("/Admin/Index");
        }

        string[] keys =
        [
            "Explorer.Roots", "Explorer.Bookmarks", "Explorer.NoRoots", "Explorer.NoBookmarks",
            "Explorer.EmptyFolder", "Explorer.Loading", "Explorer.Name", "Explorer.Size",
            "Explorer.Modified", "Explorer.Type", "Explorer.Folder", "Explorer.File",
            "Explorer.Items", "Explorer.Selected", "Explorer.NewTab", "Explorer.CloseTab",
            "Explorer.Pin", "Explorer.Unpin", "Explorer.Back", "Explorer.Forward", "Explorer.Up",
            "Explorer.Refresh", "Explorer.Search", "Explorer.ViewList", "Explorer.ViewGrid",
            "Explorer.ShowHidden", "Explorer.AddBookmark", "Explorer.RemoveBookmark",
            "Explorer.Download", "Explorer.Open", "Explorer.CopyPath", "Explorer.Copied",
            "Explorer.Properties", "Explorer.SelectAll", "Explorer.Path", "Explorer.Sidebar",
            "Explorer.Close", "Explorer.Error", "Explorer.ErrorNotFound", "Explorer.ErrorAccess",
            "Explorer.ErrorUnavailable"
        ];

        var bundle = keys.ToDictionary(
            key => key["Explorer.".Length..],
            key => _text[key].Value);

        TextBundleJson = JsonSerializer.Serialize(bundle);
        return Page();
    }
}
