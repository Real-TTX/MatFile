using MatFile.Web.Storage;

namespace MatFile.Tests;

/// <summary>
/// Was inline ausgeliefert wird, entscheidet über eingeschleustes Skript auf
/// unserer Domain. HTML und SVG dürfen deshalb Text sein, aber nie inline.
/// </summary>
public class PreviewPolicyTests
{
    [Theory]
    [InlineData("bild.jpg", PreviewKind.Image)]
    [InlineData("BILD.PNG", PreviewKind.Image)]
    [InlineData("film.mp4", PreviewKind.Video)]
    [InlineData("ton.mp3", PreviewKind.Audio)]
    [InlineData("blatt.pdf", PreviewKind.Pdf)]
    [InlineData("notiz.txt", PreviewKind.Text)]
    [InlineData("daten.json", PreviewKind.Text)]
    [InlineData("seite.html", PreviewKind.Text)]
    [InlineData("zeichnung.svg", PreviewKind.Text)]
    [InlineData("archiv.zip", PreviewKind.None)]
    [InlineData("programm.exe", PreviewKind.None)]
    [InlineData("ohne-endung", PreviewKind.None)]
    public void KindOf_ordnet_die_dateiendung_zu(string name, PreviewKind expected)
        => Assert.Equal(expected, PreviewPolicy.KindOf(name));

    [Fact]
    public void KindOf_kennt_dateien_die_nur_aus_einer_endung_bestehen()
    {
        Assert.Equal(PreviewKind.Text, PreviewPolicy.KindOf(".gitignore"));
        Assert.Equal(PreviewKind.Text, PreviewPolicy.KindOf(".editorconfig"));
    }

    [Theory]
    [InlineData("bild.png", "image/png")]
    [InlineData("film.webm", "video/webm")]
    [InlineData("blatt.pdf", "application/pdf")]
    public void InlineContentType_liefert_den_typ_der_erlaubten_formate(string name, string expected)
        => Assert.Equal(expected, PreviewPolicy.InlineContentType(name));

    [Theory]
    [InlineData("seite.html")]
    [InlineData("zeichnung.svg")]
    [InlineData("notiz.txt")]
    [InlineData("archiv.zip")]
    public void InlineContentType_verweigert_alles_andere(string name)
        => Assert.Null(PreviewPolicy.InlineContentType(name));
}
