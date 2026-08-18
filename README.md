<div align="center">

<img src="src/MatFile.Web/wwwroot/icons/matfile-192.png" width="96" alt="MatFile" />

# MatFile

**A file explorer for your NAS – in the browser.**

Several storage locations, SMB/SFTP/FTP/WebDAV, tabs, preview, search, users and permissions.
One container, no cloud, no third-party services.

</div>

![The explorer with sidebar, folder tree and details pane](docs/images/explorer.png)

---

## What this is about

TrueNAS, Synology and the like store your data reliably – but getting to a single file means
going through shares, drive letters or SSH. MatFile puts a file explorer on top of them that
runs in the browser: local paths and network shares side by side, with tabs, preview, search
and a user management that decides who may see and change what.

## At a glance

**Storage locations**
- Several freely configurable **home/root paths** – only these are visible
- Shares via **SMB, SFTP, FTP and WebDAV**, without extra privileges in the container
  (NFS will follow through an OS mount)
- **SMB without a share as well**: leave the field empty and every share of the server shows
  up as a folder on the top level
- Credentials are stored **encrypted**, with a connection test before saving
- **Reachability** per location right in the sidebar: a green dot or a red cross

**Using it**
- **"All Locations"** as the root above every storage location – including free space
- **Tabs** like in Windows Explorer: pin, reorder, middle-click to close
- Sidebar with an **expandable folder tree** (per user, off by default) and a draggable width
- Menu bar, right-click menu, multi-select, rubber-band selection, drag & drop
- **Keyboard shortcuts** for everything that matters – **F1** shows the list
- **Light and dark**, English and German, off-canvas layout on narrow screens

**Files**
- Upload (large files in chunks), download, **zip and unzip**
- Copy and move, **across storage locations** too, with progress and cancel
- **Preview with real streaming** for images, video, audio, PDF and text
- **Create files** and a **text editor with syntax highlighting**
- **Advanced search in its own tab**: recursive, optionally inside file contents, with the
  matching line
- **Access rights** (rwx) through *Properties* – local, SFTP and FTP

**Users and operations**
- Local user management with **roles** and a permission matrix per storage location
- **Administration area**: users, roles, storage locations, system settings, activity log
- Account lockout and a **rate limit** on sign-in attempts, forced password change
- Sessions survive a container restart, **activity log** across all actions

## Screenshots

### "All Locations" – every storage location with free space

![Overview of all storage locations as cards](docs/images/all-locations.png)

Every location as a card with type, usage and state. One click opens it in the same tab, the
first breadcrumb leads back – the way "This PC" works in Windows Explorer.

### Search in its own tab

![Search tab with results](docs/images/search.png)

Typing filters the current folder, Enter opens the recursive search – optionally inside file
contents, with progress, cancel and a jump into the folder of a hit.

### Preview and text editor

| Preview | Editor |
|---|---|
| ![Image preview](docs/images/preview.png) | ![Text editor with syntax highlighting](docs/images/editor.png) |

Images, video, audio, PDF and text are streamed, not downloaded first. Text files can be
edited and saved right away.

### Access rights in the properties sheet

![Properties with the rwx matrix](docs/images/properties.png)

Read, write and execute for owner, group and others – on local paths, SFTP and FTP. Storage
types without POSIX permissions simply hide the section.

### Administration in one place

| Users | Storage locations | Activity log |
|---|---|---|
| ![User management](docs/images/users.png) | ![Storage locations](docs/images/storage-locations.png) | ![Activity log](docs/images/activity-log.png) |

Users, roles, storage locations, system settings and the activity log in a single window.
Only the sections you hold a permission for are shown.

### Light theme and narrow screens

| Light | Phone | Sidebar |
|---|---|---|
| ![Light theme](docs/images/light-theme.png) | ![File list on a phone](docs/images/mobile.png) | ![Sidebar as an overlay](docs/images/mobile-sidebar.png) |

## Quick start

```bash
docker compose up -d --build
```

Then available at **http://localhost:2222** (health check: `/healthz`).
On first start the account **`admin` / `admin`** is created.

NAS volumes are bind-mounted into the container and then added as a storage location in the UI:

```yaml
services:
  matfile:
    volumes:
      - matfile-data:/data
      - /mnt/tank:/storage/tank      # <- your own share
```

There is a separate stack for development:

```bash
docker compose -f docker-compose.dev.yml up -d --build
```

### Settings that matter

| Variable | Default | Meaning |
|---|---|---|
| `ASPNETCORE_HTTP_PORTS` | `2222` | Port inside the container |
| `MATFILE_DATA` | `/data` | Data directory (SQLite, config, keys, thumbnails) |
| `PUID` / `PGID` | – | Optional: run as an unprivileged user |
| `MATFILE_ADMIN_PASSWORD` | `admin` | Initial password for `admin` on first start |
| `TZ` | `Europe/Berlin` | Time zone |

### After the first sign-in

*Administration* in the menu bar opens the admin area with **users, roles, storage locations,
system settings and the activity log**. For production, turn on **password change on first
sign-in** there – MatFile will then ask for a password of your own the first time.

The **system settings** apply to everyone: minimum password length, session lifetime, failed
attempts until lockout, lockout duration, sign-in attempts per minute, maximum upload size
(0 = no limit), the HTTPS cookie and the password change on first sign-in. They end up in
`/data/config/matfile.json`; minimum length and lockout take effect immediately, cookie,
session lifetime and rate limit after a container restart.

The interface starts in English; the globe button at the bottom left (or *Settings →
Language…*) switches to German – the choice stays with the user account.

### The `/data` volume

```
/data
├─ matfile.db      SQLite database
├─ config/         JSON configuration
├─ keys/           DataProtection keys (sessions survive restarts)
├─ thumbs/         thumbnail cache
└─ temp/           chunked uploads, zip staging
```

OS-level mounts (NFS, or SMB as an alternative) need `cap_add: [SYS_ADMIN]`; the in-process
providers for SMB, SFTP, FTP and WebDAV work without extra privileges.

## Status

| Milestone | Content | Status |
|---|---|---|
| M0 | Skeleton, Docker, CI, versioning | ✅ |
| M1 | Database, sign-in, roles, sessions, localisation | ✅ |
| M2 | Design system, controls, management (users, roles, storage locations) | ✅ |
| M3 | Storage abstraction, PathGuard, root management, listing API | ✅ |
| M4 | Explorer: sidebar, tabs, navigation, multi-select, favourites, download | ✅ |
| M5 | File operations, upload, zip/unzip, job queue with progress | ✅ |
| M6 | Preview and streaming, details pane with activity | ✅ |
| M7 | Remote connections: **SMB, SFTP, FTP, WebDAV** incl. connection test | ✅ |
| M7c | Recursive search with content search, management in dialogs | ✅ |
| M7d | System settings, access rights in *Properties*, separate test stack | ✅ |
| M7e | Administration area, **"All Locations"** with free space, folder tree, connection pool | ✅ |
| M7b | NFS through an OS mount, mounts for the session only | open |
| M8–M9 | Thumbnails, icon polish, hardening | open |

The full implementation plan lives in [docs/PLAN.md](docs/PLAN.md) (German).

## Development

Building and testing happens through the container:

```bash
docker compose -f docker-compose.dev.yml up -d --build
docker compose -f docker-compose.dev.yml logs -f
```

Locally without Docker:

```bash
dotnet run --project src/MatFile.Web
```

The unit tests need no container:

```powershell
dotnet test MatFile.slnx
```

The integration tests run against a **separate stack** (port 2223, its own volume) so the
development stack keeps its storage locations:

```powershell
.\tests\run-all.ps1
```

On top of that there are browser checks driving headless Edge (no npm involved) – details in
[`tests/README.md`](tests/README.md) (German).

## How it is built

- **ASP.NET Core 10** (Razor Pages + minimal API), **EF Core** with SQLite
- The interface is **plain JavaScript** (ES modules) – no framework, no build step
- Storage providers behind one interface: `Local`, `Smb` (SMBLibrary), `Sftp` (SSH.NET),
  `Ftp` (FluentFTP), `WebDav`
- Connections to remote servers go through a **pool** – reused instead of dialled per request
- Credentials encrypted via **DataProtection**, paths guarded against escapes by `PathGuard`

Source comments, the plan and the test documentation are written in German.

## Branches & versioning

| Branch | Purpose | Version |
|---|---|---|
| `main` | Release | `<major>.<minor>.<build>-<yyyyMMdd>` |
| `dev` | Development | `nightly-<build>-<yyyyMMdd>` |
| local | – | `local-<yyyyMMdd>` |

`Major`/`Minor` live in [`version.props`](version.props), the build number comes from the
GitHub action (`github.run_number`). Images are published to the GitHub Container Registry.
