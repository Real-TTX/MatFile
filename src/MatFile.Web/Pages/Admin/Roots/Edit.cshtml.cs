using System.ComponentModel.DataAnnotations;
using MatFile.Web.Data;
using MatFile.Web.Services;
using MatFile.Web.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatFile.Web.Pages.Admin.Roots;

public class EditModel : PageModel
{
    private readonly StorageRootService _roots;
    private readonly IStringLocalizer<SharedResource> _text;

    public EditModel(StorageRootService roots, IStringLocalizer<SharedResource> text)
    {
        _roots = roots;
        _text = text;
    }

    /// <summary>Auswahl für das Symbol in der Seitenleiste.</summary>
    public static readonly string[] IconChoices = ["folder", "hard-drive", "users", "lock", "shield", "settings"];

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public RootInput Input { get; set; } = new();

    public bool IsNew => Id is null;
    public string? ErrorMessage { get; private set; }
    public string? StatusMessage { get; private set; }
    public bool HasStoredPassword { get; private set; }

    public IEnumerable<StorageType> SupportedTypes => Enum.GetValues<StorageType>();

    public class RootInput
    {
        [Required(ErrorMessage = "Root.Error.NameRequired")]
        [StringLength(128)]
        public string Name { get; set; } = string.Empty;

        public StorageType StorageType { get; set; } = StorageType.Local;

        /// <summary>Bei lokalen Roots Pflicht, bei Freigaben optional (Unterordner auf der Gegenstelle).</summary>
        [StringLength(1024)]
        public string BasePath { get; set; } = string.Empty;

        public string? DisplayIcon { get; set; } = "folder";

        public int SortOrder { get; set; }

        public bool IsEnabled { get; set; } = true;

        public List<RoleAccessInput> Access { get; set; } = [];

        // --- Verbindungsdaten für Freigaben ---
        [StringLength(255)]
        public string? Host { get; set; }

        public int Port { get; set; }

        [StringLength(128)]
        public string? Username { get; set; }

        public string? Password { get; set; }

        /// <summary>Gespeichertes Kennwort entfernen.</summary>
        public bool ClearPassword { get; set; }

        [StringLength(128)]
        public string? Share { get; set; }

        [StringLength(512)]
        public string? Url { get; set; }

        public bool UseTls { get; set; }

        public bool Passive { get; set; } = true;

        public bool IgnoreCertificateErrors { get; set; }

        public ConnectionSettings ToConnection() => new()
        {
            Host = (Host ?? string.Empty).Trim(),
            Port = Port,
            Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
            Share = string.IsNullOrWhiteSpace(Share) ? null : Share.Trim(),
            Url = string.IsNullOrWhiteSpace(Url) ? null : Url.Trim(),
            UseTls = UseTls,
            Passive = Passive,
            IgnoreCertificateErrors = IgnoreCertificateErrors
        };
    }

    public class RoleAccessInput
    {
        public long RoleId { get; set; }
        public string RoleName { get; set; } = string.Empty;
        public bool CanRead { get; set; }
        public bool CanWrite { get; set; }
        public bool CanDelete { get; set; }
        public bool CanUpload { get; set; }
        public bool CanDownload { get; set; }
        public bool CanShare { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var access = await _roots.GetRoleAccessAsync(Id);

        if (Id is not { } id)
        {
            Input.Access = access.Select(ToInput).ToList();
            return Page();
        }

        var root = await _roots.GetAsync(id);
        if (root is null)
        {
            return NotFound();
        }

        var connection = ConnectionSettings.Parse(root.SettingsJson);
        HasStoredPassword = !string.IsNullOrEmpty(root.SecretEncrypted);

        Input = new RootInput
        {
            Name = root.Name,
            StorageType = root.StorageType,
            BasePath = root.BasePath,
            DisplayIcon = root.DisplayIcon ?? "folder",
            SortOrder = root.SortOrder,
            IsEnabled = root.IsEnabled,
            Access = access.Select(ToInput).ToList(),
            Host = connection.Host,
            Port = connection.Port,
            Username = connection.Username,
            Share = connection.Share,
            Url = connection.Url,
            UseTls = connection.UseTls,
            Passive = connection.Passive,
            IgnoreCertificateErrors = connection.IgnoreCertificateErrors
        };

        return Page();
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        await RestoreRoleNamesAsync();

        var result = await _roots.TestConnectionAsync(
            Id, Input.Name, Input.StorageType, Input.BasePath, Input.ToConnection(), Input.Password);

        if (result.Success)
        {
            StatusMessage = _text["Root.TestOk"];
        }
        else
        {
            ErrorMessage = result.Error;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await RestoreRoleNamesAsync();

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var access = Input.Access
            .Select(a => new StorageRootService.RoleAccess(
                a.RoleId,
                a.RoleName,
                new RootPermissions(a.CanRead, a.CanWrite, a.CanDelete, a.CanUpload, a.CanDownload, a.CanShare)))
            .ToList();

        var result = await _roots.SaveAsync(
            Id, Input.Name, Input.StorageType, Input.BasePath,
            Input.DisplayIcon, Input.SortOrder, Input.IsEnabled, access,
            Input.ToConnection(), Input.Password, Input.ClearPassword);

        if (!result.Success)
        {
            ErrorMessage = result.Error;
            return Page();
        }

        return RedirectToList();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (Id is not { } id)
        {
            return RedirectToList();
        }

        var result = await _roots.DeleteAsync(id);
        if (!result.Success)
        {
            ErrorMessage = result.Error;
            await RestoreRoleNamesAsync();
            return Page();
        }

        return RedirectToList();
    }

    /// <summary>Rollennamen werden nicht mitgesendet und nach dem Absenden neu geladen.</summary>
    private async Task RestoreRoleNamesAsync()
    {
        var roles = await _roots.GetRoleAccessAsync(Id);

        foreach (var item in Input.Access)
        {
            item.RoleName = roles.FirstOrDefault(r => r.RoleId == item.RoleId)?.RoleName ?? item.RoleName;
        }

        // Rollen, die zwischenzeitlich dazugekommen sind, ergänzen.
        foreach (var role in roles.Where(r => Input.Access.All(a => a.RoleId != r.RoleId)))
        {
            Input.Access.Add(ToInput(role));
        }
    }

    private static RoleAccessInput ToInput(StorageRootService.RoleAccess access) => new()
    {
        RoleId = access.RoleId,
        RoleName = access.RoleName,
        CanRead = access.Permissions.CanRead,
        CanWrite = access.Permissions.CanWrite,
        CanDelete = access.Permissions.CanDelete,
        CanUpload = access.Permissions.CanUpload,
        CanDownload = access.Permissions.CanDownload,
        CanShare = access.Permissions.CanShare
    };

    private IActionResult RedirectToList()
        => !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
            ? Redirect(ReturnUrl)
            : RedirectToPage("./Index");
}
