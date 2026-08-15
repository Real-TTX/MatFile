using MatFile.Web.Storage;

namespace MatFile.Tests;

/// <summary>
/// PathGuard entscheidet, ob ein Ausbruch aus einem Speicherort möglich ist -
/// die Regeln hier sind die wichtigsten der ganzen Anwendung.
/// </summary>
public class PathGuardTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("/Ordner/", "Ordner")]
    [InlineData("\\Ordner\\Datei.txt", "Ordner/Datei.txt")]
    [InlineData("///a///", "a")]
    public void Normalize_bringt_pfade_in_die_interne_form(string? input, string expected)
        => Assert.Equal(expected, PathGuard.Normalize(input));

    [Theory]
    [InlineData("a/b/c", "a/b")]
    [InlineData("a", "")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void GetParent_liefert_die_ebene_darueber(string? input, string? expected)
        => Assert.Equal(expected, PathGuard.GetParent(input));

    [Theory]
    [InlineData(null, "Datei.txt", "Datei.txt")]
    [InlineData("", "Datei.txt", "Datei.txt")]
    [InlineData("Ordner", "Datei.txt", "Ordner/Datei.txt")]
    [InlineData("/Ordner/", "/Datei.txt/", "Ordner/Datei.txt")]
    public void Combine_haengt_ohne_doppelte_trenner_an(string? path, string name, string expected)
        => Assert.Equal(expected, PathGuard.Combine(path, name));

    [Theory]
    [InlineData("Datei.txt", true)]
    [InlineData("Ordner mit Leerzeichen", true)]
    [InlineData(".versteckt", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    public void IsValidName_erkennt_unbrauchbare_namen(string name, bool expected)
        => Assert.Equal(expected, PathGuard.IsValidName(name));

    [Fact]
    public void TryResolve_bleibt_im_speicherort()
    {
        var root = CreateDirectory();

        Assert.True(PathGuard.TryResolve(root, "Unterordner/Datei.txt", out var full));
        Assert.StartsWith(root, full, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("Unterordner", "Datei.txt"), full, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolve_ohne_pfad_liefert_den_speicherort_selbst()
    {
        var root = CreateDirectory();

        Assert.True(PathGuard.TryResolve(root, null, out var full));
        Assert.Equal(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(full));
    }

    [Theory]
    [InlineData("../draussen")]
    [InlineData("Ordner/../../draussen")]
    [InlineData("..")]
    public void TryResolve_blockiert_den_ausbruch(string relative)
    {
        var root = CreateDirectory();

        Assert.False(PathGuard.TryResolve(root, relative, out var full));
        Assert.Equal(string.Empty, full);
    }

    [Fact]
    public void TryResolve_blockiert_absolute_pfade()
    {
        var root = CreateDirectory();
        var absolute = OperatingSystem.IsWindows() ? "C:/Windows" : "/etc";

        Assert.False(PathGuard.TryResolve(root, absolute, out _));
    }

    [Fact]
    public void IsInside_erkennt_den_speicherort_selbst()
    {
        var root = Path.GetFullPath(CreateDirectory());

        Assert.True(PathGuard.IsInside(root, root));
        Assert.True(PathGuard.IsInside(root, Path.Combine(root, "Unterordner")));
        Assert.False(PathGuard.IsInside(root, Path.GetFullPath(Path.Combine(root, "..", "daneben"))));
    }

    /// <summary>
    /// Ein Speicherort darf auf der Wurzel liegen. Ohne Sonderbehandlung entstünde
    /// dort "//" als Präfix und jeder Unterordner gälte als ausserhalb.
    /// </summary>
    [Fact]
    public void IsInside_kommt_mit_einem_wurzelpfad_zurecht()
    {
        var root = OperatingSystem.IsWindows() ? "C:\\" : "/";
        var child = OperatingSystem.IsWindows() ? "C:\\storage" : "/storage";

        Assert.True(PathGuard.IsInside(root, root));
        Assert.True(PathGuard.IsInside(root, child));
    }

    [Fact]
    public void IsLinkTargetInside_meldet_fehlende_pfade_als_unverdaechtig()
    {
        var root = CreateDirectory();

        Assert.True(PathGuard.IsLinkTargetInside(root, Path.Combine(root, "gibtsnicht.txt")));
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "matfile-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
