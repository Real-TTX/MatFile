using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MatFile.Web.Data;

/// <summary>
/// Die Sperre, die EF Core während der Migration der SQLite-Datenbank setzt (eine Zeile in <c>__EFMigrationsLock</c>). Wird ein Prozess mitten
/// darin gestoppt, bleibt die Zeile stehen, und jeder spätere Start wartet ewig auf sie ("Acquiring an exclusive lock for migration application").
/// Das passiert bei einem Update: Mattiverse startet den Stack nach dem Backup wieder und erzeugt ihn Sekunden später neu. MatFile ist der einzige
/// Prozess, der seine Datenbank öffnet; eine Zeile, die vor der Migration gefunden wird, ist also immer verwaist und wird entfernt.
/// </summary>
public static class MigrationLock
{
    public const string TableName = "__EFMigrationsLock";

    /// <summary>Entfernt die Sperre eines gestoppten Prozesses; gibt zurück, ob es eine gab.</summary>
    public static async Task<bool> ReleaseStaleAsync(DatabaseFacade database, CancellationToken ct = default)
    {
        if (!database.IsSqlite())
        {
            return false;
        }

        var connection = database.GetDbConnection();
        await database.OpenConnectionAsync(ct);
        try
        {
            await using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = '" + TableName + "'";
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(ct)) == 0)
            {
                return false;
            }

            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM \"" + TableName + "\"";
            return await delete.ExecuteNonQueryAsync(ct) > 0;
        }
        finally
        {
            await database.CloseConnectionAsync();
        }
    }
}
