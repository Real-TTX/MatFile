using MatFile.Web.Storage;

namespace MatFile.Tests;

/// <summary>
/// Der Pool hält offene Verbindungen bereit. Hier steht ein einfacher Ersatz
/// für einen echten Client - geprüft wird das Verhalten des Pools, nicht SSH.
/// </summary>
public class ConnectionPoolTests
{
    private sealed class FakeClient
    {
        public bool Connected { get; set; } = true;
        public bool Closed { get; private set; }
        public void Close() => Closed = true;
    }

    private static ConnectionPool<FakeClient> CreatePool(int perKey = 4)
        => new(client => client.Connected, client => client.Close(), perKey);

    [Fact]
    public void Zurueckgegebene_verbindung_wird_wiederverwendet()
    {
        using var pool = CreatePool();
        var erzeugt = 0;
        FakeClient Create() { erzeugt++; return new FakeClient(); }

        FakeClient erster;
        using (var lease = pool.Rent("a", Create))
        {
            erster = lease.Client;
        }

        using (var lease = pool.Rent("a", Create))
        {
            Assert.Same(erster, lease.Client);
        }

        Assert.Equal(1, erzeugt);
    }

    [Fact]
    public void Verschiedene_schluessel_teilen_sich_keine_verbindung()
    {
        using var pool = CreatePool();

        using var eine = pool.Rent("a", () => new FakeClient());
        using var andere = pool.Rent("b", () => new FakeClient());

        Assert.NotSame(eine.Client, andere.Client);
    }

    [Fact]
    public void Gleichzeitig_ausgeliehen_wird_nie_dieselbe_verbindung()
    {
        using var pool = CreatePool();

        using var eine = pool.Rent("a", () => new FakeClient());
        using var andere = pool.Rent("a", () => new FakeClient());

        Assert.NotSame(eine.Client, andere.Client);
    }

    [Fact]
    public void Als_kaputt_gemeldete_verbindung_wird_geschlossen()
    {
        using var pool = CreatePool();
        FakeClient client;

        using (var lease = pool.Rent("a", () => new FakeClient()))
        {
            client = lease.Client;
            lease.Broken = true;
        }

        Assert.True(client.Closed);

        using var neue = pool.Rent("a", () => new FakeClient());
        Assert.NotSame(client, neue.Client);
    }

    [Fact]
    public void Abgerissene_verbindung_kommt_nicht_zurueck()
    {
        using var pool = CreatePool();
        FakeClient client;

        using (var lease = pool.Rent("a", () => new FakeClient()))
        {
            client = lease.Client;
            client.Connected = false;
        }

        Assert.True(client.Closed);
    }

    [Fact]
    public void Ueber_der_hoechstzahl_wird_geschlossen_statt_gestapelt()
    {
        using var pool = CreatePool(perKey: 1);

        var eine = pool.Rent("a", () => new FakeClient());
        var andere = pool.Rent("a", () => new FakeClient());
        var ersterClient = eine.Client;
        var zweiterClient = andere.Client;

        eine.Dispose();
        andere.Dispose();

        // Der erste liegt im Pool, für den zweiten ist kein Platz.
        Assert.False(ersterClient.Closed);
        Assert.True(zweiterClient.Closed);
    }

    [Fact]
    public void Zweimal_zurueckgeben_stapelt_nicht_doppelt()
    {
        using var pool = CreatePool(perKey: 1);

        var lease = pool.Rent("a", () => new FakeClient());
        lease.Dispose();
        lease.Dispose();

        var erste = pool.Rent("a", () => new FakeClient());
        var zweite = pool.Rent("a", () => new FakeClient());

        Assert.NotSame(erste.Client, zweite.Client);
    }

    [Fact]
    public void Sweep_schliesst_was_zu_lange_liegt()
    {
        using var pool = new ConnectionPool<FakeClient>(
            client => client.Connected, client => client.Close())
        {
            MaxIdle = TimeSpan.Zero
        };

        FakeClient client;

        using (var lease = pool.Rent("a", () => new FakeClient()))
        {
            client = lease.Client;
        }

        pool.Sweep();

        Assert.True(client.Closed);
    }

    [Fact]
    public void Beim_beenden_werden_alle_verbindungen_geschlossen()
    {
        var pool = CreatePool();
        FakeClient client;

        using (var lease = pool.Rent("a", () => new FakeClient()))
        {
            client = lease.Client;
        }

        pool.Dispose();

        Assert.True(client.Closed);
    }

    [Fact]
    public void Schluessel_unterscheidet_kennwoerter_und_verraet_sie_nicht()
    {
        var eins = ConnectionPool<FakeClient>.KeyOf("sftp", "nas", "22", "matfile", "geheim");
        var zwei = ConnectionPool<FakeClient>.KeyOf("sftp", "nas", "22", "matfile", "anders");

        Assert.NotEqual(eins, zwei);
        Assert.DoesNotContain("geheim", eins, StringComparison.Ordinal);
        Assert.Equal(eins, ConnectionPool<FakeClient>.KeyOf("sftp", "nas", "22", "matfile", "geheim"));
    }
}
