# MatFile

Web-basierter Datei-Explorer / File-Manager für NAS-Systeme (TrueNAS & Co.), die von Haus aus
keinen komfortablen Zugriff auf Dateien bieten.

- Mehrere frei konfigurierbare **Home-/Root-Pfade** (nur diese sind sichtbar)
- Zusätzliche Freigaben per **SMB, SFTP, FTP und WebDAV** – ohne Zusatzrechte im Container
  (NFS folgt über einen OS-Mount)
- Explorer-Bedienung: Sidebar, **Tabs**, Menüleiste, Favoriten, Mehrfachauswahl, **Rechtsklick-Menü**
- Upload, Download, ZIP/Entpacken, Kopieren/Verschieben, **Preview mit echtem Streaming**
- **Dateien anlegen** (.txt, .json, .md, .xml …) und **Texteditor mit Syntaxhervorhebung** –
  ansehen und direkt bearbeiten
- **Erweiterte Suche als eigener Tab** (über das `+` in der Tab-Leiste): rekursiv über alle
  Unterordner, auf Wunsch im Dateiinhalt, mit Fortschritt und Sprung in den Trefferordner
- **Zugriffsrechte** (rwx für Besitzer, Gruppe, Andere) über *Eigenschaften* setzen –
  bei lokalen Pfaden, SFTP und FTP; SMB und WebDAV kennen sie nicht
- Lokale Benutzerverwaltung mit **Rollensystem**, Sessions überleben den Container-Neustart
- **Administrationsbereich** im Explorer: Benutzer, Rollen, Speicherorte,
  Systemeinstellungen und **Protokoll** in einem Fenster, erreichbar über die Menüleiste
- **Rate-Limit** für Anmeldeversuche zusätzlich zur Kontosperre
- Oberfläche in **Englisch (Standard)** und **Deutsch**, im UI umschaltbar

Der vollständige Umsetzungsplan liegt in [docs/PLAN.md](docs/PLAN.md).

## Stand

| Meilenstein | Inhalt | Status |
|---|---|---|
| M0 | Grundgerüst, Docker, CI, Versionierung | ✅ |
| M1 | Datenbank, Anmeldung, Rollen, Sessions, Mehrsprachigkeit | ✅ |
| M2 | Designsystem, Controls, Verwaltung (Benutzer, Rollen, Speicherorte) | ✅ |
| M3 | Storage-Abstraktion, PathGuard, Root-Verwaltung, Listing-API | ✅ |
| M4 | Explorer: Sidebar, Tabs, Navigation, Mehrfachauswahl, Favoriten, Download | ✅ |
| M5 | Dateioperationen, Upload, ZIP/Unzip, Job-Queue mit Fortschritt | ✅ |
| M6 | Vorschau und Streaming, Detailbereich mit Protokoll | ✅ |
| M7 | Remote-Verbindungen: **SMB, SFTP, FTP, WebDAV** inkl. Verbindungstest, Seitenleiste mit Statuspunkt | ✅ |
| M7c | Rekursive Suche mit Inhaltssuche, Verwaltung in Dialogen, Version in der Seitenleiste | ✅ |
| M7d | Systemeinstellungen als Dialog, Zugriffsrechte über *Eigenschaften*, eigener Teststack | ✅ |
| M7b | NFS über OS-Mount, Mounts nur für die Sitzung | offen |
| M8–M9 | Icon-Feinschliff, Thumbnails, Härtung | offen |

Unit-Tests (`dotnet test MatFile.slnx`) und Integrationstests gegen den laufenden Container:
siehe [tests/README.md](tests/README.md).

## Schnellstart

```bash
# Entwicklung: bauen und deployen
docker compose -f docker-compose.dev.yml up -d --build

# Release
docker compose up -d --build
```

Anschließend erreichbar unter **http://localhost:2222** (Health-Check: `/healthz`).

### Wichtige Einstellungen

| Variable | Standard | Bedeutung |
|---|---|---|
| `ASPNETCORE_HTTP_PORTS` | `2222` | Port im Container |
| `MATFILE_DATA` | `/data` | Datenverzeichnis (SQLite, Config, Keys, Thumbnails) |
| `PUID` / `PGID` | – | Optional: als unprivilegierter Benutzer laufen |
| `MATFILE_ADMIN_PASSWORD` | `admin` | Startkennwort für `admin` beim ersten Start |
| `TZ` | `Europe/Berlin` | Zeitzone |

### Erste Anmeldung

Beim ersten Start wird das Konto **`admin` / `admin`** angelegt. Ein anderes Startkennwort
lässt sich über `MATFILE_ADMIN_PASSWORD` vorgeben.

Für den produktiven Betrieb *Einstellungen → Systemeinstellungen* öffnen und
**Kennwortwechsel beim ersten Start** einschalten (schreibt
`"RequirePasswordChangeOnFirstLogin": true` nach `/data/config/matfile.json`).

Dann verlangt MatFile bei der ersten Anmeldung ein eigenes Kennwort. Die Oberfläche startet
auf Englisch; über den Weltkugel-Knopf unten links in der Seitenleiste (oder *Einstellungen →
Sprache…* in der Menüleiste) lässt sich Deutsch wählen – die Auswahl wird am Benutzerkonto
gespeichert. Direkt darunter steht die laufende Version.

Verwaltet wird alles im **Administrationsbereich**: *Administration* in der Menüleiste über
den Tabs öffnet ein Fenster mit den Bereichen **Benutzer, Rollen, Speicherorte,
Systemeinstellungen und Protokoll**. Der Punkt erscheint nur mit mindestens einem
Verwaltungsrecht, und die Spalte zeigt genau die Bereiche, für die das Recht da ist.

Die **Systemeinstellungen** (Recht `ManageSettings`) gelten für alle: Mindestlänge für
Kennwörter, Sitzungsdauer, Fehlversuche bis zur Sperre, Sperrdauer, Anmeldeversuche je Minute,
HTTPS-Cookie und der Kennwortwechsel beim ersten Start. Sie landen in
`/data/config/matfile.json`; Mindestlänge und Kontosperre wirken sofort, Cookie, Sitzungsdauer
und Rate-Limit nach einem Neustart des Containers.

Das **Protokoll** (Recht `ViewActivityLog`) zeigt, wer wann was gemacht hat – über alle
Speicherorte, mit Suche und Filtern nach Aktion und Speicherort.

Das Volume `/data` enthält:

```
/data
├─ matfile.db      SQLite-Datenbank
├─ config/         JSON-Konfiguration
├─ keys/           DataProtection-Keys (Sessions überleben Neustarts)
├─ thumbs/         Thumbnail-Cache
└─ temp/           Chunk-Uploads, ZIP-Staging
```

NAS-Volumes werden per Bind-Mount in den Container gelegt (z. B. `/mnt/tank:/storage/tank`)
und danach im UI als Root-Pfad angelegt.

Für OS-Level-Mounts (NFS, alternativ SMB) benötigt der Container `cap_add: [SYS_ADMIN]`;
die In-Process-Provider für SFTP/FTP/WebDAV/SMB kommen ohne Zusatzrechte aus.

## Entwicklung

Getestet wird immer über den Container – neu bauen und den Stack neu deployen:

```bash
docker compose -f docker-compose.dev.yml up -d --build
docker compose -f docker-compose.dev.yml logs -f
```

Lokal ohne Docker:

```bash
dotnet run --project src/MatFile.Web
```

Die Unit-Tests brauchen keinen Container:

```powershell
dotnet test MatFile.slnx
```

Die Integrationstests laufen auf einem **eigenen Stack** (Port 2223, eigenes Volume), damit
der Entwicklungsstack mit seinen Speicherorten stehen bleibt:

```powershell
.\tests\run-all.ps1
```

Einzelheiten dazu in [`tests/README.md`](tests/README.md).

## Branches & Versionierung

| Branch | Zweck | Version |
|---|---|---|
| `main` | Release | `<major>.<minor>.<buildnummer>-<yyyyMMdd>` |
| `dev` | Development | `nightly-<buildnummer>-<yyyyMMdd>` |
| lokal | – | `local-<yyyyMMdd>` |

`Major`/`Minor` stehen in [`version.props`](version.props), die Buildnummer liefert die
GitHub Action (`github.run_number`). Images landen in der GitHub Container Registry.
