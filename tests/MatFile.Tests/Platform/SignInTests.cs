using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatFile.Web.Data;
using MatPlatform;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatFile.Tests.Platform;

/// <summary>Hilfen der Anmeldetests.</summary>
internal static partial class SignIn
{
    public const string SessionCookie = "matfile.session";

    /// <summary>Schickt das lokale Formular von <paramref name="page"/> ab (die Seite hat <paramref name="browser"/> gerade geladen).</summary>
    public static Task<HttpResponseMessage> PostLocalFormAsync(HttpClient browser, string url, string page, string user = "admin", string password = "admin")
        => browser.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryToken(page),
            ["Input.Username"] = user,
            ["Input.Password"] = password,
        }));

    public static string AntiforgeryToken(string html) => TokenRegex().Match(html).Groups[1].Value;

    public static string Path(HttpResponseMessage response) => response.Headers.Location!.OriginalString.Replace("http://localhost", string.Empty);

    /// <summary>Die Rollen eines Kontos.</summary>
    public static List<string> Roles(MatFileFactory site, long userId)
        => site.Db(db => db.UserRole.Where(ur => ur.UserId == userId).Select(ur => ur.Role!.Name).OrderBy(n => n).ToList());

    /// <summary>Legt ein Konto mit Kennwort und Rollen an (wie die Benutzerverwaltung).</summary>
    public static long AddUser(MatFileFactory site, string username, string password, string? email, params string[] roles)
    {
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatFileDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<MatFile.Web.Services.PasswordHasher>();
        var user = new User { Username = username, DisplayName = username, Email = email, PasswordHash = hasher.Hash(password), IsActive = true };
        db.User.Add(user);
        db.SaveChanges();
        foreach (var role in roles)
        {
            db.UserRole.Add(new UserRole { UserId = user.Id, RoleId = db.Role.Single(r => r.Name == role).Id });
        }

        db.SaveChanges();
        return user.Id;
    }

    [GeneratedRegex(@"name=""__RequestVerificationToken"" type=""hidden"" value=""([^""]+)""")]
    private static partial Regex TokenRegex();
}

/// <summary>Ohne <c>MATIDENTITY_AUTHORITY</c> ändert sich nichts: das lokale Formular, wie bisher.</summary>
public sealed class LocalSignInTests(MatFileFactory site) : IClassFixture<MatFileFactory>
{
    [Fact]
    public async Task The_sign_in_page_is_the_local_form_and_it_works()
    {
        var browser = site.Browser();
        var unsigned = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, unsigned.StatusCode);
        Assert.StartsWith("/Login", SignIn.Path(unsigned));

        var page = await browser.GetAsync("/Login");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.DoesNotContain("MatIdentity", html);

        var signedIn = await SignIn.PostLocalFormAsync(browser, "/Login", html);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Contains(signedIn.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(SignIn.SessionCookie + "=", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/")).StatusCode);

        // Abmelden führt zurück auf die lokale Anmeldung, nicht zu MatIdentity.
        var index = await (await browser.GetAsync("/")).Content.ReadAsStringAsync();
        var signedOut = await browser.PostAsync("/Logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(index) }));
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Equal("/Login", SignIn.Path(signedOut));
    }

    [Fact]
    public async Task A_wrong_password_is_refused()
    {
        var browser = site.Browser();
        var html = await (await browser.GetAsync("/Login")).Content.ReadAsStringAsync();
        var response = await SignIn.PostLocalFormAsync(browser, "/Login", html, "admin", "wrong");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [], c => c.StartsWith(SignIn.SessionCookie + "=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task There_is_no_matidentity_callback()
        => Assert.NotEqual(HttpStatusCode.OK, (await site.Browser().GetAsync("/signin-oidc")).StatusCode);
}

/// <summary>Mit <c>MATIDENTITY_AUTHORITY</c>: die Anmeldeseite geht gleich zu MatIdentity, mit Autorisierungscode und PKCE.</summary>
public sealed class IdentityAutoSignInTests(IdentityFactory site) : IClassFixture<IdentityFactory>
{
    [Fact]
    public async Task The_sign_in_page_goes_straight_to_matidentity_with_code_and_pkce()
    {
        var response = await site.Browser().GetAsync("/Login");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = response.Headers.Location!;
        Assert.StartsWith(IdentityFactory.Authority + "/connect/authorize", location.OriginalString);

        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("matfile", query["client_id"].ToString());
        Assert.Equal("code", query["response_type"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.False(string.IsNullOrEmpty(query["code_challenge"].ToString()));
        Assert.EndsWith("/signin-oidc", query["redirect_uri"].ToString());
    }

    [Fact]
    public async Task A_page_that_needs_a_sign_in_ends_at_matidentity()
    {
        var browser = site.Browser();
        var first = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        var login = SignIn.Path(first);
        Assert.StartsWith("/Login", login);

        var second = await browser.GetAsync(login);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.StartsWith(IdentityFactory.Authority, second.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_definition_names_matidentity_and_a_bad_token_is_401()
    {
        using var doc = JsonDocument.Parse(await site.CreateClient().GetStringAsync("/api"));
        Assert.Equal(IdentityFactory.Authority, doc.RootElement.GetProperty("auth").GetProperty("identity").GetString());

        var bearer = site.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.Api().GetAsync("/api/state")).StatusCode);
    }

    [Theory]
    [InlineData("/Login?local=1", false, false)]
    [InlineData("/Login?signedOut=1", false, false)]
    [InlineData("/Login?local=1&ssoError=1", true, false)]
    [InlineData("/Login?local=1&denied=1", false, true)]
    public async Task The_local_form_stays_reachable(string url, bool ssoError, bool denied)
    {
        var page = await site.Browser().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.Contains("id=\"login-identity\"", html);
        Assert.Equal(ssoError, html.Contains("id=\"login-sso-error\""));
        Assert.Equal(denied, html.Contains("id=\"login-denied\""));
    }

    [Fact]
    public async Task The_local_sign_in_works_with_matidentity_configured()
    {
        var browser = site.Browser();
        var html = await (await browser.GetAsync("/Login?local=1")).Content.ReadAsStringAsync();
        var signedIn = await SignIn.PostLocalFormAsync(browser, "/Login?local=1", html);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/")).StatusCode);

        // Eine lokale Sitzung hat kein ID-Token: die Abmeldung bleibt hier und landet ohne automatische Anmeldung auf ?signedOut=1.
        var index = await (await browser.GetAsync("/")).Content.ReadAsStringAsync();
        var signedOut = await browser.PostAsync("/Logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(index) }));
        Assert.Equal("/Login?signedOut=1", SignIn.Path(signedOut));
    }
}

/// <summary>MatIdentity ist eingerichtet, aber nicht erreichbar: die Anmeldeseite zeigt das lokale Formular mit einer Meldung.</summary>
public sealed class UnreachableIdentityTests(UnreachableIdentityFactory site) : IClassFixture<UnreachableIdentityFactory>
{
    [Fact]
    public async Task The_local_form_is_the_way_in()
    {
        var page = await site.Browser().GetAsync("/Login");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.Contains("id=\"login-sso-error\"", html);
    }
}

/// <summary>Die Person von MatIdentity: gefunden, neu angelegt, mit der Rolle des Claims (der letzte Administrator bleibt), und auch dort abgemeldet.</summary>
public sealed class IdentityUsersTests(IdentityFactory site) : IClassFixture<IdentityFactory>
{
    [Fact]
    public async Task A_new_person_becomes_a_user_with_the_role_of_the_claim_and_a_session_with_the_id_token()
    {
        var (target, cookie) = await SignInAsync(new MatIdentityPerson("sub-new", "new@example.com", "New Person", "newbie", "admin", "id-token-new", new()));
        Assert.Equal("/files", target);
        Assert.Contains(SignIn.SessionCookie + "=", cookie);

        var user = site.Db(db => db.User.Single(u => u.ExternalSubject == "sub-new"));
        Assert.Equal("newbie", user.Username);
        Assert.Equal("New Person", user.DisplayName);
        Assert.Equal("new@example.com", user.Email);
        Assert.Equal(string.Empty, user.PasswordHash);
        Assert.Equal(["Administrator"], SignIn.Roles(site, user.Id));
        Assert.Equal("id-token-new", site.Db(db => db.UserSession.Single(s => s.UserId == user.Id).IdToken));
        Assert.True(site.Db(db => db.ActivityLog.Any(a => a.UserId == user.Id && a.ActionType == "LoginMatIdentity")));

        // Die nächste Anmeldung findet dasselbe Konto über die Kennung und folgt einer geänderten Rolle.
        await SignInAsync(new MatIdentityPerson("sub-new", "changed@example.com", "New Person", "newbie", "user", "id-token-2", new()));
        Assert.Equal(1, site.Db(db => db.User.Count(u => u.ExternalSubject == "sub-new")));
        Assert.Equal(["User"], SignIn.Roles(site, user.Id));
    }

    [Fact]
    public async Task Without_a_role_claim_a_new_person_gets_the_role_user_and_a_free_name()
    {
        await SignInAsync(new MatIdentityPerson("sub-plain", "plain@example.com", null, "plain", null, null, new()));
        var user = site.Db(db => db.User.Single(u => u.ExternalSubject == "sub-plain"));
        Assert.Equal("plain", user.Username);
        Assert.Equal(["User"], SignIn.Roles(site, user.Id));

        // Ohne Benutzernamen in den Claims kommt der Name aus der Adresse; ist er vergeben, bekommt er eine Nummer.
        await SignInAsync(new MatIdentityPerson("sub-plain-2", "plain@other.example", null, null, null, null, new()));
        Assert.Equal("plain-2", site.Db(db => db.User.Single(u => u.ExternalSubject == "sub-plain-2").Username));
    }

    [Fact]
    public async Task The_last_administrator_keeps_the_role()
    {
        // Das einzige Administratorkonto ist mit MatIdentity verknüpft; MatIdentity sagt "user".
        var admin = site.Db(db => db.User.Single(u => u.Username == "admin"));
        // Andere aktive Konten dieser Testklasse (vielleicht Administratoren) für den Test abschalten.
        var others = site.Db(db => db.User.Where(u => u.Id != admin.Id && u.IsActive).Select(u => u.Id).ToList());
        site.Db(db =>
        {
            db.User.Single(u => u.Id == admin.Id).ExternalSubject = "sub-admin";
            foreach (var other in db.User.Where(u => others.Contains(u.Id)))
            {
                other.IsActive = false;
            }

            return db.SaveChanges();
        });

        var (target, _) = await SignInAsync(new MatIdentityPerson("sub-admin", null, "Admin", "admin", "user", "id-token-admin", new()));
        Assert.Equal("/files", target);
        Assert.Contains("Administrator", SignIn.Roles(site, admin.Id));

        site.Db(db =>
        {
            foreach (var other in db.User.Where(u => others.Contains(u.Id)))
            {
                other.IsActive = true;
            }

            db.User.Single(u => u.Id == admin.Id).ExternalSubject = null;
            return db.SaveChanges();
        });
    }

    [Fact]
    public async Task A_disabled_account_is_refused()
    {
        var id = SignIn.AddUser(site, "disabled", "password-1", "disabled@example.com", "User");
        site.Db(db =>
        {
            var user = db.User.Single(u => u.Id == id);
            user.ExternalSubject = "sub-disabled";
            user.IsActive = false;
            return db.SaveChanges();
        });

        var (target, cookie) = await SignInAsync(new MatIdentityPerson("sub-disabled", null, null, null, null, null, new()));
        Assert.Equal("/Login?local=1&denied=1", target);
        Assert.DoesNotContain(SignIn.SessionCookie + "=", cookie);
    }

    [Fact]
    public async Task Several_matching_accounts_are_refused()
    {
        SignIn.AddUser(site, "twin-a", "password-1", "twin@example.com", "User");
        SignIn.AddUser(site, "twin-b", "password-1", "Twin@Example.com", "User");

        var (target, _) = await SignInAsync(new MatIdentityPerson("sub-twin", "twin@example.com", null, null, null, null, new()));
        Assert.Equal("/Login?local=1&denied=1", target);
        Assert.False(site.Db(db => db.User.Any(u => u.ExternalSubject == "sub-twin")));
    }

    [Fact]
    public async Task A_match_linked_with_another_person_is_refused()
    {
        var id = SignIn.AddUser(site, "linked-other", "password-1", "linked@example.com", "User");
        site.Db(db =>
        {
            db.User.Single(u => u.Id == id).ExternalSubject = "sub-someone-else";
            return db.SaveChanges();
        });

        var (target, _) = await SignInAsync(new MatIdentityPerson("sub-intruder", "linked@example.com", null, null, null, null, new()));
        Assert.Equal("/Login?local=1&denied=1", target);
        Assert.Equal("sub-someone-else", site.Db(db => db.User.Single(u => u.Id == id).ExternalSubject));
    }

    [Fact]
    public async Task The_sign_out_ends_the_session_at_matidentity_with_the_id_token()
    {
        using var scope = site.Services.CreateScope();
        var http = site.Context(scope);
        await MatIdentitySignIn.CompleteSignInAsync(http, new MatIdentityPerson("sub-out", "out@example.com", "Out", "out", "user", "id-token-out", new()), "/");
        var cookies = new CookieContainer();
        foreach (var header in http.Response.Headers.SetCookie)
        {
            cookies.SetCookies(new Uri("http://localhost"), header!);
        }
        var browser = site.Browser(cookies);

        var index = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        var html = await index.Content.ReadAsStringAsync();

        var signedOut = await browser.PostAsync("/Logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html) }));
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        var location = signedOut.Headers.Location!.OriginalString;
        Assert.StartsWith(IdentityFactory.Authority + "/connect/logout", location);
        Assert.Equal("id-token-out", QueryHelpers.ParseQuery(new Uri(location).Query)["id_token_hint"].ToString());

        var user = site.Db(db => db.User.Single(u => u.ExternalSubject == "sub-out"));
        Assert.True(site.Db(db => db.UserSession.Single(s => s.UserId == user.Id).IsRevoked));
    }

    /// <summary>Was der Callback /signin-oidc nach MatIdentity tut, ohne laufendes MatIdentity: wohin der Browser geht und welche Cookies er bekommt.</summary>
    private async Task<(string Target, string SetCookie)> SignInAsync(MatIdentityPerson person)
    {
        using var scope = site.Services.CreateScope();
        var http = site.Context(scope);
        var target = await MatIdentitySignIn.CompleteSignInAsync(http, person, "/files");
        return (target, http.Response.Headers.SetCookie.ToString());
    }
}
