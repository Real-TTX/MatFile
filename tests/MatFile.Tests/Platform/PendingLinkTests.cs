using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatPlatform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace MatFile.Tests.Platform;

/// <summary>
/// Ein Konto, dessen E-Mail-Adresse (oder Benutzername) zu einer Person von MatIdentity passt, wird erst nach seinem Kennwort verknüpft: jeder kann
/// eine Adresse in ein Konto schreiben, sonst könnte jemand die Adresse eines Opfers vorher belegen und das Opfer bei der ersten Anmeldung über
/// MatIdentity in seinem Konto empfangen. Jeder Test startet seine eigene App.
/// </summary>
public sealed partial class PendingLinkTests
{
    private const string ClaimedAddress = "victim@example.com";
    private const string ClaimedName = "claimer";
    private const string ClaimedPassword = "claimer-knows-this";

    [Fact]
    public async Task A_pre_claimed_address_is_not_entered_and_the_browser_lands_on_the_local_sign_in()
    {
        using var site = Site(out var claimedId);
        var users = site.Db(db => db.User.Count());

        var (target, setCookie, browser) = await MatIdentitySignInAsync(site, Person());

        Assert.Equal("/Login?local=1&link=1", target);
        Assert.DoesNotContain(SignIn.SessionCookie + "=", setCookie);
        Assert.Contains(MatIdentityPendingLink.CookieName + "=", setCookie);
        Assert.Null(Claimed(site, claimedId).ExternalSubject);
        Assert.Equal(users, site.Db(db => db.User.Count()));
        Assert.False(site.Db(db => db.UserSession.Any(s => s.UserId == claimedId)));

        var page = await browser.GetAsync(target);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-link\"", html);
        Assert.Contains("id=\"login-link-cancel\"", html);
        Assert.Contains("id=\"login-form\"", html);
    }

    [Fact]
    public async Task The_local_sign_in_of_the_matched_account_links_it_with_the_role_and_the_id_token()
    {
        using var site = Site(out var claimedId);
        var (target, _, browser) = await MatIdentitySignInAsync(site, Person());
        var html = await (await browser.GetAsync(target)).Content.ReadAsStringAsync();

        // Ein falsches Kennwort verknüpft nichts und lässt die wartende Verknüpfung stehen.
        var wrong = await SignIn.PostLocalFormAsync(browser, target, html, ClaimedName, "wrong");
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        html = await wrong.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-link\"", html);
        Assert.Null(Claimed(site, claimedId).ExternalSubject);

        var signedIn = await SignIn.PostLocalFormAsync(browser, target, html, ClaimedName, ClaimedPassword);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Contains(signedIn.Headers.GetValues("Set-Cookie"), c => c.StartsWith(MatIdentityPendingLink.CookieName + "=;", StringComparison.Ordinal));

        Assert.Equal("sub-victim", Claimed(site, claimedId).ExternalSubject);
        Assert.Contains("Administrator", SignIn.Roles(site, claimedId));
        Assert.Equal("id-token-victim", site.Db(db => db.UserSession.Single(s => s.UserId == claimedId).IdToken));
        Assert.True(site.Db(db => db.ActivityLog.Any(a => a.UserId == claimedId && a.ActionType == "LinkedMatIdentity")));

        // Ab jetzt findet MatIdentity das Konto über die Kennung und meldet direkt an.
        var (next, cookie, _) = await MatIdentitySignInAsync(site, Person());
        Assert.Equal("/", next);
        Assert.Contains(SignIn.SessionCookie + "=", cookie);
    }

    [Fact]
    public async Task Another_account_signing_in_links_nothing_and_drops_the_pending_link()
    {
        using var site = Site(out var claimedId);
        var (target, _, browser) = await MatIdentitySignInAsync(site, Person());
        var html = await (await browser.GetAsync(target)).Content.ReadAsStringAsync();

        var signedIn = await SignIn.PostLocalFormAsync(browser, target, html, "admin", "admin");
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Contains(signedIn.Headers.GetValues("Set-Cookie"), c => c.StartsWith(MatIdentityPendingLink.CookieName + "=;", StringComparison.Ordinal));

        Assert.Null(Claimed(site, claimedId).ExternalSubject);
        Assert.False(site.Db(db => db.User.Any(u => u.ExternalSubject == "sub-victim")));
        var admin = site.Db(db => db.User.Single(u => u.Username == "admin"));
        Assert.Null(admin.ExternalSubject);
        Assert.Null(site.Db(db => db.UserSession.Single(s => s.UserId == admin.Id).IdToken));

        // Und danach auch das passende Konto nicht mehr: die Verknüpfung ist weg.
        var again = site.Browser();
        var form = await (await again.GetAsync("/Login?local=1")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(again, "/Login?local=1", form, ClaimedName, ClaimedPassword)).StatusCode);
        Assert.Null(Claimed(site, claimedId).ExternalSubject);
    }

    [Fact]
    public async Task Cancel_deletes_the_pending_link_and_a_later_sign_in_links_nothing()
    {
        using var site = Site(out var claimedId);
        var (target, _, browser) = await MatIdentitySignInAsync(site, Person());
        var html = await (await browser.GetAsync(target)).Content.ReadAsStringAsync();

        var cancelled = await browser.PostAsync("/Login?handler=CancelLink", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
        }));
        Assert.Equal(HttpStatusCode.Redirect, cancelled.StatusCode);
        Assert.Equal("/Login?local=1", SignIn.Path(cancelled));
        Assert.Contains(cancelled.Headers.GetValues("Set-Cookie"), c => c.StartsWith(MatIdentityPendingLink.CookieName + "=;", StringComparison.Ordinal));

        html = await (await browser.GetAsync(target)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("id=\"login-link\"", html);
        Assert.Contains("id=\"login-link-expired\"", html);

        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, target, html, ClaimedName, ClaimedPassword)).StatusCode);
        Assert.Null(Claimed(site, claimedId).ExternalSubject);
    }

    [Fact]
    public async Task A_matching_user_name_needs_the_proof_as_well()
    {
        using var site = Site(out var claimedId);
        var (target, setCookie, _) = await MatIdentitySignInAsync(site, new MatIdentityPerson("sub-name", null, "Claimer", ClaimedName, null, null, new()));

        Assert.Equal("/Login?local=1&link=1", target);
        Assert.DoesNotContain(SignIn.SessionCookie + "=", setCookie);
        Assert.Null(Claimed(site, claimedId).ExternalSubject);
    }

    [Fact]
    public async Task A_forged_pending_cookie_is_ignored()
    {
        using var site = Site(out var claimedId);
        var json = JsonSerializer.Serialize(new MatIdentityPendingLink("sub-forged", ClaimedAddress, null, null, "admin", null, claimedId.ToString()), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await AssertIgnoredAsync(site, claimedId, Uri.EscapeDataString(json));
    }

    [Fact]
    public async Task An_expired_pending_cookie_is_ignored()
    {
        using var site = Site(out var claimedId);
        var json = JsonSerializer.Serialize(new MatIdentityPendingLink("sub-expired", ClaimedAddress, null, null, "admin", null, claimedId.ToString()), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var value = site.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(MatIdentityPendingLink.Purpose).ToTimeLimitedDataProtector()
            .Protect(json, DateTimeOffset.UtcNow.AddSeconds(-1));
        await AssertIgnoredAsync(site, claimedId, value);
    }

    /// <summary>Die App mit dem Konto, das die Adresse des Opfers trägt; sein Kennwort kennt, wer es angelegt hat.</summary>
    private static IdentityFactory Site(out long claimedId)
    {
        var site = new IdentityFactory();
        claimedId = SignIn.AddUser(site, ClaimedName, ClaimedPassword, "Victim@Example.com", "User");
        return site;
    }

    private static MatIdentityPerson Person() => new("sub-victim", ClaimedAddress, "Victim", "victim", "admin", "id-token-victim", new());

    /// <summary>Die Person meldet sich bei MatIdentity an (der Callback ohne laufendes MatIdentity); ein Browser, der behält, was der Callback gesetzt hat.</summary>
    private static async Task<(string Target, string SetCookie, HttpClient Browser)> MatIdentitySignInAsync(IdentityFactory site, MatIdentityPerson person)
    {
        using var scope = site.Services.CreateScope();
        var http = site.Context(scope);

        var target = await MatIdentitySignIn.CompleteSignInAsync(http, person, "/");
        var setCookie = http.Response.Headers.SetCookie.ToString();

        var cookies = new CookieContainer();
        var pending = PendingCookieRegex().Match(setCookie);
        if (pending.Success)
        {
            cookies.Add(new Uri("http://localhost"), new Cookie(MatIdentityPendingLink.CookieName, pending.Groups[1].Value, "/"));
        }

        return (target, setCookie, site.Browser(cookies));
    }

    private static async Task AssertIgnoredAsync(IdentityFactory site, long claimedId, string cookieValue)
    {
        var cookies = new CookieContainer();
        cookies.Add(new Uri("http://localhost"), new Cookie(MatIdentityPendingLink.CookieName, cookieValue, "/"));
        var browser = site.Browser(cookies);

        var html = await (await browser.GetAsync("/Login?local=1&link=1")).Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.DoesNotContain("id=\"login-link\"", html);

        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Login?local=1&link=1", html, ClaimedName, ClaimedPassword)).StatusCode);
        Assert.Null(Claimed(site, claimedId).ExternalSubject);
        Assert.False(site.Db(db => db.User.Any(u => u.ExternalSubject == "sub-forged" || u.ExternalSubject == "sub-expired")));
    }

    private static MatFile.Web.Data.User Claimed(IdentityFactory site, long id) => site.Db(db => db.User.Single(u => u.Id == id));

    [GeneratedRegex(@"\.MatPlatform\.PendingLink=([^;]+)")]
    private static partial Regex PendingCookieRegex();
}
