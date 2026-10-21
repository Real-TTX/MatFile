using System.ComponentModel.DataAnnotations;
using System.Globalization;
using MatFile.Web.Services;
using MatPlatform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatFile.Web.Pages;

/// <summary>
/// Anmeldung mit Benutzername und Kennwort. Mit MatIdentity (MATIDENTITY_AUTHORITY) geht ein Aufruf gleich weiter zu MatIdentity; das lokale
/// Formular bleibt mit <c>?local=1</c> erreichbar (der Weg hinein, wenn MatIdentity nicht da ist). <c>?signedOut=1</c>: zurück von der Abmeldung,
/// keine automatische Anmeldung. <c>?ssoError=1</c>: die Anmeldung bei MatIdentity ging schief. <c>?denied=1</c>: MatFile hat die Person abgelehnt.
/// <c>?link=1</c>: die Person von MatIdentity passt zu einem noch nicht verknüpften Konto; erst die Anmeldung genau dieses Kontos mit seinem
/// Kennwort verknüpft es (<see cref="MatIdentityPendingLink"/>, <see cref="UserService.LinkExternalAsync"/>), ein anderes Konto verknüpft nichts,
/// Abbrechen löscht die wartende Verknüpfung.
/// </summary>
[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly AuthService _auth;
    private readonly UserService _users;
    private readonly MatPlatformSettings _platform;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(AuthService auth, UserService users, MatPlatformSettings platform, ILogger<LoginModel> logger)
    {
        _auth = auth;
        _users = users;
        _platform = platform;
        _logger = logger;
    }

    [BindProperty]
    public LoginInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; private set; }

    /// <summary><c>?local=1</c>: das lokale Formular, auch wenn MatIdentity eingerichtet ist.</summary>
    public bool Local { get; private set; }

    /// <summary><c>?signedOut=1</c>: zurück von der Abmeldung, keine automatische Anmeldung.</summary>
    public bool SignedOut { get; private set; }

    /// <summary><c>?ssoError=1</c>: die Anmeldung bei MatIdentity ist fehlgeschlagen oder MatIdentity ist nicht erreichbar.</summary>
    public bool SsoError { get; private set; }

    /// <summary><c>?denied=1</c>: MatIdentity hat die Person angemeldet, MatFile hat sie abgelehnt (Konto deaktiviert, mehrdeutig).</summary>
    public bool Denied { get; private set; }

    /// <summary>Die wartende Verknüpfung mit einem Konto dieser Adresse, wenn MatIdentity die Person zum Beweis hierher geschickt hat.</summary>
    public MatIdentityPendingLink? PendingLink { get; private set; }

    /// <summary><c>?link=1</c> ohne wartende Verknüpfung: abgelaufen (10 Minuten) oder abgebrochen.</summary>
    public bool LinkExpired { get; private set; }

    /// <summary>Ist die Anmeldung über MatIdentity eingerichtet?</summary>
    public bool IdentityEnabled => _platform.IdentityEnabled;

    /// <summary>Die Adresse, die die Anmeldung bei MatIdentity beginnt (mit derselben Rücksprungadresse).</summary>
    public string IdentitySignInUrl => string.IsNullOrWhiteSpace(ReturnUrl)
        ? "/Login"
        : "/Login?returnUrl=" + Uri.EscapeDataString(ReturnUrl);

    public class LoginInput
    {
        [Required(ErrorMessage = "Login.UsernameRequired")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "Login.PasswordRequired")]
        public string Password { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        Prepare();

        if (User.Identity?.IsAuthenticated == true)
        {
            return Redirect(SafeReturnUrl());
        }

        // Automatische Anmeldung: gleich weiter zu MatIdentity, kein lokales Formular im Weg.
        if (IdentityEnabled && !Local && !SignedOut && !SsoError && !Denied)
        {
            try
            {
                await HttpContext.ChallengeAsync(MatIdentitySignIn.Scheme, new AuthenticationProperties { RedirectUri = SafeReturnUrl() });
                return new EmptyResult();
            }
            catch (InvalidOperationException exception)
            {
                // MatIdentity ist nicht erreichbar (die Konfiguration lässt sich nicht lesen): das lokale Formular ist der Weg hinein.
                _logger.LogWarning(exception, "MatIdentity ist nicht erreichbar, die lokale Anmeldung wird gezeigt");
                Local = true;
                SsoError = true;
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // Ein abgeschicktes Formular ist die lokale Anmeldung; bei einem Fehler zeigt es sich wieder.
        Prepare();
        Local = true;

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await _auth.CheckPasswordAsync(HttpContext, Input.Username.Trim(), Input.Password);
        if (!result.Success)
        {
            ErrorMessage = result.Error;
            return Page();
        }

        // Eine wartende Verknüpfung gilt nur für genau das Konto, das gepasst hat; gelöscht wird sie in jedem Fall.
        string? identityToken = null;
        string? identitySessionId = null;
        if (MatIdentityPendingLink.TakeFor(HttpContext, result.UserId.ToString(CultureInfo.InvariantCulture)) is { } pending)
        {
            var link = await _users.LinkExternalAsync(
                result.UserId,
                new ExternalIdentity(pending.Subject, pending.Email, pending.Name, pending.PreferredUserName, pending.Role),
                HttpContext.RequestAborted);

            if (link.Success)
            {
                identityToken = pending.IdToken;
                identitySessionId = pending.SessionId;
            }
            else
            {
                _logger.LogWarning("Konto {UserId} hat sich angemeldet, wurde aber nicht mit MatIdentity verknüpft: {Error}", result.UserId, link.Error);
            }
        }

        await _auth.StartSessionAsync(HttpContext, result.UserId, identityToken, identitySessionId: identitySessionId);

        if (result.MustChangePassword)
        {
            return RedirectToPage("/Account/ChangePassword");
        }

        return Redirect(SafeReturnUrl());
    }

    /// <summary>Abbrechen auf <c>?link=1</c>: das Konto wird nicht verknüpft, die wartende Verknüpfung gelöscht.</summary>
    public IActionResult OnPostCancelLink()
    {
        MatIdentityPendingLink.Clear(HttpContext);
        return Redirect("/Login?local=1");
    }

    private void Prepare()
    {
        Local = QueryFlag("local");
        SignedOut = QueryFlag("signedOut");
        SsoError = QueryFlag("ssoError");
        Denied = QueryFlag("denied");
        PendingLink = MatIdentityPendingLink.Peek(HttpContext);
        LinkExpired = PendingLink is null && QueryFlag("link");
    }

    /// <summary>Ein Schalter in der Adresse: <c>1</c> oder <c>true</c>.</summary>
    private bool QueryFlag(string name)
        => Request.Query[name].ToString() is "1" or "true" or "True";

    private string SafeReturnUrl()
        => !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";
}
