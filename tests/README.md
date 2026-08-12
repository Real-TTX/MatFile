# Integrationstests

Die Skripte laufen gegen den **laufenden Container** und prüfen die Anwendung von außen
(HTTP, Cookies, Antiforgery). Beide erwarten eine **frische Datenbank**, weil sie mit dem
erzeugten Startkennwort von `admin` beginnen.

```powershell
# Stack frisch aufsetzen
docker compose -f docker-compose.dev.yml down -v
docker compose -f docker-compose.dev.yml up -d --build

# Startkennwort aus dem Log holen
$pw = (docker compose -f docker-compose.dev.yml logs --no-log-prefix matfile |
       Select-String 'Startkennwort: (\S+)').Matches.Groups[1].Value

# Anmeldung, Sitzung, Sprachen (M1)
.\tests\test-auth.ps1 -SeedPassword $pw

# Danach erneut zuruecksetzen, dann Adminbereich (M2)
.\tests\test-admin.ps1 -SeedPassword $pw
```

| Skript | Prüft |
|---|---|
| `test-auth.ps1` | Zugriffsschutz, Anmeldung, Kontosperre, erzwungener Kennwortwechsel, Sitzung übersteht Container-Neustart, Abmeldung, Sprachumschaltung und gespeicherte Sprachwahl, `/healthz` |
| `test-admin.ps1` | Adminnavigation, Liste mit Toolbar/Sortierung/Suche/Pagination, Benutzer anlegen/löschen, doppelter Benutzername, Rollen anlegen/löschen, Schutz von Systemrollen und des eigenen Kontos, Rechteprüfung für Nicht-Administratoren, Adminbereich auf Deutsch |

Beide Skripte geben pro Prüfung `OK`/`FAIL` aus und beenden sich mit Exitcode 1,
sobald eine Prüfung fehlschlägt.
