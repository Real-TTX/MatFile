# MatFile

Web-basierter Datei-Explorer / File-Manager für NAS-Systeme (TrueNAS & Co.), die von Haus aus
keinen komfortablen Zugriff auf Dateien bieten.

- Mehrere frei konfigurierbare **Home-/Root-Pfade** (nur diese sind sichtbar)
- Zusätzliche Freigaben per **SMB, SFTP, FTP und WebDAV** – ohne Zusatzrechte im Container
  (NFS folgt über einen OS-Mount)
- Explorer-Bedienung: Sidebar, **Tabs**, Menüleiste, Favoriten, Mehrfachauswahl, **Rechtsklick-Menü**
- Upload, Download, ZIP/Entpacken, Kopieren/Verschieben, **Preview mit echtem Streaming**
- **Texteditor mit Syntaxhervorhebung** für JSON, XML, Markdown, YAML, Code & Co. –
  ansehen und direkt bearbeiten
- **Erweiterte Suche als eigener Tab** (über das `+` in der Tab-Leiste): rekursiv über alle
  Unterordner, auf Wunsch im Dateiinhalt, mit Fortschritt und Sprung in den Trefferordner
- Lokale Benutzerverwaltung mit **Rollensystem**, Sessions überleben den Container-Neustart
- Verwaltung von Benutzern, Rollen und Speicherorten **als Dialoge im Explorer** – ohne
  eigenen Adminbereich
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
| M7b | NFS über OS-Mount, Mounts nur für die Sitzung | offen |
| M8–M9 | Icon-Feinschliff, Thumbnails, Härtung | offen |

Integrationstests gegen den laufenden Container: siehe [tests/README.md](tests/README.md).

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

Für den produktiven Betrieb in `/data/config/matfile.json` setzen:

```json
{ "RequirePasswordChangeOnFirstLogin": true }
```

Dann verlangt MatFile bei der ersten Anmeldung ein eigenes Kennwort. Die Oberfläche startet
auf Englisch; über den Weltkugel-Knopf unten links in der Seitenleiste (oder *Einstellungen →
Sprache…* in der Menüleiste) lässt sich Deutsch wählen – die Auswahl wird am Benutzerkonto
gespeichert. Direkt darunter steht die laufende Version.

Benutzer, Rollen und Speicherorte werden über die **Menüleiste über den Tabs** verwaltet
(*Einstellungen* bzw. *Speicherorte*). Jeder Punkt erscheint nur mit dem passenden Recht.

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

## Branches & Versionierung

| Branch | Zweck | Version |
|---|---|---|
| `main` | Release | `<major>.<minor>.<buildnummer>-<yyyyMMdd>` |
| `dev` | Development | `nightly-<buildnummer>-<yyyyMMdd>` |
| lokal | – | `local-<yyyyMMdd>` |

`Major`/`Minor` stehen in [`version.props`](version.props), die Buildnummer liefert die
GitHub Action (`github.run_number`). Images landen in der GitHub Container Registry.
