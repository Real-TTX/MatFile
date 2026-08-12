namespace MatFile.Web.Services;

/// <summary>
/// Legt fest, wo MatFile seine Daten ablegt, und stellt sicher, dass die Verzeichnisse existieren.
/// Im Container zeigt alles auf das Volume /data (siehe MATFILE_DATA).
/// </summary>
public sealed class MatFilePaths
{
    public string DataDirectory { get; }
    public string ConfigDirectory { get; }
    public string KeyDirectory { get; }
    public string ThumbnailDirectory { get; }
    public string TempDirectory { get; }

    public string DatabaseFile => Path.Combine(DataDirectory, "matfile.db");
    public string ConfigFile => Path.Combine(ConfigDirectory, "matfile.json");
    public string ConnectionString => $"Data Source={DatabaseFile};Cache=Shared;Foreign Keys=True";

    private MatFilePaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        ConfigDirectory = Path.Combine(dataDirectory, "config");
        KeyDirectory = Path.Combine(dataDirectory, "keys");
        ThumbnailDirectory = Path.Combine(dataDirectory, "thumbs");
        TempDirectory = Path.Combine(dataDirectory, "temp");
    }

    /// <summary>
    /// Ermittelt das Datenverzeichnis aus MATFILE_DATA bzw. der Konfiguration.
    /// Fallback ist ./data unterhalb des Anwendungsverzeichnisses (lokale Entwicklung).
    /// </summary>
    public static MatFilePaths Create(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["MATFILE_DATA"] ?? configuration["MatFile:DataDirectory"];
        var dataDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "data")
            : Path.GetFullPath(configured);

        var paths = new MatFilePaths(dataDirectory);
        paths.EnsureDirectories();
        return paths;
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(KeyDirectory);
        Directory.CreateDirectory(ThumbnailDirectory);
        Directory.CreateDirectory(TempDirectory);
    }

    /// <summary>Prüft, ob in das Datenverzeichnis geschrieben werden kann (für /healthz).</summary>
    public bool CanWrite()
    {
        try
        {
            var probe = Path.Combine(TempDirectory, ".write-probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
