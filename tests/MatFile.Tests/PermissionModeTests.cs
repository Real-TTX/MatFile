using MatFile.Web.Storage;

namespace MatFile.Tests;

/// <summary>
/// Die Rechte reisen als Bitmuster durch die Anwendung, angezeigt und an die
/// Gegenstellen übergeben werden sie aber oktal. Genau dazwischen lag der Fehler,
/// den erst die Integrationstests gefunden haben.
/// </summary>
public class PermissionModeTests
{
    [Theory]
    [InlineData(0, "000")]
    [InlineData(0b111_101_101, "755")]
    [InlineData(0b111_000_000, "700")]
    [InlineData(0b110_100_100, "644")]
    [InlineData(0b111_111_111, "777")]
    public void ToText_schreibt_die_oktalzahl(int mode, string expected)
        => Assert.Equal(expected, PermissionMode.ToText(mode));

    [Fact]
    public void ToText_beachtet_nur_die_neun_rwx_bits()
    {
        // Setuid und Konsorten stehen darüber und dürfen nicht durchschlagen.
        Assert.Equal("755", PermissionMode.ToText(0b100_111_101_101));
    }

    [Fact]
    public void FromParts_setzt_die_bits_in_der_richtigen_reihenfolge()
    {
        var mode = PermissionMode.FromParts(
            ownerRead: true, ownerWrite: true, ownerExecute: true,
            groupRead: true, groupWrite: false, groupExecute: true,
            otherRead: true, otherWrite: false, otherExecute: true);

        Assert.Equal("755", PermissionMode.ToText(mode));
    }

    [Fact]
    public void FromParts_ohne_rechte_ergibt_null()
    {
        var mode = PermissionMode.FromParts(false, false, false, false, false, false, false, false, false);

        Assert.Equal(0, mode);
        Assert.Equal("000", PermissionMode.ToText(mode));
    }

    [Fact]
    public void FromParts_nur_schreiben_fuer_andere()
    {
        var mode = PermissionMode.FromParts(false, false, false, false, false, false, false, true, false);

        Assert.Equal("002", PermissionMode.ToText(mode));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(0b111_111_111, true)]
    [InlineData(-1, false)]
    [InlineData(0b1_000_000_000, false)]
    [InlineData(4096, false)]
    public void IsValid_laesst_nur_werte_bis_777_zu(int mode, bool expected)
        => Assert.Equal(expected, PermissionMode.IsValid(mode));
}
