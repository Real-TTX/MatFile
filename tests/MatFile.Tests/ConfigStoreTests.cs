using MatFile.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace MatFile.Tests;

/// <summary>
/// Die Konfiguration liegt als JSON im Datenverzeichnis. Sie darf den Start nie
/// verhindern - auch dann nicht, wenn jemand die Datei von Hand zerlegt hat.
/// </summary>
public class ConfigStoreTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(), "matfile-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Erster_start_legt_die_datei_mit_standardwerten_an()
    {
        var store = CreateStore(out var paths);

        Assert.True(File.Exists(paths.ConfigFile));
        Assert.Equal(14, store.Settings.SessionLifetimeDays);
        Assert.Equal(5, store.Settings.LoginMaxFailedAttempts);
        Assert.Equal(15, store.Settings.LoginLockoutMinutes);
        Assert.Equal(20, store.Settings.LoginAttemptsPerMinute);
        Assert.Equal(8, store.Settings.MinPasswordLength);
        // 0 heisst: keine Grenze beim Hochladen - auf einem NAS der Normalfall.
        Assert.Equal(0, store.Settings.MaxUploadMegabytes);
        Assert.False(store.Settings.RequireSecureCookie);
        Assert.False(store.Settings.RequirePasswordChangeOnFirstLogin);
    }

    [Fact]
    public void Gespeichertes_wird_wieder_gelesen()
    {
        var store = CreateStore(out var paths);

        store.Save(new MatFileSettings
        {
            SessionLifetimeDays = 30,
            LoginMaxFailedAttempts = 3,
            LoginLockoutMinutes = 10,
            LoginAttemptsPerMinute = 5,
            MinPasswordLength = 20,
            MaxUploadMegabytes = 2048,
            RequireSecureCookie = true,
            RequirePasswordChangeOnFirstLogin = true
        });

        Assert.Equal(30, store.Settings.SessionLifetimeDays);

        var wiedergelesen = new MatFileConfigStore(paths).Settings;

        Assert.Equal(30, wiedergelesen.SessionLifetimeDays);
        Assert.Equal(3, wiedergelesen.LoginMaxFailedAttempts);
        Assert.Equal(10, wiedergelesen.LoginLockoutMinutes);
        Assert.Equal(5, wiedergelesen.LoginAttemptsPerMinute);
        Assert.Equal(20, wiedergelesen.MinPasswordLength);
        Assert.Equal(2048, wiedergelesen.MaxUploadMegabytes);
        Assert.True(wiedergelesen.RequireSecureCookie);
        Assert.True(wiedergelesen.RequirePasswordChangeOnFirstLogin);
    }

    [Fact]
    public void Kaputte_datei_faellt_auf_die_standardwerte_zurueck()
    {
        CreateStore(out var paths);
        File.WriteAllText(paths.ConfigFile, "{ kaputt");

        var settings = new MatFileConfigStore(paths).Settings;

        Assert.Equal(14, settings.SessionLifetimeDays);
    }

    [Fact]
    public void Unbekannte_eintraege_stoeren_nicht()
    {
        CreateStore(out var paths);
        File.WriteAllText(paths.ConfigFile, """{ "SessionLifetimeDays": 7, "AusDerZukunft": true }""");

        var settings = new MatFileConfigStore(paths).Settings;

        Assert.Equal(7, settings.SessionLifetimeDays);
        Assert.Equal(8, settings.MinPasswordLength);
    }

    [Fact]
    public void CanWrite_erkennt_ein_beschreibbares_datenverzeichnis()
    {
        CreateStore(out var paths);

        Assert.True(paths.CanWrite());
    }

    private MatFileConfigStore CreateStore(out MatFilePaths paths)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MATFILE_DATA"] = _dataDirectory })
            .Build();

        paths = MatFilePaths.Create(configuration, new TestEnvironment(_dataDirectory));
        return new MatFileConfigStore(paths);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Nur so viel Umgebung, wie MatFilePaths.Create braucht.</summary>
    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "MatFile.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Test";
    }
}
