using System.Text.Json;
using MatFile.Web.Data;
using MatFile.Web.Security;
using MatFile.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatFile.Web.Pages;

/// <summary>Der Explorer. Die Oberfläche wird hier gerahmt, die Interaktion läuft über /api.</summary>
public class IndexModel : PageModel
{
    private readonly IStringLocalizer<SharedResource> _text;
    private readonly AppInfo _appInfo;

    public IndexModel(IStringLocalizer<SharedResource> text, AppInfo appInfo)
    {
        _text = text;
        _appInfo = appInfo;
    }

    /// <summary>Übersetzungen für das JavaScript des Explorers.</summary>
    public string TextBundleJson { get; private set; } = "{}";

    public string Version => _appInfo.Version;

    public string Channel => _appInfo.Channel;

    public IActionResult OnGet()
    {
        string[] keys =
        [
            "Explorer.Roots", "Explorer.Bookmarks", "Explorer.NoRoots",
            "Explorer.AllLocations", "Explorer.SpaceFree", "Explorer.SpaceUnknown",
            "Explorer.EmptyFolder", "Explorer.Loading", "Explorer.Name", "Explorer.Size",
            "Explorer.Modified", "Explorer.Type", "Explorer.Folder", "Explorer.File",
            "Explorer.Items", "Explorer.Selected", "Explorer.NewTab", "Explorer.OpenInNewTab",
            "Explorer.CloseTab",
            "Explorer.Pin", "Explorer.Unpin", "Explorer.Back", "Explorer.Forward", "Explorer.Up",
            "Explorer.Refresh", "Explorer.Search", "Explorer.ViewList", "Explorer.ViewGrid",
            "Explorer.ShowHidden", "Explorer.AddBookmark", "Explorer.RemoveBookmark",
            "Explorer.BookmarkAdded",
            "Explorer.Download", "Explorer.Open", "Explorer.CopyPath", "Explorer.Copied",
            "Explorer.Properties", "Explorer.SelectAll", "Explorer.ClearSelection",
            "Explorer.Path", "Explorer.Sidebar", "Explorer.SidebarTree", "Explorer.SidebarWidth",
            "Explorer.Expand",

            // Übersicht der Tastenkürzel
            "Explorer.Keys.Title", "Explorer.Keys.MoveSelection", "Explorer.Keys.ExtendSelection",
            "Explorer.Keys.PreviewNavigate", "Explorer.Keys.Indent", "Explorer.Keys.Shift",
            "Explorer.Keys.Backspace", "Explorer.Keys.Delete", "Explorer.Keys.Ctrl",
            "Explorer.Keys.Navigation", "Explorer.Keys.Finding", "Explorer.Keys.Viewing",
            "Explorer.Keys.Help",

            // Zugriffsrechte im Eigenschaftenblatt
            "Explorer.PermTitle", "Explorer.PermOwner", "Explorer.PermGroup", "Explorer.PermOthers",
            "Explorer.PermRead", "Explorer.PermWrite", "Explorer.PermExecute", "Explorer.PermMode",
            "Explorer.ErrorPermissionsUnsupported", "Explorer.ErrorInvalidMode",

            "Explorer.Close", "Explorer.Error", "Explorer.ErrorNotFound", "Explorer.ErrorAccess",
            "Explorer.ErrorUnavailable",

            // Dateioperationen
            "Explorer.Upload", "Explorer.NewFolder", "Explorer.NewFolderName",
            "Explorer.NewFile", "Explorer.NewFileName", "Explorer.ActionCreateFile", "Explorer.Rename",
            "Explorer.Delete", "Explorer.Cut", "Explorer.Copy", "Explorer.Paste",
            "Explorer.Compress", "Explorer.Extract", "Explorer.DownloadZip",
            "Explorer.Save", "Explorer.Cancel", "Explorer.Overwrite", "Explorer.OverwriteQuestion",
            "Explorer.DeleteQuestionOne", "Explorer.DeleteQuestionMany",
            "Explorer.CutDone", "Explorer.CopyDone", "Explorer.SelectedWithSize",
            "Explorer.Transfers", "Explorer.UploadFailed", "Explorer.JobFailed",
            "Explorer.JobCopy", "Explorer.JobMove", "Explorer.JobDelete", "Explorer.JobZip",
            "Explorer.JobUnzip", "Explorer.JobUpload",
            "Explorer.StateCompleted", "Explorer.StateFailed", "Explorer.StateCancelled",
            "Explorer.ErrorConflict", "Explorer.ErrorAccessDenied", "Explorer.ErrorReadOnly", "Explorer.ErrorInvalidName",
            "Explorer.ErrorInvalidPath", "Explorer.ErrorTargetInsideSource", "Explorer.ErrorRootNotFound",

            // Vorschau und Detailbereich
            "Explorer.Preview", "Explorer.NotPreviewable", "Explorer.Truncated",
            "Explorer.Details", "Explorer.Activity", "Explorer.NoActivity", "Explorer.NoSelection",
            "Explorer.ActionLogin", "Explorer.ActionLogout", "Explorer.ActionPasswordChanged",
            "Explorer.ActionCreateFolder", "Explorer.ActionRename", "Explorer.ActionDelete",
            "Explorer.ActionCopy", "Explorer.ActionMove", "Explorer.ActionCompress", "Explorer.ActionExtract",
            "Explorer.ActionEdit", "Explorer.ActionPermissions",
            "Explorer.ActionLoginMatIdentity", "Explorer.ActionLogoutMatIdentity", "Explorer.ActionLinkedMatIdentity", "Explorer.ActionUserCreatedMatIdentity",
            "Explorer.ActionSettingsChanged", "Explorer.ActionJobsCancelled",

            // Texteditor
            "Explorer.Edit", "Explorer.Revert", "Explorer.UnsavedTitle", "Explorer.UnsavedQuestion",
            "Explorer.Discard", "Explorer.ErrorFileTooLarge", "Explorer.ErrorUploadTooLarge",
            "Explorer.ErrorNotEditable",
            "Explorer.ErrorModifiedElsewhere",

            // Verbindungen
            "Explorer.Online", "Explorer.Offline", "Explorer.NotAvailable",

            // Speicherorte anlegen und pflegen
            "Explorer.More", "Explorer.Manage", "Explorer.AddFolder", "Explorer.AddConnection",
            "Explorer.EditLocation", "Explorer.RemoveLocation", "Explorer.RemoveLocationQuestion",
            "Explorer.LocationSaved", "Explorer.LocationRemoved", "Explorer.LocationHint",
            "Explorer.TypeLocal", "Explorer.Server", "Explorer.Port", "Explorer.PortHint",
            "Explorer.Address", "Explorer.Share", "Explorer.ShareHint", "Explorer.User", "Explorer.Password",
            "Explorer.PasswordStored", "Explorer.UseTls", "Explorer.Passive",
            "Explorer.TestConnection", "Explorer.TestOk", "Explorer.NameRequired",
            "Explorer.PathHint", "Explorer.PathHintRemote",
            "Explorer.NoRootsAdmin", "Explorer.AddLocation",
            "Explorer.SectionTarget", "Explorer.SectionLogin", "Explorer.Testing",
            "Explorer.Browse", "Explorer.ChooseFolder", "Explorer.ChooseShare", "Explorer.Apply",
            "Explorer.Advanced", "Explorer.SortOrder", "Explorer.SortOrderHint", "Explorer.Enabled",
            "Explorer.ErrorNotReachable", "Explorer.ErrorConnectionFailed", "Explorer.ErrorShareMissing",
            "Explorer.ErrorShareLevel",

            // Rekursive Suche
            "Explorer.SearchRecursive", "Explorer.SearchContent",
            "Explorer.SearchTab", "Explorer.SearchTerm",
            "Explorer.SearchScope", "Explorer.SearchFolder", "Explorer.SearchWholeLocation",
            "Explorer.SearchStart", "Explorer.SearchNothingYet", "Explorer.SearchPlaceholder",
            "Explorer.SearchRunning", "Explorer.SearchDone", "Explorer.SearchCancelled",
            "Explorer.SearchProgress", "Explorer.SearchHits", "Explorer.SearchNoHits",
            "Explorer.SearchTruncated", "Explorer.SearchQueryTooShort", "Explorer.ErrorQueryTooShort",
            "Explorer.ShowInFolder",

            // Menüleiste
            "Explorer.MenuBar", "Explorer.MenuFile", "Explorer.MenuEdit", "Explorer.MenuView",
            "Explorer.MenuSettings", "Explorer.Language", "Explorer.ThemeSystem",
            "Explorer.ThemeLight", "Explorer.ThemeDark", "Explorer.ChangePassword", "Explorer.Logout",
            "Explorer.NoExplorerAccess",

            // Verwaltungsdialoge - dieselben Bezeichnungen wie früher im Adminbereich
            "Admin.Title", "Admin.Users", "Admin.Roles", "Admin.Roots",
            "Activity.Title", "Activity.When", "Activity.Who", "Activity.What", "Activity.Where",
            "Activity.AllActions", "Activity.AllRoots", "Activity.Empty",
            "System.Title", "System.Hint", "System.MinPasswordLength", "System.MinPasswordLengthHint",
            "System.SessionLifetime", "System.SessionLifetimeHint", "System.MaxFailedAttempts",
            "System.MaxFailedAttemptsHint", "System.LockoutMinutes", "System.LockoutMinutesHint",
            "System.AttemptsPerMinute", "System.AttemptsPerMinuteHint",
            "System.MaxUpload", "System.MaxUploadHint", "System.Security",
            "System.SecureCookie", "System.FirstLogin", "System.RestartHint",
            "System.ConfigFile", "System.Version",
            "Common.Save", "Common.Back", "Common.Delete", "Common.New", "Common.Edit",
            "Common.Search", "Common.Never", "Common.Saved", "Pagination.Summary",
            "User.Username", "User.DisplayName", "User.Email", "User.Roles", "User.Status",
            "User.Active", "User.Inactive", "User.Locked", "User.Unlock", "User.Unlocked",
            "User.LastLogin", "User.Password", "User.PasswordKeep", "User.IsActive",
            "User.MustChangePassword", "User.Empty", "User.New.Title", "User.Edit.Title",
            "Role.Name", "Role.Description", "Role.Permissions", "Role.UserCount", "Role.System",
            "Role.SystemHint", "Role.Empty", "Role.New.Title", "Role.Edit.Title",
            "Root.Name", "Root.Type", "Root.Path", "Root.Status", "Root.Available", "Root.Missing",
            "Root.Disabled", "Root.Roles", "Root.Access", "Root.AccessHint", "Root.Empty",
            "Access.CanRead", "Access.CanWrite", "Access.CanDelete",
            "Access.CanUpload", "Access.CanDownload"
        ];

        // Explorer-Schlüssel behalten ihre kurze Form, alle anderen bleiben wie sie sind.
        var bundle = keys.ToDictionary(
            key => key.StartsWith("Explorer.", StringComparison.Ordinal) ? key["Explorer.".Length..] : key,
            key => _text[key].Value);

        TextBundleJson = JsonSerializer.Serialize(bundle);
        return Page();
    }
}
