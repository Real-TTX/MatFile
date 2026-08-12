using System.ComponentModel.DataAnnotations;
using MatFile.Web.Data;
using MatFile.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatFile.Web.Pages.Admin.Roles;

public class EditModel : PageModel
{
    private readonly RoleService _roles;
    private readonly IStringLocalizer<SharedResource> _text;

    public EditModel(RoleService roles, IStringLocalizer<SharedResource> text)
    {
        _roles = roles;
        _text = text;
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public RoleInput Input { get; set; } = new();

    public bool IsNew => Id is null;
    public bool IsSystemRole { get; private set; }
    public string? ErrorMessage { get; private set; }

    public IReadOnlyList<Permission> AllPermissions => RoleService.AllPermissions;

    public string PermissionLabel(Permission permission) => _roles.PermissionLabel(permission);

    public class RoleInput
    {
        [Required(ErrorMessage = "Role.Error.NameRequired")]
        [StringLength(64)]
        public string Name { get; set; } = string.Empty;

        [StringLength(256)]
        public string? Description { get; set; }

        public List<string> Permissions { get; set; } = [];
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is not { } id)
        {
            return Page();
        }

        var role = await _roles.GetAsync(id);
        if (role is null)
        {
            return NotFound();
        }

        IsSystemRole = role.IsSystem;
        Input = new RoleInput
        {
            Name = role.Name,
            Description = role.Description,
            Permissions = RoleService.AllPermissions
                .Where(p => role.Permissions.HasFlag(p))
                .Select(p => p.ToString())
                .ToList()
        };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Id is { } id)
        {
            var role = await _roles.GetAsync(id);
            IsSystemRole = role?.IsSystem ?? false;
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var permissions = Permission.None;
        foreach (var name in Input.Permissions)
        {
            if (Enum.TryParse<Permission>(name, out var permission))
            {
                permissions |= permission;
            }
        }

        var result = await _roles.SaveAsync(Id, Input.Name, Input.Description, permissions);
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

        var result = await _roles.DeleteAsync(id);
        if (!result.Success)
        {
            ErrorMessage = result.Error;
            await OnGetAsync();
            return Page();
        }

        return RedirectToList();
    }

    private IActionResult RedirectToList()
        => !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
            ? Redirect(ReturnUrl)
            : RedirectToPage("./Index");
}
