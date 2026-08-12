using MatFile.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatFile.Web.Pages;

public class LogoutModel : PageModel
{
    private readonly AuthService _auth;

    public LogoutModel(AuthService auth) => _auth = auth;

    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        await _auth.SignOutAsync(HttpContext);
        return RedirectToPage("/Login");
    }
}
