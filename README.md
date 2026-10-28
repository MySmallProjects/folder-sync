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
│   └── FolderSyncEr.Client.App/        # Avalonia UI MVVM desktop application
└── tests/
    ├── FolderSyncEr.Server.IntegrationTests/ # End-to-end Web API integration tests
    └── FolderSyncEr.Client.Tests/            # Client unit & integration tests (auth, secret store, API client)
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
- **Interactive Swagger UI**: [http://localhost:5000/swagger](http://localhost:5000/swagger)

### 3. Default Credentials (Seeded on First Startup)
When the server starts up for the first time, it automatically creates the SQLite database (`foldersyncer.db`) and seeds an initial administrator account:

| Property | Default Value | Notes |
|---|---|---|
| **Username** | `admin` | Configurable in `appsettings.json` under `Bootstrap:AdminUsername` |
| **Password** | `AdminPassword123!` | Configurable in `appsettings.json` under `Bootstrap:AdminPassword` |
| **Role** | `Admin` | Has access to `/api/settings` and cross-user logs |

### 4. Configuration (`appsettings.json`)
Key configuration sections in `src/FolderSyncEr.Server/appsettings.json`:
- `ConnectionStrings:DefaultConnection`: SQLite connection string (`Data Source=foldersyncer.db`).
- `Storage:RootPath`: Directory where file blobs are persisted (defaults to `storage/`).
- `Storage:MaxUploadBytes`: Maximum file upload size in bytes (defaults to `4294967296` = 4 GB).
- `Storage:VersionRetentionCount`: Number of previous file versions retained for recovery (defaults to `5`).
- `Jwt:Secret`: Secret key used for signing JWT access tokens (must be at least 256 bits).
- `Jwt:ExpiryMinutes`: Lifetime of access tokens (defaults to `15` minutes).
- `Jwt:RefreshExpiryDays`: Lifetime of rotating refresh tokens (defaults to `30` days).

---

## Running the Desktop Client (Avalonia)

### 1. Run the Client Application
From the solution root directory:

```bash
dotnet run --project src/FolderSyncEr.Client.App
```

### 2. Client Features (Phase 2 Skeleton)
- **Status & Latency Checker**: Real-time roundtrip ping and server UTC time verification against `/api/status`.
- **Authentication**: JWT login with automatic token refresh on expiry, session persistence across restarts, and secure logout.
- **OS-Native Secret Storage (`ISecretStore`)**:
  - **Windows**: Windows Credential Manager (`advapi32.dll` CredWriteW/CredReadW/CredDeleteW) with fallback to DPAPI `ProtectedData`.
  - **Linux / Ubuntu**: FreeDesktop Secret Service via `libsecret-1.so.0` (SecretValue / secret_password_store_sync).
  - **Fallback**: Platform-independent AES-256-GCM encrypted file (`~/.config/foldersyncer/secrets.enc.bin` or `/tmp/foldersyncer/`) restricted to `0600` POSIX file permissions.
- **Sync Folder Picker**: Select and validate local folder paths, enforcing write permissions and blocking system root directories.

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
| `GET` | `/api/status` | Anonymous health check, version, server UTC time, DB reachability | `TestConnectionAsync` |
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
| `POST` | `/api/files/move` | Rename or move file path | `MoveAsync` |
| `PATCH`| `/api/files/{id}` | Update last modified UTC timestamp | `SetLastModifiedAsync` |
| `GET` | `/api/storage` | Returns total disk space, used space, and quota | `GetStorageInfoAsync` |
| `GET` | `/api/logs` | Query paged server activity logs (scoped to user, filterable by action/result/date) | Log viewer |
| `POST` | `/api/logs/client` | Report client-side sync events and errors to the server log | Diagnostics |
| `GET/PUT`| `/api/settings` | Admin-only API key/value settings | Settings management |
| `GET/DELETE`| `/api/devices`, `/api/devices/{id}` | List or revoke user devices | Device management |

---

## Server Data & Blob Storage Design

1. **Blob Layout**: File contents are stored on disk indexed by GUID to prevent path traversal and cross-platform filename conflicts:
   ```
   <StorageRoot>/<userId>/<fileId[0..2]>/<fileId>/<version>.blob
   ```
2. **Atomic Writes**: Incoming uploads are streamed directly to temporary files (`<StorageRoot>/<userId>/.tmp/<guid>.tmp`) while computing SHA-256 incrementally. Once verified, the file is atomically moved (`File.Move`) to its final blob destination.
3. **Monotonic Change Sequence**: Changes increment a per-user sequence number (`ChangeSeq`), allowing clients to poll updates reliably without clock-skew issues.
4. **Optimistic Concurrency**:
   - `If-Match: "<baseSha256>"`: Verifies the client's base hash before updating. If changed on the server, responds with `409 Conflict` containing both `client` and `server` version metadata.
   - `If-None-Match: *`: Ensures file does not already exist when uploading a newly created local file.

