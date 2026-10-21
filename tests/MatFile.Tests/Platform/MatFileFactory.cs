using System.Net;
using MatFile.Web.Data;
using MatPlatform;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MatFile.Tests.Platform;

/// <summary>
/// Die ganze App im Speicher, auf einem eigenen leeren Datenverzeichnis, ohne Hintergrunddienste (Job-Runner, Verbindungspflege).
/// <see cref="ApiKey"/> ist <c>MAT_API_KEY</c>, außer mit <c>withKey: false</c>; mit einer <c>authority</c> ist die Anmeldung über MatIdentity an,
/// mit einer festen OpenID-Konfiguration, damit kein MatIdentity laufen muss (die Tests sehen sich nur die Weiterleitungen an).
/// Program liest seine Einstellungen aus Umgebungsvariablen, darum laufen die Testklassen nacheinander (DisableTestParallelization).
/// Der Administrator ist <c>admin</c> / <c>admin</c> (DbSeeder).
/// </summary>
public class MatFileFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-key-0123456789";

    /// <summary>Der Schlüssel, mit dem das gedachte MatIdentity signiert (in der festen OpenID-Konfiguration): für die Logout-Tokens der Tests.</summary>
    public static readonly SigningCredentials IdentityCredentials = new(
        new RsaSecurityKey(System.Security.Cryptography.RSA.Create(2048)) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256);

    private readonly string _data = Path.Combine(Path.GetTempPath(), "matfile-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string? _authority;
    private readonly bool _reachable;

    public MatFileFactory()
        : this(withKey: true, authority: null)
    {
    }

    protected MatFileFactory(bool withKey, string? authority, bool reachable = true)
    {
        _authority = authority;
        _reachable = reachable;
        Directory.CreateDirectory(_data);
        Environment.SetEnvironmentVariable("MATFILE_DATA", _data);
        Environment.SetEnvironmentVariable("MATFILE_ADMIN_PASSWORD", null);
        Environment.SetEnvironmentVariable(MatPlatformSettings.ApiKeyVariable, withKey ? ApiKey : null);
        Environment.SetEnvironmentVariable(MatPlatformSettings.AuthorityVariable, authority);
        Environment.SetEnvironmentVariable(MatPlatformSettings.ClientIdVariable, null);
        Environment.SetEnvironmentVariable(MatPlatformSettings.ClientSecretVariable, authority is null ? null : "test-secret");
        Environment.SetEnvironmentVariable(MatPlatformSettings.InternalUrlVariable, null);
    }

    public string DataDirectory => _data;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // Program liest das, bevor der Host gebaut ist; noch einmal setzen, falls eine andere Factory es inzwischen geändert hat.
        Environment.SetEnvironmentVariable("MATFILE_DATA", _data);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();

            if (_authority is not null && _reachable)
            {
                var configuration = new OpenIdConnectConfiguration
                {
                    Issuer = _authority,
                    AuthorizationEndpoint = _authority + "/connect/authorize",
                    TokenEndpoint = _authority + "/connect/token",
                    EndSessionEndpoint = _authority + "/connect/logout",
                };
                configuration.SigningKeys.Add(IdentityCredentials.Key);

                services.PostConfigure<OpenIdConnectOptions>(MatIdentitySignIn.Scheme, options =>
                {
                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
                services.PostConfigure<JwtBearerOptions>(MatApiAccess.BearerScheme, options =>
                {
                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
            }
        });
    }

    /// <summary>Ein Browser, der Cookies behält und Weiterleitungen nicht folgt (die Tests sehen sich jeden Schritt an).</summary>
    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>Ein Browser, der schon <paramref name="cookies"/> hat (etwa eine Sitzung, die ein Dienst angelegt hat).</summary>
    public HttpClient Browser(CookieContainer cookies)
    {
        _ = Server;
        return new HttpClient(new CookieContainerHandler(cookies) { InnerHandler = Server.CreateHandler() }) { BaseAddress = new Uri("http://localhost") };
    }

    /// <summary>Ein Aufrufer mit dem Schlüssel (wie Mattiverse).</summary>
    public HttpClient Api()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }

    /// <summary>Arbeitet mit der Datenbank der App (nur in den Tests; die Webschicht fragt die Dienste).</summary>
    public T Db<T>(Func<MatFileDbContext, T> work)
    {
        using var scope = Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<MatFileDbContext>());
    }

    /// <summary>Ein HttpContext mit den Diensten der App, für <see cref="MatIdentitySignIn.CompleteSignInAsync"/> ohne laufendes MatIdentity.</summary>
    public DefaultHttpContext Context(IServiceScope scope)
    {
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("localhost");
        return http;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        foreach (var variable in new[] { "MATFILE_DATA", MatPlatformSettings.ApiKeyVariable, MatPlatformSettings.AuthorityVariable, MatPlatformSettings.ClientSecretVariable })
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
            // SQLite hält die Datei vielleicht noch einen Moment; das temporäre Verzeichnis räumt das System später.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Ohne <c>MAT_API_KEY</c>: der geschützte Teil antwortet allen mit 401.</summary>
public sealed class NoKeyFactory() : MatFileFactory(withKey: false, authority: null);

/// <summary>Mit <c>MATIDENTITY_AUTHORITY</c> (ein MatIdentity, das nicht laufen muss).</summary>
public sealed class IdentityFactory() : MatFileFactory(withKey: true, authority: Authority)
{
    public const string Authority = "http://identity.test";
}

/// <summary>Mit <c>MATIDENTITY_AUTHORITY</c> auf eine Adresse, an der niemand antwortet (MatIdentity ist aus).</summary>
public sealed class UnreachableIdentityFactory() : MatFileFactory(withKey: true, authority: "http://127.0.0.1:9", reachable: false);
