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
├── build.version                       # Auto-incremented build counter for FolderSyncEr.Server
├── build.client.version                # Auto-incremented build counter for FolderSyncEr.Client.App
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

### 5. Automatic Build Version Incrementing (`v1.0.x`)
Every time the server or desktop client project is built (`dotnet build`, `dotnet run`, or `dotnet publish`), an MSBuild pre-compile target automatically increments the patch/build number (`1.0.0` -> `1.0.1` -> `1.0.2`, etc.):

- **How It Works**:
  - **Server (`src/FolderSyncEr.Server/FolderSyncEr.Server.csproj`)**: The `AutoIncrementBuildVersion` MSBuild target runs before `BeforeCompile;CoreCompile` (skipped during IDE design-time builds). It reads the integer counter in `build.version` at the repository root, increments it by `1`, writes the new value back to `build.version`, and generates `obj/.../AutoGeneratedBuildVersion.g.cs` with updated `[AssemblyVersion]`, `[AssemblyFileVersion]`, and `[AssemblyInformationalVersion]` attributes (`1.0.<BuildNumber>`).
  - **Desktop Client (`src/FolderSyncEr.Client.App/FolderSyncEr.Client.App.csproj`)**: The `AutoIncrementClientBuildVersion` target performs the same step using `build.client.version`.
  - **Changing Major/Minor Version or Resetting**: Update `<VersionMajor>` / `<VersionMinor>` in the `.csproj` file, or edit the integer in `build.version` / `build.client.version`.
- **Where the Version & Build Timestamp Are Displayed**:
  - **Build Console Output**: Prints `FolderSyncEr.Server build version incremented to v1.0.x` during compilation.
  - **Server Startup Log**: Logs `FolderSyncEr.Server starting — Version: v1.0.x, Compiled: yyyy-MM-dd HH:mm:ss UTC` to the console and `logs/server-*.log`.
  - **Status API (`GET /api/status`)**: Returns both `"version": "1.0.x"` and `"compiledUtc"`.
  - **Web File Viewer Header**: Displays `Online • v1.0.x • Built <timestamp>` in the top status badge.
  - **Desktop App**:
    - **Account & Connection Tab**: Displays `SERVER VERSION` (`v1.0.x`) and `SERVER COMPILED (UTC)` in the Server Connection Check card.
    - **Settings Tab**: Displays `Client Version` (`1.0.x`) and `Compiled DateTime` in the Application Information card at the bottom.

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
- **Activities View**:
  - A user-friendly 2-column activity feed: **Activity** and **When** (e.g. `You deleted readme.txt • 5min`, `Synced MyFile.doc • 8min`, `You created MyFile.doc • 1h`, `Connection lost • 2h`, `Connection re-established • 10min`, `Logged in as Admin • 1h 30m`).
  - Search box to filter events live by path, action, or keyword.
  - Persisted locally to disk (`activities.json` in the client configuration folder) with rolling retention (up to 1,000 entries) across application restarts.
  - Captures local file watcher events (`You created...`, `You modified...`, `You deleted...`, `You renamed...`), sync transfers (`Synced <file/folder>`), conflict warnings, connection lifecycle (`Connection lost` / `re-established`), OS network status (`Network offline` / `restored`), and auth session events (`Logged in as <User>`, `Logged out`).

####  Event Coverage:
Local File Actions:
 - You created <file>
 - You modified <file>
 - You deleted <file>
 - You renamed <old> to <new>
Sync Operations:
 - Synced <file> (when files are uploaded or downloaded by the sync engine)
 - Synced <folder> (when directories are created)
 - Conflict detected on <file>
Trash Actions:
 - Restored <file>
 - You deleted <file> forever
 - Trash emptied
Connection & Session Lifecycle:
 - Connection lost / Connection re-established
 - Network offline / Network restored
 - Logged in as <User> / Logged out
 - Sync paused / Sync resumed

- **Server Activity Logs Viewer**:
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
  - **Allow Untrusted / Self-Signed SSL Certificates**: Configurable toggle in **Settings -> Server Connection URL** (`AllowUntrustedCertificates`, default `false`). Allows connecting to internal HTTPS servers (e.g. `https://foldersync.house.lan/` or self-signed homelab certificates) without triggering `The SSL connection could not be established`.
  - **Update Cooldown (Trailing-Edge Debouncing)**: Configurable quiet period in **Settings -> System Tray & Background Sync** (`UpdateCooldownSeconds`, default `30` seconds, range 1–300s). When a file is written or changed, a countdown begins; each new write event resets the countdown so rapid successive edits (e.g. text editors, IDEs, auto-saving office tools) are bundled into a single sync run and uploaded once quiet, preventing excessive server version increments and network churn.
  - **Max Wait Ceiling**: Configurable hard ceiling in **Settings -> System Tray & Background Sync** (`MaxWaitSeconds`, default `300` seconds / 5 minutes, range 10–3600s). Guards against infinite cooldown resets: if an application continuously writes without pause, sync is guaranteed to trigger once the max wait ceiling is reached. Tooltip explanations are available on hover in the UI.
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

### 4. Server Reconnection & Network Resilience (Exponential Backoff with Jitter)
FolderSyncEr features robust network disconnection handling and intelligent server reconnection coordination (`BackoffRetryPolicy` & `NetworkConnectivityService`):

#### Truncated Exponential Backoff with Jitter
When the server cannot be reached during periodic syncs, file change pushes, or background connection checks, the client uses **Truncated Exponential Backoff with Jitter** to progressively scale back retry attempts, preventing server thundering-herd spikes while remaining responsive.

- **Formula**:
  $$\text{Delay} = \min(\text{MaxInterval}, \text{BaseInterval} \times \text{Multiplier}^{(\text{attempt} - 1)}) \times (1 \pm \text{JitterRatio})$$
- **Parameters**:
  - `BaseInterval`: **1 minute** (60 seconds)
  - `Multiplier`: **2.0**
  - `MaxInterval (Cap)`: **60 minutes** (3,600 seconds)
  - `JitterRatio`: **±10%** (multiplied by a uniform random factor between $0.90$ and $1.10$)

#### Retry Schedule Timeline
| Attempt | Calculation | Scheduled Range (±10% Jitter) | Approx Delay | Cumulative Elapsed Time |
|---|---|---|---|---|
| **#1** | $1\text{m} \times 2^0 = 1\text{m}$ | $54\text{s} - 66\text{s}$ | $\sim 1\text{ min}$ | $\sim 1\text{ min}$ |
| **#2** | $1\text{m} \times 2^1 = 2\text{m}$ | $108\text{s} - 132\text{s}$ | $\sim 2\text{ min}$ | $\sim 3\text{ min}$ |
| **#3** | $1\text{m} \times 2^2 = 4\text{m}$ | $3.6\text{m} - 4.4\text{m}$ | $\sim 4\text{ min}$ | $\sim 7\text{ min}$ |
| **#4** | $1\text{m} \times 2^3 = 8\text{m}$ | $7.2\text{m} - 8.8\text{m}$ | $\sim 8\text{ min}$ | $\sim 15\text{ min}$ |
| **#5** | $1\text{m} \times 2^4 = 16\text{m}$ | $14.4\text{m} - 17.6\text{m}$ | $\sim 16\text{ min}$ | $\sim 31\text{ min}$ |
| **#6** | $1\text{m} \times 2^5 = 32\text{m}$ | $28.8\text{m} - 35.2\text{m}$ | $\sim 32\text{ min}$ | $\sim 1\text{ hr } 3\text{ min}$ |
| **#7** | $\min(60\text{m}, 64\text{m}) = 60\text{m}$ | $54\text{m} - 66\text{m}$ | $\sim 60\text{ min (Cap)}$ | $\sim 2\text{ hr } 3\text{ min}$ |
| **#8+** | $\min(60\text{m}, 128\text{m}) = 60\text{m}$ | $54\text{m} - 66\text{m}$ | $\sim 60\text{ min (Cap)}$ | $+60\text{ min}$ per attempt |

#### Multi-Port OS Network Detection (`INetworkConnectivityService`)
- **Native OS Event Listeners**: Hooks `NetworkChange.NetworkAvailabilityChanged` and `NetworkChange.NetworkAddressChanged` across Windows and Linux.
- **Multi-Port Awareness**: Supports systems with multiple network interfaces (e.g. Wi-Fi + Ethernet, dual 10GbE NICs, or VPNs). Inspects all non-loopback network adapters with `OperationalStatus.Up` and valid assigned unicast IP addresses.
- **Offline Pause**: If all network interfaces are disconnected or cables are unplugged, retries are paused immediately to prevent useless TCP socket exhaustion and CPU churn.

#### State Reset Conventions
The backoff policy resets to attempt 0 and restores normal scheduling under the following conditions:
1. **Successful Sync / Connection**: As soon as the server is reached and preflight or sync succeeds, failure counters reset.
2. **Network Restored**: When OS network connectivity returns (e.g., Ethernet plugged in or Wi-Fi reconnects), backoff is reset immediately and an immediate reconnection probe is executed.
3. **Manual Action**: Clicking **Sync Now** immediately resets the backoff policy and attempts a connection.

#### Front-End Countdown & Tooltips
- Under the title in the client's top bar:
  - **Server Unreachable**: Displays an amber indicator with a live countdown `• Next retry in 00:45` updating every second.
  - **Countdown Tooltip**: Displays the attempt count, approximate interval, and cause: `Attempt #2 • Backoff interval: ~2m (Server unreachable)`.
  - **Network Offline**: If the machine's physical network adapters are disconnected, displays a red indicator `• Network Offline` with tooltip `No active network adapters detected (Retries paused until network reconnects)`.
  - **Last Sync Indicator**: Displays exact UTC time when connected, or relative time (`5min ago`, `1hr 25min ago`) when disconnected, updated every 10 seconds.

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

### Test Suites (157 Tests Total)
- **`FolderSyncEr.Server.IntegrationTests` (23 tests)**: Full ASP.NET Core Web API server tests against SQLite EF Core, verifying JWT auth, immediate device token revocation in validation pipeline, token rotation, device management & revocation, streaming uploads/downloads, atomic writes, optimistic concurrency, soft delete tombstones, and admin ZIP export.
- **`FolderSyncEr.Client.Tests` (47 tests)**: Client platform and UI viewmodel tests verifying Activities feed persistence, deduplication, search filtering and relative time formatting (`ActivitiesViewModel`), Truncated Exponential Backoff with Jitter scheduling & formatting, multi-NIC network detection (`INetworkConnectivityService`), countdown UI indicators & state resets, OS secret stores (`ISecretStore`), daily rolling file logger (`ClientFileLoggerProvider`) with 30-day pruning, token management with 401 automatic single-flight token refresh and offline resilience, `FileListViewModel` (local/remote merge, badge logic, and descending local date sorting), `LogsViewModel` (filters & pagination), `ConflictPromptViewModel` (interactive resolution), `SettingsViewModel` (folder migration warning, URL persistence, `StartMinimized`, `settings.json` & log file links, and ZIP export), `ClientSettingsManager` (atomic JSON loading & saving), `LoginViewModel` (active devices list & remote device sign-out), and `ConnectionCheckViewModel` (explicit URL save & persistence).
- **`FolderSyncEr.Client.Sync.Tests` (87 tests)**: SharpSync integration and hardening tests verifying `RestRemoteStorage`, `SafeLocalStorage` (atomic writes + trash recovery + hash cache + sharing violation resilience), `GuardedSyncDatabase` (R1 failure suppression + version tagging), `PolicyConflictResolver` (keep-both, newest-wins, prompt), `SyncCoordinator` (cooldown debounce, max wait ceiling, single-flight sync), and exhaustive cross-platform path validation rules (`PathRules`).

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

---

## Phase 5: Hardening, Packaging, and Deployment

### 1. System Hardening & Resilience

#### A. Cross-Platform Path Rules & Security (`PathRules.cs`)
Files synced between Linux, Windows, and macOS encounter distinct filesystem constraints. FolderSyncEr enforces strict path validation both on the server and on the client before persisting any item:
- **Path Separators**: All backslashes (`\`) are normalized to forward slashes (`/`), and leading/trailing slashes are trimmed.
- **Unicode Normalization Form C**: Canonical composition (`FormC`) is enforced so visually identical filenames have identical byte encodings across OS platforms.
- **Directory Traversal Protection**: Any path segment equal to `.` or `..` is rejected.
- **Control Character Rejection**: ASCII control characters (`0x00`-`0x1F`, including `\0`, `\r`, `\n`, `\t`) are blocked.
- **Illegal Character Filtering**: Windows-disallowed characters (`<`, `>`, `:`, `"`, `|`, `?`, `*`) are blocked on all platforms.
- **Trailing Dots and Spaces**: Segments ending with a period or space (e.g. `file.` or `dir `) are rejected because they cause silent truncation or corruption on Windows.
- **Windows Reserved Device Names**: `CON`, `PRN`, `AUX`, `NUL`, `COM1`-`COM9`, `LPT1`-`LPT9`, `CONIN$`, and `CONOUT$` are rejected regardless of file extension (e.g., `aux.txt` or `nul.json`).
- **Path Length Guard**: Relative paths exceeding 1,024 characters are rejected upfront.

#### B. Locked File & Sharing Violation Resilience
When a local application (e.g. text editor, office suite, or torrent client) opens a file for writing or locks it exclusively:
- `SafeLocalStorage` opens files with `FileShare.ReadWrite` to allow computing hashes or reading content without conflicting with non-exclusive writers.
- If an exclusive lock or sharing violation occurs (`IOException` or `UnauthorizedAccessException`), the operation fails gracefully and registers the path in `RunFailureRegistry`.
- `GuardedSyncDatabase` intercepts database updates and suppresses committing sync state for any item recorded in `RunFailureRegistry`, ensuring SharpSync does not incorrectly stamp locked or partially-read files as "Synced".

#### C. Network Failure & Session Resilience
- **Automatic Token Refresh**: The HTTP pipeline (`AuthHeaderHandler`) intercepts `401 Unauthorized` responses and performs a synchronized, single-flight token exchange (`POST /api/auth/refresh`) using an asynchronous `SemaphoreSlim` mutex. Concurrent in-flight requests wait for the new access token before retrying.
- **Self-Signed / Private CA SSL Support**: For users running the server on home or enterprise LANs (`https://foldersync.house.lan`), the client includes an **Allow Untrusted SSL/TLS Certificates** toggle in **Settings -> Server Connection URL**.
- **Update Cooldown Debounce & Max Wait**: Prevents rapid successive sync runs on frequently updated files with trailing-edge debouncing (`UpdateCooldownSeconds`, 1-300s) and a hard ceiling guard (`MaxWaitSeconds`, 1-3600s).

#### D. Production Security Checklist
- **Enforce TLS**: Set `ASPNETCORE_URLS="https://+:5001;http://+:5000"` or run the server behind a reverse proxy (Caddy / Nginx) terminating TLS.
- **Secrets Protection**: Access and refresh tokens are stored in the platform's native keychain (`wincred.dll` on Windows, `libsecret` on Linux). Daily client file logs automatically omit sensitive auth tokens and passwords.
- **Least-Privilege Storage Permissions**: Ensure the server data directory (`storage/`) is owned by the dedicated `foldersyncer` service account with restrictive permissions (`chmod 700 storage`).
- **Non-Blocking SQLite Backups**: Perform live backups of the server SQLite database using the standard online backup API:
  ```bash
  sqlite3 foldersyncer.db "VACUUM INTO 'backup-$(date +%Y%m%d%H%M%S).db';"
  ```

---

### 2. Automated Binary Release Packaging (GitHub Releases)

To generate clean binary release archives for both Linux and Windows in a single step, run the release script from the repository root:

```bash
./create-release.sh
```

This compiles self-contained, single-file executables for all platforms and produces exactly 5 files in the `Releases/` directory:

| Filename | Description | Format |
|---|---|---|
| `FolderSyncEr-Client-linux-<version>.tar.gz` | Linux Desktop Client with launcher, icon, and installer | `.tar.gz` (preserves executable bits) |
| `FolderSyncEr-Server-linux-<version>.tar.gz` | Linux Web API Server with systemd unit and web file viewer | `.tar.gz` |
| `FolderSyncEr-Client-windows-<version>.zip` | Windows Desktop Client portable single-file executable | `.zip` |
| `FolderSyncEr-Server-windows-<version>.zip` | Windows Web API Server portable single-file executable | `.zip` |
| `RELEASE_NOTES.txt` | Dynamically generated release notes with version numbers and install steps | Text |

All intermediate build files and temporary staging directories are automatically cleaned up upon completion. The contents of `Releases/` can be directly attached to a GitHub release or committed to Git.

The release notes instructions are populated from the template in `packaging/templates/release-notes-template.txt`.

---

### 3. Packaging & Deployment for Ubuntu / Linux (`linux-x64`)

#### System Requirements
- Ubuntu 20.04 LTS, 22.04 LTS, or 24.04 LTS (x86_64).
- Required desktop libraries:
  ```bash
  sudo apt update
  sudo apt install -y libsecret-1-0 libx11-6 libice6 libsm6 libfontconfig1
  ```
  *(Optional for GNOME system tray icon support: `gnome-shell-extension-appindicator`)*

#### Automated Linux Build
Run the Linux packaging script from the repository root:
```bash
./scripts/build-linux.sh
```
Or for a fully self-contained single-file bundle (including the .NET runtime):
```bash
./scripts/build-linux.sh --self-contained
```

This generates:
- Distribution archive: `dist/FolderSyncEr-linux-x64.tar.gz`
- Staging directory: `dist/linux-x64/package/` containing:
  - `foldersyncer-client` (Desktop UI launcher)
  - `foldersyncer-server` (Web API backend)
  - `foldersyncer.desktop` (XDG Desktop Entry)
  - `icon.png` (High-resolution application icon)
  - `install.sh` (One-click installer)
  - `foldersyncer-server.service` (Systemd service unit)

#### One-Click Installation
Extract and install the package on Ubuntu:
```bash
tar -xzf dist/FolderSyncEr-linux-x64.tar.gz -C /tmp/foldersyncer-pkg
cd /tmp/foldersyncer-pkg
./install.sh
```
- For standard users: Installs binaries to `~/.local/bin`, the desktop launcher to `~/.local/share/applications`, and the icon to `~/.local/share/icons/hicolor/256x256/apps`.
- For system-wide install (`sudo ./install.sh`): Installs to `/usr/local/bin` and `/usr/local/share/...`.

#### Running Server as a Headless Systemd Service
To run the server continuously in the background on Ubuntu Server:
```bash
# 1. Create a dedicated service user and directory
sudo useradd -r -s /bin/false foldersyncer || true
sudo mkdir -p /opt/foldersyncer-server
sudo cp -r dist/linux-x64/package/* /opt/foldersyncer-server/
sudo chown -R foldersyncer:foldersyncer /opt/foldersyncer-server

# 2. Install and enable the systemd service
sudo cp packaging/systemd/foldersyncer-server.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now foldersyncer-server

# 3. Check server status and logs
sudo systemctl status foldersyncer-server
sudo journalctl -u foldersyncer-server -f
```

#### Reverse Proxy Configuration (HTTPS)
- **Caddy**: Copy `packaging/caddy/Caddyfile.example` to `/etc/caddy/Caddyfile` for automatic Let's Encrypt TLS.
- **Nginx**: Copy `packaging/nginx/nginx.conf.example` to `/etc/nginx/sites-available/foldersyncer.conf` and symlink to `sites-enabled`.

---

### 3. Packaging & Deployment for Windows (`win-x64`)

#### System Requirements
- Windows 10 (version 1809 or higher), Windows 11, or Windows Server 2016+.
- Native credential storage via Windows Credential Manager (`wincred.dll`).

#### Automated Windows Build
Run the Windows build script from PowerShell or Command Prompt:

**Using PowerShell:**
```powershell
.\scripts\build-windows.ps1 -SelfContained
```

**Using Batch:**
```cmd
scripts\build-windows.bat
```

This compiles self-contained single-file executables into `dist\win-x64\package\`:
- `FolderSyncEr.Client.exe`: Standalone desktop client with Avalonia UI and system tray integration.
- `FolderSyncEr.Server.exe`: Standalone Web API server.
- `appsettings.json`: Server configuration file.

#### Running on Windows
1. **Desktop Client**: Double-click `FolderSyncEr.Client.exe`. The application can minimize to the system notification tray and optionally start minimized on Windows login.
2. **Server as a Windows Service**: Run using NSSM (Non-Sucking Service Manager) or Windows Task Scheduler:
   ```cmd
   nssm.exe install FolderSyncErServer "C:\FolderSyncEr\FolderSyncEr.Server.exe"
   nssm.exe set FolderSyncErServer AppDirectory "C:\FolderSyncEr"
   nssm.exe start FolderSyncErServer
   ```


