using System.ComponentModel.DataAnnotations;
using MatFile.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatFile.Web.Pages;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly AuthService _auth;

    public LoginModel(AuthService auth) => _auth = auth;

    [BindProperty]
    public LoginInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; private set; }

    public class LoginInput
    {
        [Required(ErrorMessage = "Login.UsernameRequired")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "Login.PasswordRequired")]
        public string Password { get; set; } = string.Empty;
    }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return Redirect(SafeReturnUrl());
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await _auth.SignInAsync(HttpContext, Input.Username.Trim(), Input.Password);
        if (!result.Success)
        {
            ErrorMessage = result.Error;
            return Page();
        }

        if (result.MustChangePassword)
        {
            return RedirectToPage("/Account/ChangePassword");
        }

        return Redirect(SafeReturnUrl());
    }

    private string SafeReturnUrl()
        => !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";
}
