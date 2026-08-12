using MatFile.Web.Services;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();

var paths = MatFilePaths.Create(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<AppInfo>();

builder.Services.AddRazorPages();

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
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

app.MapGet("/healthz", (AppInfo info, MatFilePaths matFilePaths) =>
{
    var writable = matFilePaths.CanWrite();
    var payload = new
    {
        status = writable ? "healthy" : "degraded",
        version = info.Version,
        channel = info.Channel,
        dataDirectory = matFilePaths.DataDirectory,
        dataWritable = writable,
        startedAt = info.StartedAt
    };

    return writable ? Results.Ok(payload) : Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.Logger.LogInformation("MatFile {Version} ({Channel}) gestartet - Datenverzeichnis: {DataDirectory}",
    app.Services.GetRequiredService<AppInfo>().Version,
    app.Services.GetRequiredService<AppInfo>().Channel,
    paths.DataDirectory);

app.Run();
