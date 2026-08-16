# Tests

## Unit-Tests (ohne Container)

`MatFile.Tests` prüft die Bausteine, die für sich allein entscheidbar sind – vor allem
die Pfadprüfung, an der hängt, ob ein Ausbruch aus einem Speicherort möglich ist.

```powershell
dotnet test MatFile.slnx
```

| Datei | Prüft |
|---|---|
| `PathGuardTests.cs` | Normalisierung, Eltern- und Namensregeln, Auflösung gegen den Speicherort, blockierte Ausbrüche über `..` und absolute Pfade, **Speicherort auf der Wurzel** |
| `RemotePathTests.cs` | Basispfad der Gegenstelle vereinheitlichen und zusammensetzen, blockierte Ausbrüche |
| `PermissionModeTests.cs` | Bitmuster ↔ Oktalschreibweise der Zugriffsrechte, Sonderbits bleiben aussen vor, Grenzen |
| `PreviewPolicyTests.cs` | Welche Endung als was gilt und was **nie inline** ausgeliefert wird (HTML, SVG) |
| `ConnectionSettingsTests.cs` | JSON lesen/schreiben inkl. kaputter Datei, Standardports je Protokoll |
| `ConfigStoreTests.cs` | Standardwerte beim ersten Start, Speichern und Wiederlesen, kaputte oder unbekannte Einträge |
| `PasswordHasherTests.cs` | PBKDF2-Format, eigenes Salz je Hash, abgelehnte Fremdformate |
| `RootPermissionsTests.cs` | Rechte mehrerer Rollen addieren sich |

Die CI führt sie bei jedem Push mit aus (`dotnet test MatFile.slnx`).

# Integrationstests

Die Skripte laufen gegen den **laufenden Container** und prüfen die Anwendung von außen
(HTTP, Cookies, Antiforgery). Sie beginnen mit dem Standardkonto `admin` / `admin`.
`test-auth.ps1` ändert dessen Kennwort und setzt es am Ende wieder zurück – dafür senkt es
kurz die Mindestlänge über `/api/system`, weil `admin` sonst zu kurz wäre. Am saubersten
bleibt trotzdem eine **frische Datenbank** je Suite.

## Alles auf einmal: eigener Teststack

`run-all.ps1` setzt vor **jeder** Suite eine frische Datenbank auf – standardmäßig auf dem
Teststack aus `docker-compose.test.yml` (Port **2223**, eigenes Volume, Container
`matfile-test`). Der Entwicklungsstack auf Port 2222 bleibt dabei unangetastet, samt der
dort angelegten Speicherorte.

```powershell
.\tests\run-all.ps1                          # alle Suiten, Teststack
.\tests\run-all.ps1 -Suites test-search      # einzelne Suiten
.\tests\run-all.ps1 -SkipBuild               # ohne Neubau des Images
.\tests\run-all.ps1 -Dev                     # stattdessen auf 2222 - loescht dort die Daten!
```

Am Ende steht eine Zusammenfassung je Suite; der Exitcode ist 1, sobald eine Suite
Fehler meldet.

## Einzeln

Der Teststack laesst sich auch von Hand aufsetzen und einzeln beschicken:

```powershell
docker compose -f docker-compose.test.yml down -v
docker compose -f docker-compose.test.yml up -d --build

.\tests\test-auth.ps1     -BaseUrl http://localhost:2223 -Compose .\docker-compose.test.yml
.\tests\test-admin.ps1    -BaseUrl http://localhost:2223
.\tests\test-storage.ps1  -BaseUrl http://localhost:2223 -Container matfile-test
```

Dasselbe geht gegen den Entwicklungsstack (`docker-compose.dev.yml`, Port 2222,
Container `matfile-dev`) – dort löscht `down -v` allerdings auch die eigenen
Speicherorte, Benutzer und Favoriten.

`test-operations.ps1`, `test-preview.ps1` und `test-search.ps1` arbeiten bewusst in
containerinternen Ordnern (`/storage/optest`, `/storage/pvtest`, `/storage/suchtest`),
damit `sample-data/` aus dem Repo nicht mit Testdateien verschmutzt wird.

Gemeinsame Hilfen stehen in `_helpers.ps1`: Anmeldung, JSON-Aufrufe mit Statuscode und
Fehlerkörper sowie das Anlegen von Speicherorten, Benutzern und Rechtematrizen über `/api`.
Seit die Verwaltung im Administrationsbereich steckt, gibt es keine Adminseiten mehr, gegen
die man testen könnte – die Suiten sprechen direkt die API an, die auch die Oberfläche benutzt.

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

# Administrationsbereich: Benutzer, Rollen, Rechtematrix eines Speicherorts, Systemeinstellungen
node .\tests\ui-check-manage.mjs http://localhost:2222 admin .\manage.png

# Texteditor: Syntaxhervorhebung, Breite, Bearbeiten und Speichern
node .\tests\ui-check-editor.mjs http://localhost:2222 admin Demo .\editor.png

# Schmale Schirme: 1440 bis 390 px, Details statt Liste, Seitenleiste als Überlagerung
node .\tests\ui-check-mobile.mjs http://localhost:2222 admin .\mobile.png
```

`ui-check-mobile.mjs` prüft bei fünf Breiten, dass die Seite nicht waagerecht scrollt,
die Dateiliste sichtbar bleibt, die Menüleiste nicht umbricht (sondern schiebt) und dass
man auf dem Telefon aus den Details wieder zur Liste kommt. Der Exitcode ist 1, sobald
eine dieser Bedingungen kippt – die Ausgabe nennt dann Breite und Grund.

Benötigt Node (nur für diesen Check, nicht für den Build) und Microsoft Edge.

Weicht das Startkennwort ab (z. B. per `MATFILE_ADMIN_PASSWORD` gesetzt), lässt es sich
mit `-SeedPassword <kennwort>` übergeben.

| Skript | Prüft |
|---|---|
| `test-auth.ps1` | Zugriffsschutz, Anmeldung, Kontosperre, erzwungener Kennwortwechsel, Sitzung übersteht Container-Neustart, Abmeldung, Sprachumschaltung und gespeicherte Sprachwahl, `/healthz`, **Rate-Limit für Anmeldeversuche** (429 mit `Retry-After`, Anmeldeseite und übrige Aufrufe bleiben erreichbar – steht am Ende der Suite, weil die Adresse danach eine Minute gebremst ist) |
| `test-admin.ps1` | Verwaltung über die API: Benutzerliste mit Suche/Sortierung/Seiten, Benutzer anlegen/ändern/löschen, doppelter Benutzername, Kennwortpflicht, Rollen anlegen/ändern/löschen samt Rechtekatalog, Schutz von Systemrollen, benutzten Rollen und des eigenen Kontos, Rechteprüfung für Nicht-Administratoren, entfernte Adminseiten, **Kennwortwechsel je Benutzer** (Anmeldung führt zum Wechsel, Kennzeichen fällt danach weg), **Systemeinstellungen** lesen/speichern inkl. Begrenzung der Werte, sofort wirkender Mindestlänge und Sperre ohne `ManageSettings`, **Protokoll** (Seiten, Sortierung neueste zuerst, Filter nach Aktion, Suche, gesperrt ohne `ViewActivityLog` und ohne Anmeldung), Oberfläche auf Deutsch |
| `test-storage.ps1` | Speicherorte anlegen inkl. Pfadprüfungen (relativ, fehlend, Datenverzeichnis, fehlender Name, doppelter Name), Rechtematrix je Rolle inkl. nachträglicher Erweiterung, Listing-API, Sortierung Ordner vor Dateien, ausgeblendete Symlinks nach außen, blockierte Pfadausbrüche, Sichtbarkeit je Rolle, API ohne Anmeldung gesperrt, **Speicherort auf der Containerwurzel `/`** mit unsichtbarem Datenverzeichnis |
| `test-search.ps1` | Suche nach Namen über mehrere Ebenen, Ordner als Treffer, Schreibweise egal, Suche ab einem Unterordner, Inhaltssuche mit Fundstelle als Textzeile, übersprungene Binärdateien, versteckte Dateien nur auf Wunsch, Fortschrittszählung, Abbruch, stückweises Nachladen der Treffer, zu kurze Eingabe, Inhaltssuche ohne Downloadrecht gesperrt, fremde Suche nicht lesbar |

| `test-remote.ps1` | Verbindungstest mit richtigem und falschem Kennwort, Zugangsdaten verschlüsselt gespeichert (Klartext-Suche in der Datenbank), **SFTP, WebDAV, FTP und SMB** jeweils lesen/schreiben/umbenennen/löschen, **Kopieren zwischen lokalem Speicher und Gegenstelle in beide Richtungen**, Pfadausbruch blockiert, Basispfad begrenzt den Zugriff, **Zugriffsrechte über SFTP und FTP** (lesen, setzen, an der Gegenstelle mit `ls -l` geprüft, ungültiger Wert abgelehnt, fehlender Eintrag 404) und die Gegenprobe, dass SMB und WebDAV keine kennen, Statusabfrage für die Seitenleiste (erreichbar und Ausfall). Startet die Testserver `sftp`, `webdav`, `ftp` und `smb` über das Compose-Profil `testing`. |
| `test-preview.ps1` | **Bearbeiten von Textdateien** (speichern, veralteter Stand, nur Textdateien, zu große Dateien schreibgeschützt, Protokolleintrag, Rechte), Vorschautypen im Listing, Bild inline mit Schutzheadern, Video per Range-Anfrage, **HTML und unbekannte Typen nie inline**, Textvorschau inkl. Kürzung großer Dateien, Protokoll zu einem Eintrag, Vorschau ohne Downloadrecht und ohne Anmeldung gesperrt |
| `test-operations.ps1` | **Größengrenze beim Hochladen** (zu große Datei mit 413 abgelehnt, kleinere weiter erlaubt, mehr als angekündigt gestoppt, Grenze wieder aufgehoben), **Zugriffsrechte** auf einem lokalen Ordner (lesen, setzen, gegen `ls -l` geprüft, ohne Schreibrecht nur lesbar, ohne Anmeldung gesperrt), **Datei anlegen** (leer, sofort bearbeitbar, doppelter und ungültiger Name, ohne Schreibrecht gesperrt, Protokolleintrag), Ordner anlegen/umbenennen, Namenskonflikte, Upload in Teilstücken inkl. Fortsetzen bei falschem Offset, Kopieren/Verschieben als Job, Kopie mit neuem Namen, Zielordner im Quellordner, Komprimieren, ZIP-Download, Entpacken, **Zip-Slip blockiert**, Löschen, Rechteprüfung ohne Schreib-/Upload-/Löschrecht, fremde Jobs nicht abbrechbar |
| `test-explorer.ps1` | Explorer als Startseite, eingebettete Übersetzungen, ES-Module, Anzeigeeinstellungen inkl. Normalisierung, Favoriten anlegen/löschen/nicht doppelt, angeheftete Tabs mit Filterung fremder Roots, Download inkl. Range-Anfrage, Ordner nicht herunterladbar, Pfadausbruch blockiert, Download ohne Recht gesperrt, Zustand je Benutzer getrennt |

`test-storage.ps1` legt über `docker exec` Testdaten unter `/storage/internal` im Container an
(inklusive eines Symlinks nach `/etc`, um die Pfadprüfung zu testen).

Jedes Skript gibt pro Prüfung `OK`/`FAIL` aus und beendet sich mit Exitcode 1,
sobald eine Prüfung fehlschlägt. Welchen Container es anspricht, steht in `-Container`;
`test-remote.ps1` benennt die Gegenstellen über `-RemotePrefix` (`matfile-test` im Teststack).
