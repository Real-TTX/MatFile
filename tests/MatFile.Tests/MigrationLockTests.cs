using MatFile.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MatFile.Tests;

/// <summary>
/// Ein Prozess, der während der Migration gestoppt wird, hinterlässt die Sperrzeile von EF Core, und der nächste Start würde ewig auf sie warten
/// (passiert bei einem Update: der Stack wird nach dem Backup wieder gestartet und Sekunden später neu erzeugt).
/// </summary>
public class MigrationLockTests
{
    [Fact]
    public async Task Eine_verwaiste_sperre_wird_entfernt_und_die_migration_laeuft()
    {
        var file = Path.Combine(Path.GetTempPath(), "matfile-lock-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using (var connection = new SqliteConnection("Data Source=" + file))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE \"__EFMigrationsLock\" (\"Id\" INTEGER NOT NULL PRIMARY KEY, \"Timestamp\" TEXT NOT NULL); " +
                                      "INSERT INTO \"__EFMigrationsLock\" VALUES (1, '2026-10-18 13:28:48');";
                await command.ExecuteNonQueryAsync();
            }

            await using var db = Context(file);
            Assert.True(await MigrationLock.ReleaseStaleAsync(db.Database));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await db.Database.MigrateAsync(timeout.Token);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.False(await MigrationLock.ReleaseStaleAsync(db.Database));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Eine_neue_datenbank_hat_nichts_zu_entfernen()
    {
        var file = Path.Combine(Path.GetTempPath(), "matfile-lock-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using var db = Context(file);
            Assert.False(await MigrationLock.ReleaseStaleAsync(db.Database));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    private static MatFileDbContext Context(string file)
        => new(new DbContextOptionsBuilder<MatFileDbContext>().UseSqlite("Data Source=" + file).Options);
}
