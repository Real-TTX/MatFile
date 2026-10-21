using System.Net;
using MatPlatform;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MatFile.Tests.Platform;

/// <summary>
/// Die Abmeldung durch MatIdentity (Back-Channel-Logout, MatPlatform 1.4.0): MatIdentity schickt ein signiertes Logout-Token an
/// <c>POST /signout-backchannel</c>, MatFile beendet die Sitzungen, und die nächste Anfrage einer beendeten Sitzung ist abgemeldet.
/// </summary>
public sealed class BackchannelLogoutTests(IdentityFactory site) : IClassFixture<IdentityFactory>
{
    private const string Path = "/signout-backchannel";

    [Fact]
    public async Task A_valid_token_ends_the_session_it_names_and_its_next_request_is_signed_out()
    {
        var first = await SignedInBrowserAsync(Person("sub-bc-sid", "sid-bc-1"));
        var second = await SignedInBrowserAsync(Person("sub-bc-sid", "sid-bc-2"));
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/")).StatusCode);

        var response = await PostAsync(Token("sub-bc-sid", "sid-bc-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var signedOut = await first.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.StartsWith("/Login", SignIn.Path(signedOut));
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/")).StatusCode);

        var userId = site.Db(db => db.User.Single(u => u.ExternalSubject == "sub-bc-sid").Id);
        Assert.True(site.Db(db => db.UserSession.Single(s => s.IdentitySessionId == "sid-bc-1").IsRevoked));
        Assert.True(site.Db(db => db.ActivityLog.Any(a => a.UserId == userId && a.ActionType == "LogoutMatIdentity")));
    }

    [Fact]
    public async Task A_token_with_the_subject_alone_ends_every_session_of_the_linked_user()
    {
        var first = await SignedInBrowserAsync(Person("sub-bc-all", "sid-bc-all-1"));
        var second = await SignedInBrowserAsync(Person("sub-bc-all", "sid-bc-all-2"));
        var other = await SignedInBrowserAsync(Person("sub-bc-other", "sid-bc-other"));

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token("sub-bc-all", null))).StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await first.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await second.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_subject_or_session_ends_nothing_and_is_no_error()
    {
        var browser = await SignedInBrowserAsync(Person("sub-bc-known", "sid-bc-known"));

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token("sub-bc-unknown", null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token(null, "sid-bc-unknown"))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task A_token_of_another_key_is_400_and_ends_nothing()
    {
        var browser = await SignedInBrowserAsync(Person("sub-bc-forged", "sid-bc-forged"));
        var other = new SigningCredentials(new RsaSecurityKey(System.Security.Cryptography.RSA.Create(2048)) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(Token("sub-bc-forged", "sid-bc-forged", other))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/")).StatusCode);
    }

    private static MatIdentityPerson Person(string subject, string sessionId)
        => new(subject, subject + "@example.com", null, subject, "user", "id-token-" + sessionId, new()) { SessionId = sessionId };

    /// <summary>Die Person meldet sich über MatIdentity an (der Callback ohne laufendes MatIdentity); ein Browser mit dem Sitzungscookie.</summary>
    private async Task<HttpClient> SignedInBrowserAsync(MatIdentityPerson person)
    {
        using var scope = site.Services.CreateScope();
        var http = site.Context(scope);
        await MatIdentitySignIn.CompleteSignInAsync(http, person, "/");
        var cookies = new CookieContainer();
        foreach (var header in http.Response.Headers.SetCookie)
        {
            cookies.SetCookies(new Uri("http://localhost"), header!);
        }

        return site.Browser(cookies);
    }

    private Task<HttpResponseMessage> PostAsync(string token)
        => site.CreateClient().PostAsync(Path, new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = token }));

    /// <summary>Ein Logout-Token, wie MatIdentity es schreibt (signiert mit dem Schlüssel der festen Konfiguration).</summary>
    internal static string Token(string? subject, string? sessionId, SigningCredentials? credentials = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = IdentityFactory.Authority,
            ["aud"] = "matfile",
            ["iat"] = now,
            ["nbf"] = now,
            ["exp"] = now + 120,
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["events"] = new Dictionary<string, object> { [MatIdentityLogoutToken.EventType] = new Dictionary<string, object>() },
        };
        if (subject is not null)
        {
            claims["sub"] = subject;
        }

        if (sessionId is not null)
        {
            claims["sid"] = sessionId;
        }

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            TokenType = MatIdentityLogoutToken.TokenType,
            SigningCredentials = credentials ?? MatFileFactory.IdentityCredentials,
        });
    }
}

/// <summary>Ohne <c>MATIDENTITY_AUTHORITY</c> gibt es keinen Back-Channel-Logout.</summary>
public sealed class BackchannelLogoutWithoutIdentityTests(MatFileFactory site) : IClassFixture<MatFileFactory>
{
    [Fact]
    public async Task Without_matidentity_there_is_no_endpoint()
    {
        var endpoints = site.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        Assert.DoesNotContain(endpoints, e => e.RoutePattern.RawText?.TrimStart('/') == "signout-backchannel");

        // Wie jede unbekannte Adresse: ohne Anmeldung zur Anmeldeseite (die Rückfall-Richtlinie verlangt eine); angemeldet trifft ein POST nur
        // die Seitenrouten für GET (405) - nichts liest das Token.
        var content = new Dictionary<string, string> { ["logout_token"] = BackchannelLogoutTests.Token("sub-bc", "sid-bc") };
        var anonymous = await site.Browser().PostAsync("/signout-backchannel", new FormUrlEncodedContent(content));
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.StartsWith("/Login", SignIn.Path(anonymous));

        var browser = site.Browser();
        var html = await (await browser.GetAsync("/Login")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Login", html)).StatusCode);
        var signedIn = await browser.PostAsync("/signout-backchannel", new FormUrlEncodedContent(content));
        Assert.Contains(signedIn.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
    }
}
