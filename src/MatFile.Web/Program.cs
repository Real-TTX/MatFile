using System.Text.Encodings.Web;
using System.Text.Unicode;
using MatFile.Web;
using MatFile.Web.Data;
using MatFile.Web.Security;
using MatFile.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.WebEncoders;

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
})
.AddViewLocalization()
.AddDataAnnotationsLocalization(options =>
    options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));

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
