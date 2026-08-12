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

# Und noch einmal zuruecksetzen, dann Speicherorte und API (M3)
docker compose -f docker-compose.dev.yml down -v
docker compose -f docker-compose.dev.yml up -d
.\tests\test-storage.ps1

# Zuruecksetzen, dann Explorer-Zustand, Favoriten, Tabs und Download (M4)
docker compose -f docker-compose.dev.yml down -v
docker compose -f docker-compose.dev.yml up -d
.\tests\test-explorer.ps1
```

## Oberflächenprüfung im Browser

`ui-check.mjs` startet Edge im Headless-Modus, meldet sich an, öffnet den Explorer und
prüft, was die HTTP-Tests nicht sehen: ob das JavaScript ohne Fehler läuft, ob Seitenleiste,
Tabs, Breadcrumb und Liste gefüllt sind, ob Auswahl und Kontextmenü reagieren. Am Ende
entsteht ein Screenshot. Der Exitcode ist 1, sobald eine Konsolenmeldung als Fehler auftaucht.

```powershell
# Setzt einen bereits angelegten Root voraus, z. B. nach test-explorer.ps1
node .\tests\ui-check.mjs http://localhost:2222 admin MatFile-Test-2026 .\explorer.png
```

Benötigt Node (nur für diesen Check, nicht für den Build) und Microsoft Edge.

Weicht das Startkennwort ab (z. B. per `MATFILE_ADMIN_PASSWORD` gesetzt), lässt es sich
mit `-SeedPassword <kennwort>` übergeben.

| Skript | Prüft |
|---|---|
| `test-auth.ps1` | Zugriffsschutz, Anmeldung, Kontosperre, erzwungener Kennwortwechsel, Sitzung übersteht Container-Neustart, Abmeldung, Sprachumschaltung und gespeicherte Sprachwahl, `/healthz` |
| `test-admin.ps1` | Adminnavigation, Liste mit Toolbar/Sortierung/Suche/Pagination, Benutzer anlegen/löschen, doppelter Benutzername, Rollen anlegen/löschen, Schutz von Systemrollen und des eigenen Kontos, Rechteprüfung für Nicht-Administratoren, Adminbereich auf Deutsch |
| `test-storage.ps1` | Root-Pfade anlegen inkl. Pfadprüfungen (relativ, fehlend, Datenverzeichnis, doppelter Name), Rechtematrix je Rolle, Listing-API, Sortierung Ordner vor Dateien, ausgeblendete Symlinks nach außen, blockierte Pfadausbrüche, Sichtbarkeit je Rolle, API ohne Anmeldung gesperrt |

| `test-explorer.ps1` | Explorer als Startseite, eingebettete Übersetzungen, ES-Module, Anzeigeeinstellungen inkl. Normalisierung, Favoriten anlegen/löschen/nicht doppelt, angeheftete Tabs mit Filterung fremder Roots, Download inkl. Range-Anfrage, Ordner nicht herunterladbar, Pfadausbruch blockiert, Download ohne Recht gesperrt, Zustand je Benutzer getrennt |

`test-storage.ps1` legt über `docker exec` Testdaten unter `/storage/internal` im Container an
(inklusive eines Symlinks nach `/etc`, um die Pfadprüfung zu testen).

Beide Skripte geben pro Prüfung `OK`/`FAIL` aus und beenden sich mit Exitcode 1,
sobald eine Prüfung fehlschlägt.
