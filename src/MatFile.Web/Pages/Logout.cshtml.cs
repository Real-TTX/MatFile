using MatFile.Web.Services;
using MatPlatform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatFile.Web.Pages;

/// <summary>
/// Abmeldung (nur als POST). Begann die Sitzung über MatIdentity (oder wurde sie bei der Anmeldung verknüpft), endet sie auch dort;
/// mit MatIdentity landet die Person danach auf <c>/Login?signedOut=1</c>, damit sie nicht gleich wieder angemeldet wird.
/// </summary>
public class LogoutModel : PageModel
{
    private readonly AuthService _auth;
    private readonly MatPlatformSettings _platform;

    public LogoutModel(AuthService auth, MatPlatformSettings platform)
    {
        _auth = auth;
        _platform = platform;
    }

    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        var identityToken = await _auth.SignOutAsync(HttpContext);

        if (!_platform.IdentityEnabled)
        {
            return RedirectToPage("/Login");
        }

        // Dann auch bei MatIdentity; die schickt den Browser über /signout-callback-oidc zurück auf /Login?signedOut=1.
        return identityToken is not null
            ? MatIdentitySignIn.SignOutAtIdentity(HttpContext, identityToken)
            : Redirect("/Login?signedOut=1");
    }
}
