# Integrationstests

Die Skripte laufen gegen den **laufenden Container** und prüfen die Anwendung von außen
(HTTP, Cookies, Antiforgery). Beide erwarten eine **frische Datenbank**, weil sie mit dem
Standardkonto `admin` / `admin` beginnen und dessen Kennwort im Verlauf ändern.

```powershell
# Stack frisch aufsetzen
docker compose -f docker-compose.dev.yml down -v
docker compose -f docker-compose.dev.yml up -d --build

# Anmeldung, Sitzung, Sprachen (M1)
.\tests\test-auth.ps1

# Danach erneut zuruecksetzen, dann Adminbereich (M2)
docker compose -f docker-compose.dev.yml down -v
docker compose -f docker-compose.dev.yml up -d
.\tests\test-admin.ps1
```

Weicht das Startkennwort ab (z. B. per `MATFILE_ADMIN_PASSWORD` gesetzt), lässt es sich
mit `-SeedPassword <kennwort>` übergeben.

| Skript | Prüft |
|---|---|
| `test-auth.ps1` | Zugriffsschutz, Anmeldung, Kontosperre, erzwungener Kennwortwechsel, Sitzung übersteht Container-Neustart, Abmeldung, Sprachumschaltung und gespeicherte Sprachwahl, `/healthz` |
| `test-admin.ps1` | Adminnavigation, Liste mit Toolbar/Sortierung/Suche/Pagination, Benutzer anlegen/löschen, doppelter Benutzername, Rollen anlegen/löschen, Schutz von Systemrollen und des eigenen Kontos, Rechteprüfung für Nicht-Administratoren, Adminbereich auf Deutsch |

Beide Skripte geben pro Prüfung `OK`/`FAIL` aus und beenden sich mit Exitcode 1,
sobald eine Prüfung fehlschlägt.
