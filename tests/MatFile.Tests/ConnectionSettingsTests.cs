using MatFile.Web.Data;
using MatFile.Web.Storage;

namespace MatFile.Tests;

public class ConnectionSettingsTests
{
    [Fact]
    public void Parse_liefert_bei_leerem_json_die_standardwerte()
    {
        var settings = ConnectionSettings.Parse(null);

        Assert.Equal(string.Empty, settings.Host);
        Assert.True(settings.Passive);
        Assert.False(settings.UseTls);
    }

    [Fact]
    public void Parse_uebersteht_kaputtes_json()
    {
        // Eine unlesbare Einstellung darf den Speicherort nicht unbrauchbar machen.
        var settings = ConnectionSettings.Parse("{ das ist kein json");

        Assert.Equal(string.Empty, settings.Host);
    }

    [Fact]
    public void Parse_liest_gross_und_kleinschreibung_gleich()
    {
        var settings = ConnectionSettings.Parse("""{ "host": "nas", "PORT": 2222, "useTls": true }""");

        Assert.Equal("nas", settings.Host);
        Assert.Equal(2222, settings.Port);
        Assert.True(settings.UseTls);
    }

    [Fact]
    public void ToJson_und_Parse_ergeben_wieder_dasselbe()
    {
        var original = new ConnectionSettings
        {
            Host = "nas",
            Port = 2200,
            Username = "matfile",
            Share = "daten",
            Passive = false,
            UseTls = true
        };

        var copy = ConnectionSettings.Parse(original.ToJson());

        Assert.Equal(original.Host, copy.Host);
        Assert.Equal(original.Port, copy.Port);
        Assert.Equal(original.Username, copy.Username);
        Assert.Equal(original.Share, copy.Share);
        Assert.Equal(original.Passive, copy.Passive);
        Assert.Equal(original.UseTls, copy.UseTls);
    }

    [Theory]
    [InlineData(StorageType.Sftp, false, 22)]
    [InlineData(StorageType.Ftp, false, 21)]
    [InlineData(StorageType.Smb, false, 445)]
    [InlineData(StorageType.WebDav, false, 80)]
    [InlineData(StorageType.WebDav, true, 443)]
    [InlineData(StorageType.Local, false, 0)]
    public void EffectivePort_faellt_auf_den_ueblichen_port_zurueck(StorageType type, bool useTls, int expected)
    {
        var settings = new ConnectionSettings { UseTls = useTls };

        Assert.Equal(expected, settings.EffectivePort(type));
    }

    [Fact]
    public void EffectivePort_bevorzugt_den_eingetragenen_port()
    {
        var settings = new ConnectionSettings { Port = 2222 };

        Assert.Equal(2222, settings.EffectivePort(StorageType.Sftp));
    }

    [Fact]
    public void Describe_zeigt_die_gegenstelle_je_typ()
    {
        var settings = new ConnectionSettings { Host = "nas", Share = "daten", Url = "https://cloud/dav" };

        Assert.Equal(string.Empty, settings.Describe(StorageType.Local));
        Assert.Equal("nas:22", settings.Describe(StorageType.Sftp));
        Assert.Equal("\\\\nas\\daten", settings.Describe(StorageType.Smb));
        Assert.Equal("https://cloud/dav", settings.Describe(StorageType.WebDav));
    }
}
