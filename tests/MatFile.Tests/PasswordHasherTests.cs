using MatFile.Web.Services;

namespace MatFile.Tests;

public class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new();

    [Fact]
    public void Hash_und_Verify_passen_zusammen()
    {
        var hash = _hasher.Hash("Ein-gutes-Kennwort-2026");

        Assert.True(_hasher.Verify("Ein-gutes-Kennwort-2026", hash));
        Assert.False(_hasher.Verify("ein-gutes-kennwort-2026", hash));
        Assert.False(_hasher.Verify("etwas anderes", hash));
    }

    [Fact]
    public void Gleiches_kennwort_ergibt_verschiedene_hashes()
    {
        // Jeder Hash bekommt sein eigenes Salz - sonst verraten gleiche Hashes gleiche Kennwörter.
        var erster = _hasher.Hash("Kennwort");
        var zweiter = _hasher.Hash("Kennwort");

        Assert.NotEqual(erster, zweiter);
        Assert.True(_hasher.Verify("Kennwort", erster));
        Assert.True(_hasher.Verify("Kennwort", zweiter));
    }

    [Fact]
    public void Hash_nennt_verfahren_und_durchlaeufe()
    {
        var parts = _hasher.Hash("Kennwort").Split('$');

        Assert.Equal(5, parts.Length);
        Assert.Equal("pbkdf2", parts[0]);
        Assert.Equal("sha256", parts[1]);
        Assert.True(int.Parse(parts[2]) >= 100_000);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("kein-hash")]
    [InlineData("pbkdf2$sha256$abc$salz$hash")]
    [InlineData("md5$sha256$1000$salz$hash")]
    public void Verify_lehnt_unbrauchbare_hashes_ab(string? stored)
        => Assert.False(_hasher.Verify("Kennwort", stored));

    [Fact]
    public void Verify_ohne_kennwort_ist_immer_falsch()
        => Assert.False(_hasher.Verify(string.Empty, _hasher.Hash("Kennwort")));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Hash_verlangt_ein_kennwort(string password)
        => Assert.Throws<ArgumentException>(() => _hasher.Hash(password));
}
