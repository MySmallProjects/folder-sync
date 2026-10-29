# FolderSyncEr

A cross-device file synchronization system built with .NET 8, featuring:
1. **[ASP.NET](http://asp.net) Core Web API Server**: Storage and metadata backend with SQLite (EF Core), JWT authentication, streaming file uploads/downloads, optimistic concurrency, and soft-delete tombstones.
2. **Avalonia Desktop Client**: Cross-platform MVVM client (Windows & Ubuntu) utilizing **Oire.SharpSync (1.0.4)** as the client-side synchronization engine, with OS-native credential storage (`ISecretStore`).
3. **Shared Contracts**: Common DTOs, route constants, and cross-platform path validation rules.

---

## Tech Stack & Architecture

- **Runtime**: .NET 8 (LTS)
- **Server**: ASP.NET Core Minimal APIs, Entity Framework Core 8, SQLite, Serilog, Swashbuckle (Swagger/OpenAPI).
- **Client**: Avalonia UI (CommunityToolkit.Mvvm), `sqlite-net-pcl`, OS credential managers (Windows Credential Manager / DPAPI, Linux Secret Service / libsecret).
- **Sync Engine**: `Oire.SharpSync` (pure .NET, Apache 2.0) running **strictly on the client**. The client speaks to the server via a custom REST adapter (`RestRemoteStorage : ISyncStorage`).

---

## Solution Layout

```
FolderSyncEr/
├── FolderSyncEr.sln
├── README.md                           # This document
├── Directory.Build.targets             # Build settings (disables telemetry)
├── src/
│   ├── FolderSyncEr.Contracts/         # Shared DTOs, route/header constants, PathRules
│   ├── FolderSyncEr.Server/            # Web API backend, EF Core DB, blob store
│   ├── FolderSyncEr.Client.Platform/   # OS-native ISecretStore (Windows Credential Manager, Linux Secret Service, fallback)
│   ├── FolderSyncEr.Client.Api/        # Typed HTTP API client with auth token manager and auto-refresh handler
│   ├── FolderSyncEr.Client.Sync/       # SharpSync integration: RestRemoteStorage, SafeLocalStorage, GuardedSyncDatabase, PolicyConflictResolver, SyncCoordinator
│   └── FolderSyncEr.Client.App/        # Avalonia UI MVVM desktop application
└── tests/
    ├── FolderSyncEr.Server.IntegrationTests/ # End-to-end Web API integration tests
    ├── FolderSyncEr.Client.Tests/            # Client platform & API integration tests
    └── FolderSyncEr.Client.Sync.Tests/       # Sync engine, adapter, conflict resolver & decorator tests
```

---

## Getting Started: Running the Server

### 1. Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or newer.
- SQLite is self-contained via `Microsoft.EntityFrameworkCore.Sqlite`.

### 2. Run the Web API Server
From the solution root directory:

```bash
dotnet run --project src/FolderSyncEr.Server
```

By default, the server will launch and listen on:
- `http://localhost:5000` (or `https://localhost:5001` if HTTPS is configured)
- **Web File Viewer**: [http://localhost:5000/](http://localhost:5000/) (built-in HTML/JS TreeView to sign in and inspect/download server files)
- **Interactive Swagger UI**: [http://localhost:5000/swagger](http://localhost:5000/swagger)

### 3. Default Credentials (Seeded on First Startup)
When the server starts up for the first time, it automatically creates the SQLite database (`foldersyncer.db`) and seeds an initial administrator account:

| Property | Default Value | Notes |
|---|---|---|
| **Username** | `admin` | Configurable in `appsettings.json` under `Bootstrap:AdminUsername` |
| **Password** | `AdminPassword123!` | Configurable in `appsettings.json` under `Bootstrap:AdminPassword` |
| **Role** | `Admin` | Has access to `/api/settings` and cross-user logs |

### 4. Configuration (`appsettings.json`) & Token Lifetimes
Key configuration sections in `src/FolderSyncEr.Server/appsettings.json`:
- `ConnectionStrings:DefaultConnection`: SQLite connection string (`Data Source=foldersyncer.db`).
- `Storage:RootPath`: Directory where file blobs are persisted (defaults to `storage/`).
- `Storage:MaxUploadBytes`: Maximum file upload size in bytes (defaults to `4294967296` = 4 GB).
- `Storage:VersionRetentionCount`: Number of previous file versions retained for recovery (defaults to `5`).
- `Jwt:Secret`: Secret key used for signing JWT access tokens (must be at least 256 bits).
- `Jwt:ExpiryMinutes`: Lifetime of short-lived JWT access tokens (defaults to `15` minutes).
- `Jwt:RefreshExpiryDays`: Lifetime of rotating refresh tokens (defaults to `30` days).

#### How Long Until a User Must Log In Again?
FolderSyncEr uses a two-token authentication model with **rolling refresh token rotation**:
1. **Access Token (`15 minutes` default)**: Sent as `Authorization: Bearer <token>` on API calls. When it expires, the user does **not** need to log in again.
2. **Refresh Token (`30 days` default, rolling)**: Whenever the 15-minute access token expires, the client (`AuthTokenManager`) automatically exchanges the refresh token in the background (`POST /api/auth/refresh`) for a new 15-minute access token **and** a brand-new 30-day refresh token.
3. **Effective Session Duration**: As long as the client connects to the server at least once every **30 days**, the user remains signed in indefinitely across app and machine restarts. A user only needs to sign in again if:
   - The client has been completely offline/inactive for more than 30 days,
   - The user explicitly clicks **Sign Out** (or the device is remotely signed out from the **Logged-In Devices** list), or
   - The server database is reset (e.g. via `./reset-server.sh`).

---

## Running the Desktop Client (Avalonia)

### 1. Run the Client Application
From the solution root directory:

```bash
dotnet run --project src/FolderSyncEr.Client.App
```

### 2. Client Features (Phase 2 & Phase 4 Full UI)
- **Status & Latency Checker**: Real-time roundtrip ping and server UTC time verification against `/api/status`.
- **Configurable Server URL & Port**: Enter any custom server URL and port (e.g. `http://localhost:5001`) and click **Save URL** to persist it to secure storage (`ISecretStore`) and apply it across the app.
- **Authentication & Active Devices**:
  - JWT login with automatic token refresh on expiry, session persistence across restarts, and secure logout.
  - Displays the current device name & ID (`Device: <Name> (<Id>)`) and lists all active logged-in devices (`GET /api/devices`), allowing users to remotely revoke/sign out any other device (`DELETE /api/devices/{id}`).
- **OS-Native Secret Storage (`ISecretStore`)**:
  - **Windows**: Windows Credential Manager (`advapi32.dll` CredWriteW/CredReadW/CredDeleteW) with fallback to DPAPI `ProtectedData`.
  - **Linux / Ubuntu**: FreeDesktop Secret Service via `libsecret-1.so.0` (SecretValue / secret_password_store_sync).
  - **Fallback**: Platform-independent AES-256-GCM encrypted file (`~/.config/foldersyncer/secrets.enc.bin` or `/tmp/foldersyncer/`) restricted to `0600` POSIX file permissions.
- **Synced Files View**:
  - Displays relative path, local size and UTC last-modified time, server version and SHA-256 prefix, server size and UTC last-modified time.
  - Colored sync status badges: **In sync** (green), **Local newer** (blue), **Server newer** (purple), **Conflict** (orange), **Error** (red).
  - Search box and quick filter dropdown ("All", "In sync", "Local newer", "Server newer", "Conflict", "Errors").
- **Deleted Files (Trash) View**:
  - Lists soft-deleted (tombstoned) files and folders from `/api/trash`, folding children into their deleted parent folder with item count and total size.
  - **Restore**: Restores a deleted file or folder (including contents deleted with the folder). If a file or folder now exists at the original path, restores it alongside as `name (restored yyyy-MM-dd).ext`.
  - **Delete Forever & Empty Trash**: Per-item permanent deletion and full trash purge with inline confirmation dialogs.
- **Activity Logs Viewer**:
  - Paged server-side audit logs fetched directly from `/api/logs`.
  - Optional auto-refresh toggle (disabled by default to avoid unnecessary background polling; polls every 4 seconds when enabled).
  - Filter by action (`Upload`, `Download`, `Conflict`, `Delete`, `Move`, `Login`, `Error`) and result (`Success`, `Failure`, `Conflict`, `Rejected`).
- **Interactive Conflict Dialog**:
  - Modal overlay appearing when a conflict is encountered under the `Prompt` policy.
  - Side-by-side comparison cards (Size, Last Modified UTC, SHA-256 prefix, Originating Device).
  - Interactive decision actions: **Keep Local**, **Keep Server**, and **Keep Both (Rename Loser)**.
- **Settings, Administration & Diagnostics**:
  - **Change Synced Folder**: Path validation with an explicit warning dialog explaining how changing the sync directory affects the local sync state database.
  - **Conflict Policy Selection**: Choose between `KeepBoth` (automatic loser rename), `NewestWins` (UTC timestamp comparison), or `Prompt` (interactive modal).
  - **Sync Interval**: Configurable periodic background scan frequency (seconds).
  - **Start Minimized to Tray**: Configurable toggle (`StartMinimized`, default `false`) to launch the app directly into the system tray without showing the main window on startup.
  - **Close-to-Tray**: Configurable toggle to minimize to notification tray on window close.
  - **Admin Folder Archive**: One-click download of the user's remote files bundled as a compressed ZIP archive (`GET /api/files/export`).
  - **App Settings & Log File Links**: Clickable file path links at the bottom of the Settings tab to open `settings.json` and the current daily client log file (`Logs/app-yyyy-MM-dd.log`) in the system's default text editor / viewer.
- **Client Daily File Logging (`ClientFileLoggerProvider`)**:
  - **Location**: Stored inside a `Logs` subfolder of the app's configuration directory:
    - **Linux**: `~/.config/foldersyncer/Logs/app-yyyy-MM-dd.log`
    - **Windows**: `%LOCALAPPDATA%\FolderSyncEr\Logs\app-yyyy-MM-dd.log`
  - **Retention**: One log file per UTC day (`app-yyyy-MM-dd.log`), automatically pruning log files older than **30 days**.
  - **Logged Events**:
    - Every HTTP API request (`Method` + `Url`), HTTP response status code, and network/transport error (`AuthHeaderHandler`).
    - Server connection checks succeeding or failing (`FileSyncApiClient.CheckConnectionAsync`).
    - File uploads to the server (start, byte size, resulting server version, or 409 conflict), remote directory creation, remote deletions, moves/renames, and trash restores (`FileSyncApiClient`).
    - Local file and folder change-detection events (`Created`, `Changed`, `Deleted`, `Renamed`) from `FileSystemWatcher` (`SyncCoordinator`), as well as local file writes, directory creations, moves, and `.sync-trash` operations (`SafeLocalStorage`).
- **System Tray Integration (`TrayIcon`)**:
  - **Dynamic Tray Icon States**:
    - **Grey folder icon** (`icon-offline.png`): Disconnected / unable to reach the server.
    - **Blue folder icon with green tick** (`icon-synced.png`): Connected to the server and all files are in sync.
    - **Blue folder icon** (`icon.png`): Connected and syncing or local/remote changes pending.
  - Tray menu options: **Open FolderSyncEr**, **Sync Now**, **Pause / Resume Sync**, and **Quit**.
  - **Linux Desktop Environment Caveats**:
    - Default GNOME Shell on Ubuntu does not display system tray icons out of the box unless the AppIndicator / Ayatana extension (`gnome-shell-extension-appindicator`) is enabled:
      ```bash
      sudo apt install gnome-shell-extension-appindicator
      ```
    - In desktop environments with native notification area support (KDE Plasma, XFCE, Cinnamon, MATE) or when the GNOME extension is enabled, Avalonia's `TrayIcon` integrates into the top/bottom system panel.
    - If no tray notification area is present, the window close-to-tray behavior can be toggled off in the Settings tab, allowing standard window exit.

### 3. Sync Engine & Safety Decorators (Phase 3 Integration)
- **Engine**: Powered by `Oire.SharpSync (1.0.4)` running purely on the desktop client.
- **`RestRemoteStorage : ISyncStorage`**: Custom remote storage adapter translating SharpSync operations into REST calls (`IFileSyncApi`).
  - `ListItemsAsync`: Serves instantly from an in-memory `RemoteSnapshot` without per-directory HTTP network requests (Mitigating R2).
  - `ComputeHashAsync`: Serves Base64 SHA-256 directly from `RemoteSnapshot` without downloading file content (Mitigating R2/R9).
  - `WriteFileAsync`: Streams data to `PUT /api/files` with `If-Match: "<baseSha256>"` or `If-None-Match: *`. Intercepts HTTP 409 conflict responses, notifies `RunFailureRegistry`, and aborts erroneous sync states.
- **`SafeLocalStorage : ISyncStorage`**:
  - **Atomic Writes**: Writes incoming content to `.foldersyncer/tmp/<guid>.tmp` before atomic move/replace, ensuring no partial files are left on disk (Mitigating R3).
  - **Safe Trash Deletion**: Instead of destructive `File.Delete`, files and directories are safely preserved under `.foldersyncer/trash/<yyyyMMddTHHmmssZ>/...` (Mitigating R4).
  - **Hash Caching**: Caches file hashes by `(path, size, mtimeUtc)` to eliminate expensive SHA-256 recomputation across scans for unchanged local files (Mitigating R9).
- **`GuardedSyncDatabase : ISyncDatabase`**: Wraps `SqliteSyncDatabase`, intercepting `UpdateSyncStateAsync` to suppress false `SyncStatus.Synced` records when a file operation failed or conflicted during the run (Mitigating R1). Stamps remote server versions into `SyncState.ETag` for seamless UI display.
- **`PolicyConflictResolver : IConflictResolver`**: Configurable conflict resolution:
  - `KeepBoth`: Copies the local file to `name (conflict <device> <yyyyMMddTHHmmssZ>).ext` and downloads the server version as the primary file, preventing data loss.
  - `NewestWins`: Automatically compares local vs remote last modified UTC timestamps.
  - `Prompt`: Triggers `IConflictPromptHandler` to allow interactive decision (Keep Local / Keep Server / Keep Both).
- **`SyncCoordinator`**: Top-level coordinator managing `FileSystemWatcher` debounced change detection, periodic background scans, pre-sync untracked seeding (Mitigating R8), and pause/resume lifecycle controls.

---

## Running the Tests

Integration tests run against an in-process test server using `Microsoft.AspNetCore.Mvc.Testing` (`WebApplicationFactory`), with isolated temporary SQLite databases and storage directories created per test run.

Run all tests from the repository root:

```bash
dotnet test
```

To run without restoring (in offline/sandboxed environments):

```bash
dotnet test --no-restore
```

To run with detailed test log output:

```bash
dotnet test --logger "console;verbosity=normal"
```

### Test Suites (74 Tests Total)
- **`FolderSyncEr.Server.IntegrationTests` (19 tests)**: Full ASP.NET Core Web API server tests against SQLite EF Core, verifying JWT auth, token rotation, device management & revocation, streaming uploads/downloads, atomic writes, optimistic concurrency, soft delete tombstones, and admin ZIP export.
- **`FolderSyncEr.Client.Tests` (28 tests)**: Client platform and UI viewmodel tests verifying OS secret stores (`ISecretStore`), daily rolling file logger (`ClientFileLoggerProvider`) with 30-day pruning, token management with 401 automatic single-flight token refresh and offline resilience, `FileListViewModel` (local/remote merge & badge logic), `LogsViewModel` (filters & pagination), `ConflictPromptViewModel` (interactive resolution), `SettingsViewModel` (folder migration warning, URL persistence, `StartMinimized`, `settings.json` & log file links, and ZIP export), `ClientSettingsManager` (atomic JSON loading & saving), `LoginViewModel` (active devices list & remote device sign-out), and `ConnectionCheckViewModel` (explicit URL save & persistence).
- **`FolderSyncEr.Client.Sync.Tests` (27 tests)**: SharpSync integration tests verifying `RestRemoteStorage`, `SafeLocalStorage` (atomic writes + trash recovery + hash cache), `GuardedSyncDatabase` (R1 failure suppression + version tagging), `PolicyConflictResolver` (keep-both, newest-wins, prompt), and `SyncCoordinator`.

### Integration Test Credentials
The test suite in `FolderSyncEr.Server.IntegrationTests` configures a dedicated test admin account per test fixture:
- **Username**: `testadmin`
- **Password**: `Admin12345!`
- **Device IDs**: `dev-1`, `dev-upload`, `dev-download`, `dev-cc-1`, etc.

---

## API Summary & SharpSync Mapping

All endpoints require a Bearer token (`Authorization: Bearer <token>`) except `/api/status`, `/api/auth/login`, and `/api/auth/refresh`.

| Method | Endpoint | Description | SharpSync Mapping |
|---|---|---|---|
| `GET` | `/api/status` | Anonymous health check, version, server UTC time, compile UTC datetime, DB reachability | `TestConnectionAsync` |
| `POST` | `/api/auth/login` | Login with username, password, and device info. Returns JWT + refresh token. Rate-limited (10 req/min per IP) | Client Authentication |
| `POST` | `/api/auth/refresh` | Refresh JWT with rotation & reuse revocation | Auth handler |
| `POST` | `/api/auth/logout` | Revoke active refresh tokens for the calling device | Client Logout |
| `GET` | `/api/auth/me` | Fetch profile of currently authenticated user | Connection verification |
| `GET` | `/api/files` | List file metadata. Supports `sinceSeq`, `since` (UTC), `includeDeleted`, `cursor`, and `limit` | Snapshot listing (`ListItemsAsync`, `GetItemAsync`) |
| `GET` | `/api/files/meta?path=` | Get single file metadata by relative path | `GetItemAsync` fallback |
| `GET` | `/api/files/{id}` | Get single file metadata by unique file ID | UI / Details |
| `GET` | `/api/files/{id}/content` | Stream file contents. Supports `Range` headers (HTTP 206) and returns `ETag: "<sha256>"` | `ReadFileAsync` |
| `PUT` | `/api/files?path=` | Stream file upload (atomic temp-then-move). Supports `If-Match`, `If-None-Match`, `X-Content-SHA256`, `X-Base-Version`. Returns 409 on conflict | `WriteFileAsync` |
| `POST` | `/api/directories` | Create directory metadata entry | `CreateDirectoryAsync` |
| `DELETE`| `/api/files/{id}` | Soft delete (tombstone) with `If-Match` precondition | `DeleteAsync` |
| `GET` | `/api/trash` | List top-level deleted (tombstoned) files and folders | Deleted Files tab |
| `POST` | `/api/trash/{id}/restore` | Restore a deleted file/folder (optionally at `targetPath` as a copy) | Deleted Files tab |
| `DELETE` | `/api/trash/{id}` | Permanently delete a tombstoned item and its stored version blobs | Deleted Files tab |
| `DELETE` | `/api/trash` | Empty trash: permanently delete all tombstoned items and blobs for the user | Deleted Files tab |
| `POST` | `/api/files/move` | Rename or move file path | `MoveAsync` |
| `PATCH`| `/api/files/{id}` | Update last modified UTC timestamp | `SetLastModifiedAsync` |
| `GET` | `/api/storage` | Returns total disk space, used space, and quota | `GetStorageInfoAsync` |
| `GET` | `/api/logs` | Query paged server activity logs (scoped to user, filterable by action/result/date) | Log viewer |
| `POST` | `/api/logs/client` | Report client-side sync events and errors to the server log | Diagnostics |
| `GET/PUT`| `/api/settings` | Admin-only API key/value settings | Settings management |
| `GET/POST`| `/api/users` | Admin-only user list and new regular user creation | Web User Admin |
| `PATCH` | `/api/users/{id}/status` | Admin-only enable, disable, or mark/restore deleted user | Web User Admin |
| `PUT` | `/api/users/{id}/password` | Admin-only change a regular user's password | Web User Admin |
| `GET/DELETE`| `/api/devices`, `/api/devices/{id}` | List or revoke user devices | Device management |

---

## Server Data & Blob Storage Design

1. **Per-User Blob Layout**: File contents are stored under each user's username folder, indexed by GUID to prevent path traversal and cross-platform filename conflicts:
   ```
   <StorageRoot>/<username>/<fileId[0..2]>/<fileId>/<version>.blob
   ```
2. **Atomic Writes**: Incoming uploads are streamed directly to temporary files (`<StorageRoot>/<username>/.tmp/<guid>.tmp`) while computing SHA-256 incrementally. Once verified, the file is atomically moved (`File.Move`) to its final blob destination.
3. **Monotonic Change Sequence**: Changes increment a per-user sequence number (`ChangeSeq`), allowing clients to poll updates reliably without clock-skew issues.
4. **Optimistic Concurrency**:
   - `If-Match: "<baseSha256>"`: Verifies the client's base hash before updating. If changed on the server, responds with `409 Conflict` containing both `client` and `server` version metadata.
   - `If-None-Match: *`: Ensures file does not already exist when uploading a newly created local file.

