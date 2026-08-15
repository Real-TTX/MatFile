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
  │  Razor Pages (Server-Rendering: Login, Kennwortwechsel, App-Shell)
  │  JSON-API  (/api/fs/*, /api/users, /api/roles, /api/roots, /api/search)
  │            ← Explorer und Verwaltungsdialoge via Fetch
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
│  ├─ Pages/                 Login, Explorer (App-Shell), Account/ChangePassword, Error
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
  Beides greift auf verschiedenen Ebenen: Die **Kontosperre** schützt ein einzelnes Konto nach
  zu vielen Fehlversuchen, das **Rate-Limit** bremst den Absender (POST auf `/Login` und
  `/Account/ChangePassword`, Standard 20 je Minute) – damit auch das Durchprobieren vieler
  verschiedener Benutzernamen. Abgewiesene Versuche bekommen 429 mit `Retry-After: 60` und
  die Fehlerseite; alles andere bleibt ungebremst.
- **Autorisierung** zweistufig: Rolle (globale Fähigkeiten) → `RootAccess` (pro Root/Mount).
- Upload-Größenlimit, MIME-Sniffing-Schutz (`X-Content-Type-Options`), Downloads immer
  mit `Content-Disposition: attachment` außer bei explizitem Preview.

---

## 5. UI-Konzept

### Explorer (Hauptanwendung)
- **Sidebar** (Finder/Explorer-Stil): **ein Abschnitt „Speicherorte"** für lokale Ordner und
  Verbindungen zusammen – der Typ steckt im farbigen Symbol, der Punkt daneben zeigt die
  Erreichbarkeit einer Gegenstelle. Darunter Favoriten. Auf Mobile als Off-Canvas, über
  *Ansicht → Seitenleiste* ganz ausblendbar (die Wahl bleibt am Benutzer).
  **Ein Abschnitt ohne Einträge verschwindet ganz** – keine Überschrift ohne Inhalt. Solange
  es keinen Speicherort gibt, steht dort nur ein Knopf „Speicherort hinzufügen".
- **Tab-Leiste** wie Windows Explorer: `+`, Schließen, Mittelklick, Drag-Reorder,
  Kontextmenü „Tab anheften/persistent machen" → gespeichert in `ExplorerTab`.
- **Menüleiste über den Tabs** wie im Windows-Explorer: Datei · Bearbeiten · Ansicht ·
  Speicherorte · Einstellungen, jeweils als Ausklappmenü in der Optik des Rechtsklick-Menüs.
  Punkte ohne Recht oder ohne passende Auswahl sind ausgegraut.
- **Toolbar**: Zurück/Vor/Hoch, Breadcrumb-Pfad, Suche, Sortierung, Ansicht (Liste/Kacheln), Upload, Neu.
- **Suche**: Tippen filtert den aktuellen Ordner. Alles darüber hinaus ist die **erweiterte
  Suche in einem eigenen Tab** – das `+` in der Tab-Leiste öffnet sie, Enter im Suchfeld
  ebenso (mit dem eingetippten Begriff). Der Tab enthält Suchbegriff, Speicherort, Ordner
  und die Häkchen für Dateiinhalt und versteckte Dateien, darunter Fortschritt
  (Ordner/Dateien, aktueller Pfad), Abbruch und die Treffer mit Pfad, Fundstelle als
  hervorgehobene Textzeile und „Im Ordner anzeigen". Der Sprung landet im Ordnertab, der
  Suchtab bleibt mit seinem Ergebnis stehen. Suchtabs werden nicht gemerkt.
- **Dateiliste**: Mehrfachauswahl (Klick, Ctrl+Klick, Shift+Klick, Rubber-Band, Ctrl+A),
  Spalten sortierbar, Inline-Rename, Drag&Drop (intern verschieben, extern hochladen).
- **Kontextmenü (Rechtsklick)**: Öffnen · Vorschau · Download · Umbenennen · Kopieren ·
  Ausschneiden · Einfügen · Als ZIP herunterladen · Entpacken · Neuer Ordner ·
  Zu Favoriten · Eigenschaften · *(Abstand)* Löschen.
- **Tastatur**: F2, Entf, Ctrl+C/X/V, Ctrl+A, Enter, Backspace, Pfeiltasten, F5.
- **Vorschau-Overlay**: Bild, Video/Audio (echtes Streaming per HTTP-Range), PDF, Text/Code,
  Blättern mit Pfeiltasten. Text steht in lesbarer Breite (max. 1100 px) statt über den
  ganzen Bildschirm.
- **Neue Datei** neben *Neuer Ordner* (Werkzeugleiste, Menü *Datei*, Rechtsklick ins Leere):
  Namensabfrage mit anklickbaren Endungen (.txt, .md, .json, .xml, .yml, .csv, .log, .sh);
  danach öffnet sich die leere Datei gleich im Editor.
- **Texteditor** im selben Overlay: Syntaxhervorhebung für JSON, XML/HTML, Markdown, YAML/INI,
  Shell/Python, SQL, CSS und C-artige Sprachen, Zeilennummern, Tabulator, `Strg+S`.
  Bearbeiten nur mit Schreibrecht; gekürzt geladene (zu große) Dateien bleiben schreibgeschützt,
  und ein zwischenzeitlich geänderter Stand wird beim Speichern abgelehnt statt überschrieben.
- **Transfer-Panel** unten rechts: laufende Uploads/Jobs mit Fortschritt und Abbruch.

### Verwaltung (nach den CRUD-Guidelines)

Die Verwaltung sitzt in **einem Administrationsbereich**: ein Fenster, das der Punkt
*Administration* in der Menüleiste öffnet, mit einer Spalte links (Benutzer, Rollen,
Speicherorte, Systemeinstellungen, Protokoll) und der jeweiligen Ansicht rechts. Es gibt weder
eigene Adminseiten noch verteilte Einzeldialoge – die Verwaltung bleibt im Explorer,
aber an einer Stelle. Der Punkt erscheint nur, wenn der Benutzer mindestens eines der
Verwaltungsrechte hat; die Spalte zeigt genau die Bereiche, die er benutzen darf.

Jeder Bereich bekommt beim Wechsel **eigene Elemente**. Lädt ein vorher offener Bereich
noch nach, schreibt er in seine abgehängten Knoten statt in die neue Ansicht – sonst
gewinnt beim schnellen Wechseln die langsamere Antwort.

Die CRUD-Guidelines gelten in jedem Bereich:

- Liste und Bearbeiten sind **getrennte Ansichten** desselben Bereichs (statt Unterseite).
- Über der Tabelle eine **Toolbar**: Suchtext, Filter, „hinzufügen".
- Direkt unter der Tabelle nur die **Pagination**.
- Aktionen der Liste **unterhalb**, linksbündig; **Löschen mit Abstand** abgesetzt.
- Button-Reihenfolge positiv → negativ: `Speichern · Zurück · ⟨Spacer⟩ · Löschen`.
- Icons wo sinnvoll, durchgängig mobil optimiert.

Der Dialog für Speicherorte selbst:

- **Typkacheln ohne Überschrift und ohne Erklärzeile**, jeder Typ mit eigener Farbe und
  festem Symbol – eine eigene Symbolauswahl gibt es nicht mehr, das Symbol folgt dem Typ
  (auch in der Seitenleiste).
- **Ordnerauswahl**: Der Knopf am Pfadfeld öffnet einen Browser der Gegenstelle – mit den
  gerade eingetragenen Zugangsdaten, also bevor der Speicherort gespeichert ist. Bei SMB
  ohne Freigabe erscheinen zuerst die **Freigaben** des Servers.
- **Erweitert** (aufklappbar) für Port, Sortierung, TLS, passiven Modus und „aktiv".

Verbindungsdaten eines Speicherorts bearbeitet weiterhin der eigene, plakative Dialog;
die **Rechtematrix je Rolle** sitzt im Bereich *Speicherorte*. Alles spricht dieselbe
JSON-API (`/api/users`, `/api/roles`, `/api/roots`, `/api/system`), gegen die auch die
Tests laufen.

Gestaltung der Dialoge:

- **Feste Größe** je Dialog (Verwaltung 860×620, Speicherort 720×760). Ohne das springt
  der Rahmen beim Wechsel zwischen Liste und Formular oder beim Umschalten des Speichertyps.
- Formulare laufen über das gemeinsame **zweispaltige Raster** (`.mf-form-grid`); nur was
  lang wird (WebDAV-Adresse, Rollen- und Rechteauswahl) nimmt die ganze Zeile.
- Beschriftungen klein und gesperrt, Eingaben durchgehend 34 px hoch, Mehrfachauswahl in
  einem umrandeten Kasten mit mehreren Spalten.
- Der Verbindungsdialog kommt für **jeden Speichertyp ohne Blättern** aus.

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
- Die Sprachwahl ist ein **Knopf mit Weltkugel und Kürzel**, der einen Dialog öffnet:
  im Explorer unten links in der Seitenleiste, sonst in der Kopfzeile. Auf der Anmeldeseite
  funktioniert er ebenso.
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
`favicon.ico` (16/32/48), `matfile.svg`, `apple-touch-icon.png` (180),
PWA-Icons 192/512 (dazu eine maskierbare Fassung) + `site.webmanifest`.

Erzeugt werden sie von `tools/make-icons.mjs`: Edge rendert das SVG im
Headless-Modus in den gewünschten Kantenlängen, die `favicon.ico` setzt das
Skript selbst aus den PNGs zusammen. So bleibt es bei einer Quelle - dem SVG -
ohne npm-Werkzeugkette. Nach einer Änderung am Logo das Skript einmal laufen
lassen und die PNGs mit einchecken.

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
| **M2** | Designsystem-CSS, Layout, Controls (`_Table`, `_Pagination`, `_FormLayout`, `_TabBar`, `_Toolbar`), CRUD für User & Rollen | Verwaltung nach Guidelines nutzbar |
| **M3** | `IStorageProvider` + `LocalProvider`, `PathGuard`, Root-Verwaltung im UI, `RootAccess`-Rechte, Listing-API (`/api/fs/roots`, `/api/fs/list`) | Mehrere Home-/Root-Paths konfigurierbar und browsebar |
| **M4** | Explorer-Shell: Sidebar, Breadcrumb, Liste/Kacheln, Mehrfachauswahl, Sortierung, Tabs (inkl. persistent), Bookmarks, **Download** (aus M5 vorgezogen) | Navigation komplett |
| **M5** | Datei-Operationen: Upload (chunked/resumable), ZIP-Download, Komprimieren, Entpacken, Copy/Cut/Paste, Move, Rename, Delete, Neuer Ordner, Job-Queue mit Fortschritt | Voller Funktionsumfang lokal |
| **M6** | Preview/Streaming: Range-Requests, Bild/Video/Audio/PDF/Text, Overlay-Viewer, Detailbereich mit Protokoll | Medien direkt im Browser |
| **M7** | Remote-Provider **SMB, SFTP, FTP und WebDAV**, Verbindungsdaten im UI, Verbindungstest, Credential-Verschlüsselung, Abschnitt „Verbindungen" in der Seitenleiste mit Statuspunkt | Externe Freigaben eingebunden |
| **M7c** | Menüleiste über den Tabs, Sprachauswahl als Dialog, **erweiterte Suche als eigener Tab** (rekursiv, mit Inhaltssuche), Verwaltung als Dialoge (Adminseiten entfernt), Version in der Seitenleiste, starre Hülle gegen Layoutsprünge | Bedienung aus einer Fläche |
| **M7d** | **Administrationsbereich** (Benutzer, Rollen, Speicherorte, Systemeinstellungen in einem Fenster, `ManageSettings`), **Zugriffsrechte im Eigenschaftenblatt** (lokal, SFTP, FTP), Icon-Set und PWA-Manifest, eigener Teststack | Verwaltung vollständig |
| **M7b** | NFS über OS-Mount, Mounts nur für die Sitzung | Letzte Protokolle |
| **M8** | App-Icon & Favicon-Set, PWA-Manifest, Mobile-Feinschliff, Dark-Mode, Tastatur-Shortcuts | Optisch fertig |
| **M9** | Härtung: ~~Rate-Limit~~, ~~Protokollansicht~~, Limits, Fehlerseiten, README/Doku, Tests der Kern-Services | Release-fähig |

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
- **SMB** läuft über SMBLibrary, also ohne OS-Mount und ohne Zusatzrechte im Container.
  Der Provider liest nie über das Dateiende hinaus — manche Server beenden dabei die
  Verbindung — und schließt Handles auch dann sauber, wenn die Verbindung schon weg ist.
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
  Der Schalter gilt nur für das mitgelieferte Konto beim Anlegen der Datenbank; für
  bestehende Benutzer steht dasselbe im Benutzerdialog je Konto.
- **Zugriffsrechte** stehen im Eigenschaftenblatt, sobald der Provider `IPermissionStorage`
  anbietet: lokal über `File.GetUnixFileMode`/`SetUnixFileMode` (unter Windows gibt es sie
  nicht), SFTP über `ChangePermissions`, FTP über `SITE CHMOD` - und auch nur, wenn der
  Server die Rechte im Listing mitschickt. SMB und WebDAV kennen kein solches Modell;
  dort meldet die API `supported: false` und der Dialog zeigt den Abschnitt gar nicht erst.
  Ändern setzt `CanWrite` am Speicherort voraus und landet im Protokoll.
- **Protokoll** (`ViewActivityLog`, Bereich in der Administration) zeigt alle Einträge des
  `ActivityLog` über sämtliche Speicherorte, neueste zuerst, mit Suche und Filtern nach
  Aktion und Speicherort. Geschrieben werden die Einträge weiterhin dort, wo die Aktion
  passiert; die Ansicht liest nur.
- **Systemeinstellungen** (`ManageSettings`, Bereich in der Administration) schreiben
  `matfile.json` zur Laufzeit: Mindestlänge für Kennwörter, Sitzungsdauer, Fehlversuche
  und Sperrdauer, HTTPS-Cookie, Kennwortwechsel beim ersten Start. Werte werden serverseitig
  auf gültige Bereiche begrenzt. Was aus der Datenbank kommt (Kennwortprüfung, Sperre),
  wirkt sofort; was beim Start verdrahtet wird (Cookie-Optionen), erst nach einem Neustart.
- Ein Speicherort darf auf die **Containerwurzel `/`** zeigen. MatFiles eigenes `/data`
  blendet der lokale Provider dabei aus und verweigert den Zugriff darauf — sonst lägen
  Datenbank und Schlüssel im Explorer offen. `/data` selbst bleibt als Pfad verboten.
- Die **rekursive Suche** läuft im Hintergrund über `IStorageProvider`, also auch über
  SFTP/FTP/WebDAV/SMB. Ergebnisse leben nur im Speicher (max. 500 Treffer, 20.000 Ordner,
  24 Ebenen, Symlinks werden nicht verfolgt) und verfallen 15 Minuten nach dem Ende.
  Einen echten Prozentwert gibt es nicht — angezeigt wird der Anteil der erledigten an den
  bekannten Ordnern. Die **Inhaltssuche** verlangt zusätzlich das Downloadrecht, überspringt
  Dateien über 8 MB und bricht beim ersten Nullbyte als Binärdatei ab.
- Der **Texteditor** ist selbst geschrieben: ein Eingabefeld mit durchsichtiger Schrift liegt
  deckungsgleich über dem eingefärbten Text, ein eigener Tokenizer je Sprachfamilie erledigt
  die Hervorhebung. CodeMirror oder Monaco hätten eine vendorte Fremdbibliothek bedeutet -
  dieselbe Entscheidung wie gegen Alpine.js. Ab 200 000 Zeichen wird nicht mehr eingefärbt,
  damit das Tippen flüssig bleibt; bearbeitbar sind Dateien bis 512 KB.
- **Thumbnails** in der Kachelansicht fehlen noch: dafür wird eine Bildbibliothek gebraucht
  (ImageSharp oder SkiaSharp). Offen bis M8, die Vorschau selbst funktioniert ohne.
- **Video-Thumbnails** brauchen `ffmpeg` (+ ~100 MB Image). Standard: aus, per Build-Arg aktivierbar.
- **Rollen** starten mit `Admin` und `User`; feinere Rechte laufen über `RootAccess` je Root/Mount.
- Alle Leisten der Hülle (Kopfzeile, Menüleiste, Tab-Leiste, Werkzeugleiste, Statuszeile)
  sind `flex: 0 0 auto` mit fester Höhe. Ohne das nimmt der Umbruch ihnen Höhe weg, sobald
  der Inhalt wächst — die Kopfzeile schrumpfte von 52 auf 37 px und die Seite wackelte bei
  jedem Neuladen und jedem Klick. Die Dateiliste hat zusätzlich `scrollbar-gutter: stable`,
  damit die Spalten beim Erscheinen der Bildlaufleiste nicht seitwärts rutschen.
- Kein Node/npm im Build und keine JS-Bibliothek: der Explorer läuft mit eigenen ES-Modulen
  (`wwwroot/js/explorer/`). Alpine.js hätte eine vendorte Fremddatei bedeutet und wurde
  nicht gebraucht. Node wird nur für die optionale Oberflächenprüfung `tests/ui-check.mjs` benötigt.
- Übersetzungen für das JavaScript kommen als JSON-Bundle aus der Razor-Seite (`window.matfileText`),
  damit es auch im Explorer keine fest verdrahteten Texte gibt.
- SQLite kann `DateTimeOffset` nicht vergleichen — alle Zeitstempel liegen als UTC-`DateTime`
  in der Datenbank (globaler Value-Converter im DbContext).
- Freigabe-Links (Public Share) sind **nicht** Teil dieses Plans; das Datenmodell lässt sie
  aber später leicht ergänzen.
