# MatFile — Implementierungsplan

> Web-basierter Datei-Explorer / File-Manager für NAS-Systeme (TrueNAS & Co.),
> die von Haus aus keinen komfortablen Dateizugriff bieten.

---

## 1. Entscheidungen (abgestimmt)

| Thema | Entscheidung |
|---|---|
| Backend | ASP.NET Core 10 (SDK 10.0.203 lokal vorhanden) |
| Frontend | **Razor Pages + Vanilla JS (ES-Module)**, kein Node-Build, keine JS-Bibliothek |
| Styling | **Eigenes CSS-Designsystem** (CSS-Variablen, Light/Dark), angelehnt an Synology FileStation / Finder |
| Remote-Zugriff | **In-Process-Provider** (SFTP/FTP/WebDAV/SMB) **+ optionaler OS-Mount** (NFS, alternativ SMB) |
| Sprachen | **Englisch (Standard)** und **Deutsch**, umschaltbar im UI (.resx-Ressourcen) |
| Datenbank | SQLite (Logik) + JSON (Konfiguration) |
| Container-Port | **2222** |
| Deployment | Docker, Volume für `/data` |

---

## 2. Architektur-Überblick

```
Browser
  │  Razor Pages (Server-Rendering: Admin-CRUD, Login, App-Shell)
  │  JSON-API  (/api/fs/*)  ← Explorer-Interaktion via Fetch + Alpine
  │  Streaming (/files/*)   ← Download, Preview, Range-Requests
  ▼
ASP.NET Core 10  (Kestrel :2222)
  ├─ Auth        Cookie → SessionToken → UserSession (SQLite)
  ├─ Services    UserService, RoleService, RootService, MountService,
  │              FileService, ArchiveService, PreviewService, JobService
  ├─ Storage     IStorageProvider
  │                ├─ LocalProvider      (Root-Paths, OS-Mounts)
  │                ├─ SftpProvider       (SSH.NET)
  │                ├─ FtpProvider        (FluentFTP)
  │                ├─ WebDavProvider     (HttpClient)
  │                └─ SmbProvider        (SMBLibrary)
  └─ Data        EF Core + SQLite  (/data/matfile.db)

Volume /data
  ├─ matfile.db          SQLite
  ├─ config/*.json       Konfiguration
  ├─ keys/               DataProtection-Keys (Session/Cookies überleben Restart)
  ├─ thumbs/             Thumbnail-Cache
  └─ temp/               Chunk-Uploads, ZIP-Staging
```

### Projektstruktur (flach gehalten — keine tiefen Verschachtelungen)

```
MatFile/
├─ src/MatFile.Web/
│  ├─ Program.cs
│  ├─ Pages/                 Login, Explorer, Admin/{Users,Roles,Roots,Mounts,Sessions,Log}
│  ├─ Pages/Shared/Controls/ _Table, _Pagination, _FormLayout, _TabBar, _Toolbar, _Confirm
│  ├─ Api/                   FsEndpoints, UploadEndpoints, StreamEndpoints, JobEndpoints
│  ├─ Services/              Business-Logik (mehrere Funktionen je Klasse erlaubt)
│  ├─ Storage/               IStorageProvider + Implementierungen + PathGuard
│  ├─ Data/                  MatFileDbContext, Entities, AuditInterceptor, Seed
│  └─ wwwroot/               css/, js/explorer/, js/vendor/alpine.min.js, icons/
├─ docker/                   Dockerfile, entrypoint.sh
├─ .github/workflows/        docker-build.yml
├─ docs/                     PLAN.md, ARCHITECTURE.md
├─ docker-compose.yml        Release
├─ docker-compose.dev.yml    Development
└─ MatFile.sln
```

---

## 3. Datenmodell (SQLite)

**Konventionen:** Tabellen PascalCase · PK immer `Id` (BIGINT, Autoincrement) ·
jeder Datensatz mit `CreateDate`, `CreateUserId`, `UpdateDate`, `UpdateUserId`
(automatisch über EF-Core-`SaveChanges`-Interceptor) · sicherheitsrelevante
Schlüssel zusätzlich als `Token` (UUID).

| Tabelle | Felder (Kern) |
|---|---|
| `User` | Username, DisplayName, Email, PasswordHash (PBKDF2), IsActive, LastLoginDate |
| `Role` | Name, Description, IsSystem, Permissions (Flags) |
| `UserRole` | UserId, RoleId |
| `UserSession` | **Token (UUID)**, UserId, ExpiresDate, LastActivityDate, IpAddress, UserAgent, IsRevoked |
| `StorageRoot` | Name, Type (Local/Smb/Nfs/Sftp/Ftp/WebDav), DisplayIcon, BasePath, SortOrder, IsEnabled, Persistence (Persistent/Session), SettingsJson, SecretEncrypted |
| `RootAccess` | StorageRootId, RoleId, CanRead/CanWrite/CanDelete/CanUpload/CanDownload/CanShare |
| `Bookmark` | UserId, Name, StorageRootId, RelativePath, DisplayIcon, SortOrder |
| `ExplorerTab` | UserId, Title, StorageRootId, RelativePath, SortOrder, IsPersistent, IsActive |
| `UserSetting` | UserId, SettingKey, SettingValue (View-Modus, Sortierung, Theme, Sidebar-Breite) |
| `JobTask` | **Token (UUID)**, UserId, JobType (Zip/Unzip/Copy/Move/Delete/Upload), Status, ProgressPercent, PayloadJson, ResultPath, ErrorMessage |
| `ActivityLog` | UserId, ActionType, StorageRootId, TargetPath, DetailsJson, IpAddress |

**JSON-Konfiguration** (`/data/config/matfile.json`): Upload-Limits, Chunk-Größe,
Thumbnail-Einstellungen, Session-Lebensdauer, Feature-Toggles, erlaubte Archivformate.

---

## 4. Sicherheit

- **PathGuard**: jeder Pfad wird kanonisiert und gegen den Root geprüft. Blockiert werden
  `..`-Segmente, absolute Pfade, ungültige Zeichen und Symlinks, deren Ziel den Root verlässt;
  solche Einträge erscheinen erst gar nicht im Listing. Zentral, ohne Ausnahme für alle Provider.
- Ein Root darf **nicht** auf das MatFile-Datenverzeichnis zeigen oder es enthalten
  (dort liegen Datenbank und Schlüssel) — wird beim Speichern abgelehnt.
- **Credentials** der Remote-Verbindungen mit ASP.NET DataProtection verschlüsselt;
  Keys im Volume → überleben Restart. Session-Mounts speichern *nie* Zugangsdaten in der DB.
- **Login-Rate-Limit** + Lockout, Anti-Forgery auf allen POSTs, Secure/HttpOnly/SameSite-Cookie.
- **Autorisierung** zweistufig: Rolle (globale Fähigkeiten) → `RootAccess` (pro Root/Mount).
- Upload-Größenlimit, MIME-Sniffing-Schutz (`X-Content-Type-Options`), Downloads immer
  mit `Content-Disposition: attachment` außer bei explizitem Preview.

---

## 5. UI-Konzept

### Explorer (Hauptanwendung)
- **Sidebar** (Finder/Explorer-Stil): Home-/Root-Paths, aktive Mounts, Favoriten/Bookmarks,
  zuletzt verwendet. Ein-/ausklappbar, per Drag sortierbar, auf Mobile als Off-Canvas.
- **Tab-Leiste** wie Windows Explorer: `+`, Schließen, Mittelklick, Drag-Reorder,
  Kontextmenü „Tab anheften/persistent machen" → gespeichert in `ExplorerTab`.
- **Toolbar**: Zurück/Vor/Hoch, Breadcrumb-Pfad, Suche, Sortierung, Ansicht (Liste/Kacheln), Upload, Neu.
- **Dateiliste**: Mehrfachauswahl (Klick, Ctrl+Klick, Shift+Klick, Rubber-Band, Ctrl+A),
  Spalten sortierbar, Inline-Rename, Drag&Drop (intern verschieben, extern hochladen).
- **Kontextmenü (Rechtsklick)**: Öffnen · Vorschau · Download · Umbenennen · Kopieren ·
  Ausschneiden · Einfügen · Als ZIP herunterladen · Entpacken · Neuer Ordner ·
  Zu Favoriten · Eigenschaften · *(Abstand)* Löschen.
- **Tastatur**: F2, Entf, Ctrl+C/X/V, Ctrl+A, Enter, Backspace, Pfeiltasten, F5.
- **Vorschau-Overlay**: Bild, Video/Audio (echtes Streaming per HTTP-Range), PDF, Text/Code,
  Blättern mit Pfeiltasten.
- **Transfer-Panel** unten rechts: laufende Uploads/Jobs mit Fortschritt und Abbruch.

### Admin-Bereich (nach den CRUD-Guidelines)
- Liste + **separate Unterseite** zum Erstellen/Bearbeiten.
- Über der Tabelle eine **Toolbar**: Suchtext, Filter, Sortierung.
- Listen-Aktionen **unterhalb**, linksbündig; **Delete mit Abstand** abgesetzt.
- Direkt unter der Tabelle nur die **Pagination**.
- Button-Reihenfolge positiv → negativ: `Speichern · Zurück · ⟨Spacer⟩ · Löschen`.
- Icons wo sinnvoll, durchgängig mobil optimiert.

### Wiederverwendbare Controls

Umgesetzt als **TagHelper** im Ordner `Controls/`, jeweils mit eigener **Instanz-Id**,
damit mehrere gleiche Controls auf einer Seite kollisionsfrei funktionieren:

| Tag | Zweck |
|---|---|
| `<mf-table>` | Tabelle; Spalten inkl. Sortierlinks aus `columns`, Zeilen als Inhalt |
| `<mf-pagination>` | Seitenwechsel, hängt direkt unter der Tabelle |
| `<mf-toolbar>` | Leiste über der Liste (Suche, Filter, Seitengröße) |
| `<mf-field>` | Formularfeld mit Beschriftung, Hinweis und Validierungsmeldung |
| `<mf-actions>` / `<mf-actions-gap>` | Buttonzeile positiv → negativ, Delete abgesetzt |
| `<mf-tabbar>` | Tab-Leiste |
| `<mf-icon>` | Inline-SVG-Icons ohne externe Bibliothek |

Sortier- und Seitenparameter lassen sich über `param-prefix` je Control trennen.

### Mehrsprachigkeit

- Alle Oberflächentexte liegen in `Resources/SharedResource.resx` (neutral = **Englisch**)
  und `SharedResource.de.resx` (**Deutsch**). Keine festen Strings in Views oder Services.
- Reihenfolge der Spracherkennung: **Culture-Cookie → Accept-Language → Englisch**.
- Angemeldete Benutzer bekommen ihre Wahl in `UserSetting` (`Language`) gespeichert;
  beim Anmelden wird daraus das Cookie gesetzt, die Sprache folgt also dem Konto.
- Umschalter (EN/DE) sitzt in der Topbar und funktioniert auch auf der Anmeldeseite.
- Daten in der Datenbank (Rollennamen, Root-Namen) werden **nicht** übersetzt.
- Datums- und Zahlenformate folgen der gewählten Kultur (ICU im Container aktiv).

### Visueller Abgleich mit dem Mockup

Referenz: [`docs/mockups/Mockup-Sample1.png`](mockups/Mockup-Sample1.png) — als Richtung zu verstehen,
nicht als Pixelvorgabe.

Bereits übernommen:

- Aufbau und Reihenfolge des Rechtsklick-Menüs (Öffnen · Herunterladen · Ausschneiden/Kopieren/Einfügen ·
  Umbenennen/Löschen · Komprimieren/Entpacken · Eigenschaften), Löschen in Rot
- Checkbox-Spalte in der Liste inklusive „alles auswählen" in der Kopfzeile
- Statuszeile mit „x ausgewählt (Größe)" und „n Einträge"
- Dunkles Design mit blauem Akzent, Seitenleiste mit Favoriten

Noch offen (eigener Feinschliff-Durchgang, siehe M8):

| Punkt | Milestone |
|---|---|
| App-Icon mit „M" statt Ordnersymbol, komplettes Favicon-Set | M8 |
| ~~Detailbereich rechts mit Vorschau und den Reitern Details/Aktivität~~ | ✅ M6 |
| Seitenleiste um „Übersicht/Home" und den Abschnitt „Verbindungen" mit Statuspunkt | M7 |
| Tabs in die Kopfzeile, globale Suche und Benutzer-Avatar oben rechts | M8 |
| Mehrere Ansichtsmodi im Umschalter, Menü „Teilen" | M8 / später |

### Icon
Eigenes SVG-Logo (Ordner + Datei, Farbverlauf), daraus generiert:
`favicon.ico` (16/32/48), `favicon.svg`, `apple-touch-icon.png` (180),
PWA-Icons 192/512 + `site.webmanifest`.

---

## 6. Docker & Betrieb

**Dockerfile** (Multi-Stage): `sdk:10.0` → publish → `aspnet:10.0` (Debian).
Im Runtime-Image zusätzlich `cifs-utils`, `nfs-common` (nur für optionalen OS-Mount),
`ca-certificates`, `tzdata`. `ffmpeg` optional per Build-Arg (Video-Thumbnails).

```yaml
services:
  matfile:
    image: matfile:local
    ports: ["2222:2222"]
    environment:
      ASPNETCORE_HTTP_PORTS: "2222"
      MATFILE_DATA: "/data"
    volumes:
      - matfile-data:/data
      - /mnt/tank:/storage/tank        # Beispiel: NAS-Volume
    # für NFS/SMB-OS-Mounts optional:
    # cap_add: [SYS_ADMIN]
    # devices: ["/dev/fuse"]
```

- `docker-compose.yml` → Release, `docker-compose.dev.yml` → Development.
- **Test-Workflow: Container neu bauen und Stack redeployen** (kein `dotnet watch`).
- Healthcheck auf `/healthz`, Non-Root-User wo möglich (bei OS-Mounts nicht möglich → dokumentiert).

---

## 7. Git, CI & Versionierung

- Branches: **`main`** (Release) und **`dev`** (Development).
- GitHub Action `docker-build.yml`: Build + Push nach GHCR, Buildx-Cache, Tag-Berechnung:

| Kontext | Schema | Beispiel |
|---|---|---|
| `main` | `<major>.<minor>.<run_number>-<yyyyMMdd>` | `1.0.42-20260812` |
| `dev` | `nightly-<run_number>-<yyyyMMdd>` | `nightly-118-20260812` |
| lokal | `local-<yyyyMMdd>` | `local-20260812` |

`Major`/`Minor` liegen in `version.props` im Repo, Buildnummer = `github.run_number`.
Version wird als Build-Arg ins Image und in die UI (Footer/About) durchgereicht.
Commits und PRs laufen ausschließlich unter **Matthias Schmoldt** — keine Co-Author-Trailer.

---

## 8. Meilensteine

| # | Inhalt | Ergebnis |
|---|---|---|
| **M0** | Repo-Skeleton, Branches `main`/`dev`, .gitignore, Solution, Dockerfile, Compose dev/release, GitHub Action, Versionierung | Container startet auf `:2222`, zeigt Platzhalterseite |
| **M1** | EF Core + SQLite, alle Tabellen, Audit-Interceptor, Seed-Admin, Login/Logout, DB-gestützte Session (restart-fest), Rollen & Policies, **Mehrsprachigkeit EN/DE** | Anmeldung funktioniert, Session überlebt Neustart |
| **M2** | Designsystem-CSS, Layout, Controls (`_Table`, `_Pagination`, `_FormLayout`, `_TabBar`, `_Toolbar`), Admin-CRUD für User & Rollen | Admin-Bereich nach Guidelines nutzbar |
| **M3** | `IStorageProvider` + `LocalProvider`, `PathGuard`, Root-Verwaltung im UI, `RootAccess`-Rechte, Listing-API (`/api/fs/roots`, `/api/fs/list`) | Mehrere Home-/Root-Paths konfigurierbar und browsebar |
| **M4** | Explorer-Shell: Sidebar, Breadcrumb, Liste/Kacheln, Mehrfachauswahl, Sortierung, Tabs (inkl. persistent), Bookmarks, **Download** (aus M5 vorgezogen) | Navigation komplett |
| **M5** | Datei-Operationen: Upload (chunked/resumable), ZIP-Download, Komprimieren, Entpacken, Copy/Cut/Paste, Move, Rename, Delete, Neuer Ordner, Job-Queue mit Fortschritt | Voller Funktionsumfang lokal |
| **M6** | Preview/Streaming: Range-Requests, Bild/Video/Audio/PDF/Text, Overlay-Viewer, Detailbereich mit Protokoll | Medien direkt im Browser |
| **M7** | Remote-Provider **SFTP, FTP und WebDAV**, Verbindungsdaten im UI, Verbindungstest, Credential-Verschlüsselung, Abschnitt „Verbindungen" in der Seitenleiste mit Statuspunkt | Externe Freigaben eingebunden |
| **M7b** | SMB (SMBLibrary), NFS über OS-Mount, Mounts nur für die Sitzung | Alle Protokolle |
| **M8** | App-Icon & Favicon-Set, PWA-Manifest, Mobile-Feinschliff, Dark-Mode, Tastatur-Shortcuts | Optisch fertig |
| **M9** | Härtung: Rate-Limit, ActivityLog, Limits, Fehlerseiten, README/Doku, Tests der Kern-Services | Release-fähig |

---

## 9. Offene Punkte / Annahmen

- **NFS** wird nur über OS-Mount unterstützt (kein brauchbarer .NET-Client) → benötigt
  `CAP_SYS_ADMIN` im Container; ohne diese Capability blendet das UI NFS mit Hinweis aus.
- **Vorschau** wird nur für eine feste Liste von Dateitypen inline ausgeliefert (Bild, Video, Audio, PDF).
  HTML und SVG gehören bewusst **nicht** dazu, sonst könnte fremdes Skript auf unserer Domain laufen und
  das Sitzungscookie abgreifen; beide werden stattdessen als Quelltext angezeigt. Jede Vorschauantwort
  trägt zusätzlich `X-Content-Type-Options: nosniff` und eine restriktive Content-Security-Policy.
- **Zugangsdaten** der Freigaben liegen mit DataProtection verschlüsselt in der Datenbank,
  die Schlüssel im Volume unter `/data/keys`. Ohne das Volume sind sie wertlos.
- **SFTP** baut derzeit je Operation eine Verbindung auf. Das ist robust, aber bei vielen
  kleinen Dateien langsam — ein Verbindungspool wäre die nächste Optimierung.
- **FTP** nutzt im passiven Modus **EPSV**: der Server antwortet dabei nur mit einem Port,
  während PASV eine IP-Adresse meldet, die aus einem Container heraus oft unerreichbar ist.
- Ein FTP-Server chrootet den Benutzer nicht zwangsläufig. Der **Basispfad** des Roots
  begrenzt den Zugriff — genau dafür gibt es ihn; der Explorer kommt nicht darüber hinaus.
- **WebDAV**: Apache leitet Ordner ohne abschließenden Schrägstrich per 301 um, und
  `HttpClient` verwirft beim automatischen Folgen den Authorization-Header. Der Provider
  folgt Umleitungen deshalb selbst; automatisches Folgen ist im Handler abgeschaltet.
- **Kennwortwechsel beim ersten Start** ist über `RequirePasswordChangeOnFirstLogin`
  in `matfile.json` steuerbar und standardmäßig **aus**, damit `admin`/`admin` beim
  Entwickeln direkt funktioniert. Für den produktiven Betrieb einschalten.
- **Thumbnails** in der Kachelansicht fehlen noch: dafür wird eine Bildbibliothek gebraucht
  (ImageSharp oder SkiaSharp). Offen bis M8, die Vorschau selbst funktioniert ohne.
- **Video-Thumbnails** brauchen `ffmpeg` (+ ~100 MB Image). Standard: aus, per Build-Arg aktivierbar.
- **Rollen** starten mit `Admin` und `User`; feinere Rechte laufen über `RootAccess` je Root/Mount.
- Kein Node/npm im Build und keine JS-Bibliothek: der Explorer läuft mit eigenen ES-Modulen
  (`wwwroot/js/explorer/`). Alpine.js hätte eine vendorte Fremddatei bedeutet und wurde
  nicht gebraucht. Node wird nur für die optionale Oberflächenprüfung `tests/ui-check.mjs` benötigt.
- Übersetzungen für das JavaScript kommen als JSON-Bundle aus der Razor-Seite (`window.matfileText`),
  damit es auch im Explorer keine fest verdrahteten Texte gibt.
- SQLite kann `DateTimeOffset` nicht vergleichen — alle Zeitstempel liegen als UTC-`DateTime`
  in der Datenbank (globaler Value-Converter im DbContext).
- Freigabe-Links (Public Share) sind **nicht** Teil dieses Plans; das Datenmodell lässt sie
  aber später leicht ergänzen.
