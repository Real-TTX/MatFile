using System.ComponentModel.DataAnnotations;
using MatFile.Web.Security;
using MatFile.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatFile.Web.Pages.Account;

public class ChangePasswordModel : PageModel
{
    private readonly AuthService _auth;
    private readonly MatFileConfigStore _config;
    private readonly IStringLocalizer<SharedResource> _text;

    public ChangePasswordModel(AuthService auth, MatFileConfigStore config, IStringLocalizer<SharedResource> text)
    {
        _auth = auth;
        _config = config;
        _text = text;
    }

    [BindProperty]
    public PasswordInput Input { get; set; } = new();

    public string? ErrorMessage { get; private set; }
    public string? SuccessMessage { get; private set; }
    public bool MustChange => User.RequiresPasswordChange();
    public int MinPasswordLength => _config.Settings.MinPasswordLength;

    public class PasswordInput
    {
        [Required(ErrorMessage = "ChangePassword.CurrentRequired")]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "ChangePassword.NewRequired")]
        public string NewPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "ChangePassword.ConfirmRequired")]
        [Compare(nameof(NewPassword), ErrorMessage = "ChangePassword.Mismatch")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await _auth.ChangePasswordAsync(HttpContext, Input.CurrentPassword, Input.NewPassword);
        if (!result.Success)
        {
            ErrorMessage = result.Error;
            return Page();
        }

        Input = new PasswordInput();
        ModelState.Clear();
        SuccessMessage = _text["ChangePassword.Success"];
        return Page();
    }
}
