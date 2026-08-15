using MatFile.Web.Storage;

namespace MatFile.Tests;

/// <summary>Dieselbe Regel wie lokal, nur für Gegenstellen: der Basispfad ist die Wurzel.</summary>
public class RemotePathTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("upload", "/upload")]
    [InlineData("/upload/", "/upload")]
    [InlineData("\\upload\\unter\\", "/upload/unter")]
    public void NormalizeBase_vereinheitlicht_den_basispfad(string? input, string expected)
        => Assert.Equal(expected, RemotePath.NormalizeBase(input));

    [Theory]
    [InlineData("/upload", "Ordner/Datei.txt", "/upload/Ordner/Datei.txt")]
    [InlineData("/upload", "", "/upload")]
    [InlineData("/upload", null, "/upload")]
    [InlineData("", "Ordner", "/Ordner")]
    [InlineData("", "", "/")]
    public void TryResolve_setzt_basis_und_pfad_zusammen(string? basePath, string? relative, string expected)
    {
        Assert.True(RemotePath.TryResolve(basePath, relative, out var full));
        Assert.Equal(expected, full);
    }

    [Theory]
    [InlineData("../geheim")]
    [InlineData("Ordner/../..")]
    [InlineData("./versteckt")]
    public void TryResolve_blockiert_den_ausbruch(string relative)
    {
        Assert.False(RemotePath.TryResolve("/upload", relative, out var full));
        Assert.Equal(string.Empty, full);
    }
}
