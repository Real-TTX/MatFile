using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Threading.RateLimiting;
using MatFile.Web;
using MatFile.Web.Api;
using MatFile.Web.Data;
using MatFile.Web.Platform;
using MatFile.Web.Security;
using MatFile.Web.Services;
using MatFile.Web.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.WebEncoders;
using MatPlatform;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();

var paths = MatFilePaths.Create(builder.Configuration, builder.Environment);
var configStore = new MatFileConfigStore(paths);
var settings = configStore.Settings;

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(configStore);
builder.Services.AddSingleton<AppInfo>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ICurrentUser, HttpCurrentUser>();
builder.Services.AddSingleton<AuditInterceptor>();

builder.Services.AddDbContext<MatFileDbContext>((serviceProvider, options) =>
    options.UseSqlite(paths.ConnectionString)
           .AddInterceptors(serviceProvider.GetRequiredService<AuditInterceptor>()));

builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<RoleService>();
builder.Services.AddScoped<StorageRootService>();
builder.Services.AddScoped<ExplorerStateService>();
builder.Services.AddScoped<FileOperationService>();
builder.Services.AddScoped<ActivityLogService>();
builder.Services.AddScoped<UploadService>();
builder.Services.AddSingleton<JobService>();
builder.Services.AddHostedService<JobRunner>();
builder.Services.AddSingleton<SearchService>();
builder.Services.AddScoped<SystemStatusService>();
builder.Services.AddSingleton<StorageSurveyCache>();
// Offene Verbindungen zu SFTP und FTP wiederverwenden - das Anmelden kostet
// bei SSH mehrere hundert Millisekunden je Anfrage.
builder.Services.AddSingleton(_ => new ConnectionPool<Renci.SshNet.SftpClient>(
    client => client.IsConnected, client => client.Dispose()));
builder.Services.AddSingleton(_ => new ConnectionPool<FluentFTP.AsyncFtpClient>(
    client => client.IsConnected, client => client.Dispose()));
builder.Services.AddHostedService<ConnectionPoolSweeper>();

builder.Services.AddSingleton<StorageProviderFactory>();
builder.Services.AddSingleton<SecretProtector>();

builder.Services.AddHttpClient(StorageProviderFactory.WebDavClient, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    // Umleitungen behandelt der Provider selbst - beim automatischen Folgen
    // verwirft HttpClient den Authorization-Header.
    AllowAutoRedirect = false
});

// Der Plattformvertrag der Mat-Apps (MatDesign docs/PLATFORM.md): /api, /api/health, /api/state, /api/configure, /api/actions
// aus dem Paket MatPlatform (eine Kopie in MatPlatform/); MatFilePlatform beschreibt MatFile und liefert die Werte.
// MAT_API_KEY schützt den Teil mit Werten, MATIDENTITY_* schaltet die Anmeldung über MatIdentity ein (ohne: alles wie bisher).
var platform = builder.AddMatPlatform<MatFilePlatform>(MatFilePlatform.Describe);

// Schlüssel im Volume -> Cookies und damit Sitzungen überleben einen Container-Neustart.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(paths.KeyDirectory))
    .SetApplicationName("MatFile");

builder.Services.AddAuthentication(MatFileClaims.AuthenticationScheme)
    .AddCookie(MatFileClaims.AuthenticationScheme, options =>
    {
        options.Cookie.Name = "matfile.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = settings.RequireSecureCookie
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/Login";
        options.LogoutPath = "/Logout";
        options.AccessDeniedPath = "/Error";
        options.ReturnUrlParameter = "returnUrl";
        options.ExpireTimeSpan = TimeSpan.FromDays(settings.SessionLifetimeDays);
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = AuthService.ValidatePrincipalAsync;
        // Seitenaufrufe werden umgeleitet, API-Aufrufe bekommen einen Statuscode.
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }

            context.Response.Redirect("/Error?code=403");
            return Task.CompletedTask;
        };

        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    })
    // Anmeldung über MatIdentity, nur mit MATIDENTITY_AUTHORITY (ohne ändert sich nichts).
    // Ein abgelehntes Konto (deaktiviert, mehrdeutig) landet mit einer Meldung auf der lokalen Anmeldung.
    .AddMatIdentitySignIn<MatFileIdentityUsers>(platform, options =>
    {
        options.CookieScheme = MatFileClaims.AuthenticationScheme;
        options.LoginPath = "/Login";
        options.AccessDeniedPath = "/Login?local=1&denied=1";
    });

builder.Services.AddAuthorizationBuilder()
    .AddMatFilePolicies()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// Oberflächensprachen: Englisch ist Standard, Deutsch ist über die Sprachauswahl wählbar.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.SetDefaultCulture(MatFileCultures.Default);
    options.AddSupportedCultures(MatFileCultures.Codes);
    options.AddSupportedUICultures(MatFileCultures.Codes);
    options.ApplyCurrentCultureToResponseHeaders = true;
});

// Umlaute und Sonderzeichen nicht als HTML-Entities ausgeben.
builder.Services.Configure<WebEncoderOptions>(options =>
    options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/Error");
    options.Conventions.AllowAnonymousToPage("/SetLanguage");
    // Die Verwaltung läuft über /api - dort hängt die Autorisierung an den Endpunkten.
})
.AddViewLocalization()
.AddDataAnnotationsLocalization(options =>
    options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)))
.AddMvcOptions(options =>
{
    // Nicht jedes Non-Nullable-Feld ist ein Pflichtfeld - sonst entstehen zudem
    // unübersetzte Meldungen. Pflichtfelder werden ausdrücklich mit [Required] markiert.
    options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
});

// Anmeldeversuche begrenzen: die Kontosperre schützt ein Konto, das hier bremst den
// Absender - also auch das Durchprobieren vieler verschiedener Benutzernamen.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        if (!HttpMethods.IsPost(context.Request.Method) || !IsLoginPath(context.Request.Path))
        {
            return RateLimitPartition.GetNoLimiter("frei");
        }

        return RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Clamp(settings.LoginAttemptsPerMinute, 3, 1000),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });

    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };
});

static bool IsLoginPath(PathString path)
    => path.StartsWithSegments("/Login") || path.StartsWithSegments("/Account/ChangePassword");

// Hinter einem Reverse-Proxy steht die echte Adresse in X-Forwarded-For; darum kümmert
// sich UseForwardedHeaders, das vor dem Limiter läuft.
static string ClientKey(HttpContext context)
    => context.Connection.RemoteIpAddress?.ToString() ?? "unbekannt";

// MatFile läuft typischerweise hinter einem Reverse-Proxy des NAS.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");
app.UseRouting();
// Nach UseRouting: die Grenze der anonymen Plattform-Endpunkte (/api, /api/health) hängt am Endpunkt.
app.UseRateLimiter();
app.UseRequestLocalization();
app.UseAuthentication();
app.UseAuthorization();

// Erzwungener Kennwortwechsel (erste Anmeldung oder Zurücksetzen durch einen Administrator).
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true && context.User.RequiresPasswordChange())
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var allowed = path.StartsWith("/Account/ChangePassword", StringComparison.OrdinalIgnoreCase)
                      || path.StartsWith("/Logout", StringComparison.OrdinalIgnoreCase)
                      || path.StartsWith("/Login", StringComparison.OrdinalIgnoreCase)
                      || path.StartsWith(MatIdentitySignIn.CallbackPath, StringComparison.OrdinalIgnoreCase)
                      || path.StartsWith(MatIdentitySignIn.SignedOutCallbackPath, StringComparison.OrdinalIgnoreCase)
                      || path.StartsWith("/SetLanguage", StringComparison.OrdinalIgnoreCase)
                      || path.StartsWith("/css", StringComparison.OrdinalIgnoreCase)
                      || path.StartsWith("/js", StringComparison.OrdinalIgnoreCase)
                      || path.StartsWith("/icons", StringComparison.OrdinalIgnoreCase)
                      || path.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase)
                      || path.StartsWith("/site.webmanifest", StringComparison.OrdinalIgnoreCase);

        if (!allowed)
        {
            context.Response.Redirect("/Account/ChangePassword");
            return;
        }
    }

    await next();
});

app.MapStaticAssets().AllowAnonymous();
app.MapRazorPages().WithStaticAssets();
app.MapFileSystemEndpoints();
app.MapFileOperationEndpoints();
app.MapPreviewEndpoints();
app.MapExplorerEndpoints();
app.MapRootManagementEndpoints();
app.MapSearchEndpoints();
app.MapAdminEndpoints();
app.MapSystemEndpoints();
app.MapActivityEndpoints();
// Neben MatFiles eigener API unter /api/...: keine Route überschneidet sich (/api/fs, /api/jobs, /api/settings, ... gegen
// /api, /api/health, /api/state, /api/configure, /api/actions).
app.MapMatPlatform();

app.MapGet("/healthz", (AppInfo info, MatFilePaths matFilePaths, MatFileDbContext db) =>
{
    var writable = matFilePaths.CanWrite();
    var databaseReady = db.Database.CanConnect();
    var healthy = writable && databaseReady;

    var payload = new
    {
        status = healthy ? "healthy" : "degraded",
        version = info.Version,
        channel = info.Channel,
        dataDirectory = matFilePaths.DataDirectory,
        dataWritable = writable,
        databaseReady,
        startedAt = info.StartedAt
    };

    return healthy ? Results.Ok(payload) : Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();

var appInfo = app.Services.GetRequiredService<AppInfo>();
app.Logger.LogInformation("MatFile {Version} ({Channel}) startet - Datenverzeichnis: {DataDirectory}",
    appInfo.Version, appInfo.Channel, paths.DataDirectory);

await DbSeeder.SeedAsync(app.Services, app.Logger);

app.Run();
