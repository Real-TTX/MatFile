using MatFile.Web.Data;
using MatFile.Web.Storage;

namespace MatFile.Tests;

/// <summary>
/// Hat ein Benutzer mehrere Rollen, gilt auf einem Speicherort die Summe ihrer
/// Rechte - nicht die erste gefundene Zeile der Rechtematrix.
/// </summary>
public class RootPermissionsTests
{
    [Fact]
    public void Merge_addiert_die_rechte_mehrerer_rollen()
    {
        var lesen = new RootPermissions(true, false, false, false, true);
        var schreiben = new RootPermissions(false, true, false, true, false);

        var zusammen = lesen.Merge(schreiben);

        Assert.True(zusammen.CanRead);
        Assert.True(zusammen.CanWrite);
        Assert.True(zusammen.CanUpload);
        Assert.True(zusammen.CanDownload);
        Assert.False(zusammen.CanDelete);
    }

    [Fact]
    public void Merge_mit_None_aendert_nichts()
    {
        var rechte = new RootPermissions(true, true, false, false, true);

        Assert.Equal(rechte, rechte.Merge(RootPermissions.None));
        Assert.Equal(rechte, RootPermissions.None.Merge(rechte));
    }

    [Fact]
    public void Merge_mit_Full_gibt_alles()
    {
        Assert.Equal(RootPermissions.Full, RootPermissions.None.Merge(RootPermissions.Full));
    }

    [Fact]
    public void None_und_Full_sind_die_beiden_extreme()
    {
        Assert.False(RootPermissions.None.CanRead);
        Assert.False(RootPermissions.None.CanDelete);
        Assert.True(RootPermissions.Full.CanRead);
        Assert.True(RootPermissions.Full.CanDelete);
    }

    [Fact]
    public void From_uebernimmt_die_zeile_der_rechtematrix()
    {
        var access = new RootAccess
        {
            CanRead = true,
            CanWrite = false,
            CanDelete = true,
            CanUpload = false,
            CanDownload = true
        };

        var rechte = RootPermissions.From(access);

        Assert.True(rechte.CanRead);
        Assert.False(rechte.CanWrite);
        Assert.True(rechte.CanDelete);
        Assert.False(rechte.CanUpload);
        Assert.True(rechte.CanDownload);
    }
}
