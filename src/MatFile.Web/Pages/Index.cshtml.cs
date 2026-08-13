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
            "Explorer.ErrorUnavailable",

            // Dateioperationen
            "Explorer.Upload", "Explorer.NewFolder", "Explorer.NewFolderName", "Explorer.Rename",
            "Explorer.Delete", "Explorer.Cut", "Explorer.Copy", "Explorer.Paste",
            "Explorer.Compress", "Explorer.Extract", "Explorer.DownloadZip",
            "Explorer.Save", "Explorer.Cancel", "Explorer.Overwrite", "Explorer.OverwriteQuestion",
            "Explorer.DeleteQuestionOne", "Explorer.DeleteQuestionMany",
            "Explorer.CutDone", "Explorer.CopyDone", "Explorer.SelectedWithSize",
            "Explorer.Transfers", "Explorer.UploadFailed", "Explorer.JobFailed",
            "Explorer.JobCopy", "Explorer.JobMove", "Explorer.JobDelete", "Explorer.JobZip",
            "Explorer.JobUnzip", "Explorer.JobUpload",
            "Explorer.StateCompleted", "Explorer.StateFailed", "Explorer.StateCancelled",
            "Explorer.ErrorConflict", "Explorer.ErrorAccessDenied", "Explorer.ErrorInvalidName",
            "Explorer.ErrorInvalidPath", "Explorer.ErrorTargetInsideSource", "Explorer.ErrorRootNotFound",

            // Vorschau und Detailbereich
            "Explorer.Preview", "Explorer.NotPreviewable", "Explorer.Truncated",
            "Explorer.Details", "Explorer.Activity", "Explorer.NoActivity", "Explorer.NoSelection",
            "Explorer.ActionLogin", "Explorer.ActionLogout", "Explorer.ActionPasswordChanged",
            "Explorer.ActionCreateFolder", "Explorer.ActionRename", "Explorer.ActionDelete",
            "Explorer.ActionCopy", "Explorer.ActionMove", "Explorer.ActionCompress", "Explorer.ActionExtract",

            // Verbindungen
            "Explorer.Connections", "Explorer.Online", "Explorer.Offline",

            // Speicherorte anlegen und pflegen
            "Explorer.More", "Explorer.Manage", "Explorer.AddFolder", "Explorer.AddConnection",
            "Explorer.EditLocation", "Explorer.RemoveLocation", "Explorer.RemoveLocationQuestion",
            "Explorer.LocationSaved", "Explorer.LocationRemoved", "Explorer.LocationHint",
            "Explorer.TypeLocal", "Explorer.Server", "Explorer.Port", "Explorer.PortHint",
            "Explorer.Address", "Explorer.Share", "Explorer.User", "Explorer.Password",
            "Explorer.PasswordStored", "Explorer.UseTls", "Explorer.Passive",
            "Explorer.TestConnection", "Explorer.TestOk", "Explorer.NameRequired", "Explorer.PathHint",
            "Explorer.NoRootsAdmin", "Explorer.NoLocationsYet",
            "Explorer.TypeLocalSub", "Explorer.TypeSmbSub", "Explorer.TypeSftpSub",
            "Explorer.TypeFtpSub", "Explorer.TypeWebDavSub",
            "Explorer.SectionType", "Explorer.SectionGeneral", "Explorer.SectionTarget",
            "Explorer.SectionLogin", "Explorer.Icon", "Explorer.Testing",

            // Menüleiste
            "Explorer.MenuBar", "Explorer.MenuFile", "Explorer.MenuEdit", "Explorer.MenuView",
            "Explorer.MenuSettings", "Explorer.Language", "Explorer.ThemeSystem",
            "Explorer.ThemeLight", "Explorer.ThemeDark", "Explorer.ChangePassword", "Explorer.Logout"
        ];

        var bundle = keys.ToDictionary(
            key => key["Explorer.".Length..],
            key => _text[key].Value);

        TextBundleJson = JsonSerializer.Serialize(bundle);
        return Page();
    }
}
