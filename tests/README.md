# Integrationstests

Die Skripte laufen gegen den **laufenden Container** und prüfen die Anwendung von außen
(HTTP, Cookies, Antiforgery). Sie beginnen mit dem Standardkonto `admin` / `admin`.
`test-auth.ps1` ändert dessen Kennwort und setzt es am Ende wieder zurück – dafür senkt es
kurz die Mindestlänge über `/api/system`, weil `admin` sonst zu kurz wäre. Am saubersten
bleibt trotzdem eine **frische Datenbank** je Suite.

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

# Zuruecksetzen, dann Dateioperationen, Upload und Jobs (M5)
docker compose -f docker-compose.dev.yml down -v
docker compose -f docker-compose.dev.yml up -d
.\tests\test-operations.ps1

# Zuruecksetzen, dann Vorschau und Streaming (M6)
docker compose -f docker-compose.dev.yml down -v
docker compose -f docker-compose.dev.yml up -d
.\tests\test-preview.ps1
```

Alle Suiten hintereinander:

```powershell
foreach ($t in 'test-auth','test-admin','test-storage','test-explorer','test-operations',
               'test-preview','test-search','test-remote') {
    docker compose -f docker-compose.dev.yml down -v | Out-Null
    docker compose -f docker-compose.dev.yml up -d | Out-Null
    Start-Sleep -Seconds 7
    & ".\tests\$t.ps1"
}
```

`test-operations.ps1`, `test-preview.ps1` und `test-search.ps1` arbeiten bewusst in
containerinternen Ordnern (`/storage/optest`, `/storage/pvtest`, `/storage/suchtest`),
damit `sample-data/` aus dem Repo nicht mit Testdateien verschmutzt wird.

Gemeinsame Hilfen stehen in `_helpers.ps1`: Anmeldung, JSON-Aufrufe mit Statuscode und
Fehlerkörper sowie das Anlegen von Speicherorten, Benutzern und Rechtematrizen über `/api`.
Seit die Verwaltung in Dialogen steckt, gibt es keine Adminseiten mehr, gegen die man testen
könnte – die Suiten sprechen direkt die API an, die auch die Dialoge benutzen.

## Oberflächenprüfung im Browser

`ui-check.mjs` startet Edge im Headless-Modus, meldet sich an, öffnet den Explorer und
prüft, was die HTTP-Tests nicht sehen: ob das JavaScript ohne Fehler läuft, ob Seitenleiste,
Tabs, Breadcrumb und Liste gefüllt sind, ob Auswahl und Kontextmenü reagieren. Am Ende
entsteht ein Screenshot. Der Exitcode ist 1, sobald eine Konsolenmeldung als Fehler auftaucht.

```powershell
# Setzt einen bereits angelegten Speicherort voraus, z. B. nach test-explorer.ps1
node .\tests\ui-check.mjs http://localhost:2222 admin admin .\explorer.png

# Speicherort über das Pluszeichen anlegen, bearbeiten, entfernen
node .\tests\ui-check-locations.mjs http://localhost:2222 admin .\rootflow.png

# Erweiterte Suche als Tab: Pluszeichen, Fortschritt, Inhaltssuche, Sprung in den Ordner
node .\tests\ui-check-search.mjs http://localhost:2222 admin Suche .\search.png

# Verwaltungsdialoge: Benutzer, Rollen, Rechtematrix eines Speicherorts, Systemeinstellungen
node .\tests\ui-check-manage.mjs http://localhost:2222 admin .\manage.png

# Texteditor: Syntaxhervorhebung, Breite, Bearbeiten und Speichern
node .\tests\ui-check-editor.mjs http://localhost:2222 admin Demo .\editor.png
```

Benötigt Node (nur für diesen Check, nicht für den Build) und Microsoft Edge.

Weicht das Startkennwort ab (z. B. per `MATFILE_ADMIN_PASSWORD` gesetzt), lässt es sich
mit `-SeedPassword <kennwort>` übergeben.

| Skript | Prüft |
|---|---|
| `test-auth.ps1` | Zugriffsschutz, Anmeldung, Kontosperre, erzwungener Kennwortwechsel, Sitzung übersteht Container-Neustart, Abmeldung, Sprachumschaltung und gespeicherte Sprachwahl, `/healthz` |
| `test-admin.ps1` | Verwaltung über die API: Benutzerliste mit Suche/Sortierung/Seiten, Benutzer anlegen/ändern/löschen, doppelter Benutzername, Kennwortpflicht, Rollen anlegen/ändern/löschen samt Rechtekatalog, Schutz von Systemrollen, benutzten Rollen und des eigenen Kontos, Rechteprüfung für Nicht-Administratoren, entfernte Adminseiten, **Kennwortwechsel je Benutzer** (Anmeldung führt zum Wechsel, Kennzeichen fällt danach weg), **Systemeinstellungen** lesen/speichern inkl. Begrenzung der Werte, sofort wirkender Mindestlänge und Sperre ohne `ManageSettings`, Oberfläche auf Deutsch |
| `test-storage.ps1` | Speicherorte anlegen inkl. Pfadprüfungen (relativ, fehlend, Datenverzeichnis, fehlender Name, doppelter Name), Rechtematrix je Rolle inkl. nachträglicher Erweiterung, Listing-API, Sortierung Ordner vor Dateien, ausgeblendete Symlinks nach außen, blockierte Pfadausbrüche, Sichtbarkeit je Rolle, API ohne Anmeldung gesperrt, **Speicherort auf der Containerwurzel `/`** mit unsichtbarem Datenverzeichnis |
| `test-search.ps1` | Suche nach Namen über mehrere Ebenen, Ordner als Treffer, Schreibweise egal, Suche ab einem Unterordner, Inhaltssuche mit Fundstelle als Textzeile, übersprungene Binärdateien, versteckte Dateien nur auf Wunsch, Fortschrittszählung, Abbruch, stückweises Nachladen der Treffer, zu kurze Eingabe, Inhaltssuche ohne Downloadrecht gesperrt, fremde Suche nicht lesbar |

| `test-remote.ps1` | Verbindungstest mit richtigem und falschem Kennwort, Zugangsdaten verschlüsselt gespeichert (Klartext-Suche in der Datenbank), **SFTP, WebDAV, FTP und SMB** jeweils lesen/schreiben/umbenennen/löschen, **Kopieren zwischen lokalem Speicher und Gegenstelle in beide Richtungen**, Pfadausbruch blockiert, Basispfad begrenzt den Zugriff, Statusabfrage für die Seitenleiste (erreichbar und Ausfall). Startet die Testserver `sftp`, `webdav`, `ftp` und `smb` über das Compose-Profil `testing`. |
| `test-preview.ps1` | **Bearbeiten von Textdateien** (speichern, veralteter Stand, nur Textdateien, zu große Dateien schreibgeschützt, Protokolleintrag, Rechte), Vorschautypen im Listing, Bild inline mit Schutzheadern, Video per Range-Anfrage, **HTML und unbekannte Typen nie inline**, Textvorschau inkl. Kürzung großer Dateien, Protokoll zu einem Eintrag, Vorschau ohne Downloadrecht und ohne Anmeldung gesperrt |
| `test-operations.ps1` | **Datei anlegen** (leer, sofort bearbeitbar, doppelter und ungültiger Name, ohne Schreibrecht gesperrt, Protokolleintrag), Ordner anlegen/umbenennen, Namenskonflikte, Upload in Teilstücken inkl. Fortsetzen bei falschem Offset, Kopieren/Verschieben als Job, Kopie mit neuem Namen, Zielordner im Quellordner, Komprimieren, ZIP-Download, Entpacken, **Zip-Slip blockiert**, Löschen, Rechteprüfung ohne Schreib-/Upload-/Löschrecht, fremde Jobs nicht abbrechbar |
| `test-explorer.ps1` | Explorer als Startseite, eingebettete Übersetzungen, ES-Module, Anzeigeeinstellungen inkl. Normalisierung, Favoriten anlegen/löschen/nicht doppelt, angeheftete Tabs mit Filterung fremder Roots, Download inkl. Range-Anfrage, Ordner nicht herunterladbar, Pfadausbruch blockiert, Download ohne Recht gesperrt, Zustand je Benutzer getrennt |

`test-storage.ps1` legt über `docker exec` Testdaten unter `/storage/internal` im Container an
(inklusive eines Symlinks nach `/etc`, um die Pfadprüfung zu testen).

Beide Skripte geben pro Prüfung `OK`/`FAIL` aus und beenden sich mit Exitcode 1,
sobald eine Prüfung fehlschlägt.
