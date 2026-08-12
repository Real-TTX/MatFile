# MatFile

Web-basierter Datei-Explorer / File-Manager für NAS-Systeme (TrueNAS & Co.), die von Haus aus
keinen komfortablen Zugriff auf Dateien bieten.

- Mehrere frei konfigurierbare **Home-/Root-Pfade** (nur diese sind sichtbar)
- Zusätzliche Freigaben per **SMB, NFS, SFTP, FTP, WebDAV** – dauerhaft oder nur für die Sitzung
- Explorer-Bedienung: Sidebar, **Tabs**, Favoriten, Mehrfachauswahl, **Rechtsklick-Menü**
- Upload, Download, ZIP/Entpacken, Kopieren/Verschieben, **Preview mit echtem Streaming**
- Lokale Benutzerverwaltung mit **Rollensystem**, Sessions überleben den Container-Neustart
- Oberfläche in **Englisch (Standard)** und **Deutsch**, im UI umschaltbar

Der vollständige Umsetzungsplan liegt in [docs/PLAN.md](docs/PLAN.md).

## Stand

| Meilenstein | Inhalt | Status |
|---|---|---|
| M0 | Grundgerüst, Docker, CI, Versionierung | ✅ |
| M1 | Datenbank, Anmeldung, Rollen, Sessions, Mehrsprachigkeit | ✅ |
| M2 | Designsystem, Controls, Admin-CRUD | ✅ |
| M3 | Storage-Abstraktion, PathGuard, Root-Verwaltung, Listing-API | ✅ |
| M4 | Explorer: Sidebar, Tabs, Navigation, Mehrfachauswahl, Favoriten, Download | ✅ |
| M5 | Dateioperationen, Upload, ZIP/Unzip, Job-Queue mit Fortschritt | ✅ |
| M6 | Vorschau und Streaming, Detailbereich mit Protokoll | ✅ |
| M7–M9 | Remote-Mounts (SMB/NFS/SFTP/FTP/WebDAV), Thumbnails, Feinschliff | offen |

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

Nach der Anmeldung verlangt MatFile sofort ein eigenes Kennwort. Die Oberfläche startet
auf Englisch; über den Schalter `EN | DE` in der Kopfzeile lässt sich Deutsch wählen –
die Auswahl wird am Benutzerkonto gespeichert.

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
