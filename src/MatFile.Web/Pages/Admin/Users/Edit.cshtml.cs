using System.ComponentModel.DataAnnotations;
using MatFile.Web.Data;
using MatFile.Web.Security;
using MatFile.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatFile.Web.Pages.Admin.Users;

public class EditModel : PageModel
{
    private readonly UserService _users;
    private readonly IStringLocalizer<SharedResource> _text;

    public EditModel(UserService users, IStringLocalizer<SharedResource> text)
    {
        _users = users;
        _text = text;
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public UserInput Input { get; set; } = new();

    public IReadOnlyList<Role> AllRoles { get; private set; } = [];
    public string? ErrorMessage { get; private set; }
    public bool IsNew => Id is null;

    public class UserInput
    {
        [Required(ErrorMessage = "User.Error.UsernameRequired")]
        [StringLength(64)]
        public string Username { get; set; } = string.Empty;

        [StringLength(128)]
        public string DisplayName { get; set; } = string.Empty;

        [EmailAddress(ErrorMessage = "User.Error.EmailInvalid")]
        [StringLength(256)]
        public string? Email { get; set; }

        public bool IsActive { get; set; } = true;

        public string? NewPassword { get; set; }

        public bool MustChangePassword { get; set; } = true;

        public List<long> RoleIds { get; set; } = [];
    }

    public async Task<IActionResult> OnGetAsync()
    {
        AllRoles = await _users.GetAllRolesAsync();

        if (Id is not { } id)
        {
            return Page();
        }

        var user = await _users.GetAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        Input = new UserInput
        {
            Username = user.Username,
            DisplayName = user.DisplayName,
            Email = user.Email,
            IsActive = user.IsActive,
            MustChangePassword = user.MustChangePassword,
            RoleIds = user.UserRoles.Select(ur => ur.RoleId).ToList()
        };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        AllRoles = await _users.GetAllRolesAsync();

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await _users.SaveAsync(
            Id,
            Input.Username,
            Input.DisplayName,
            Input.Email,
            Input.IsActive,
            Input.NewPassword,
            Input.MustChangePassword,
            Input.RoleIds,
            User.GetUserId() ?? 0);

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

        var result = await _users.DeleteAsync(id, User.GetUserId() ?? 0);
        if (!result.Success)
        {
            AllRoles = await _users.GetAllRolesAsync();
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
