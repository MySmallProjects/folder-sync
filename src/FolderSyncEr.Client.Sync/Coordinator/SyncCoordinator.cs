using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSyncEr.Client.Api;
using FolderSyncEr.Client.Sync.Common;
using FolderSyncEr.Client.Sync.Conflicts;
using FolderSyncEr.Client.Sync.Database;
using FolderSyncEr.Client.Sync.Registry;
using FolderSyncEr.Client.Sync.Storage;
using FolderSyncEr.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Oire.SharpSync.Core;
using Oire.SharpSync.Database;
using Oire.SharpSync.Storage;
using Oire.SharpSync.Sync;

namespace FolderSyncEr.Client.Sync.Coordinator;

public record ClientSyncResult(
    bool Success,
    int ItemsProcessed,
    int ConflictsResolved,
    int ErrorsCount,
    TimeSpan Duration,
    string? ErrorMessage
);

public sealed class CoordinatorProgressEventArgs : EventArgs
{
    public int ProcessedItems { get; init; }
    public int TotalItems { get; init; }
    public string CurrentItem { get; init; } = string.Empty;
    public double Percentage { get; init; }
}

public sealed class CoordinatorSyncCompletedEventArgs : EventArgs
{
    public ClientSyncResult Result { get; init; } = null!;
}

/// <summary>
/// Top-level coordinator managing the synchronization lifecycle between the local folder,
/// the SharpSync engine, and the remote REST storage service.
/// </summary>
public sealed class SyncCoordinator : IDisposable
{
    private readonly IFileSyncApi _api;
    private readonly string _localFolderPath;
    private readonly string _deviceId;
    private readonly IConflictPromptHandler? _promptHandler;
    private readonly ILogger<SyncCoordinator> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly RunFailureRegistry _failureRegistry = new();

    private SafeLocalStorage? _localStorage;
    private RestRemoteStorage? _remoteStorage;
    private GuardedSyncDatabase? _guardedDb;
    private PolicyConflictResolver? _conflictResolver;
    private SyncEngine? _engine;
    private RemoteSnapshot? _currentSnapshot;

    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private Timer? _periodicTimer;
    private bool _pendingSyncRequested;
    private bool _dbInitialized;
    private bool _isDisposed;

    public string LocalFolderPath => _localFolderPath;
    public bool IsSynchronizing { get; private set; }
    public bool IsPaused { get; private set; }
    public DateTime? LastSyncTimeUtc { get; private set; }
    public string? LastError { get; private set; }

    public ConflictPolicy Policy
    {
        get => _conflictResolver?.Policy ?? ConflictPolicy.KeepBoth;
        set
        {
            if (_conflictResolver != null)
                _conflictResolver.Policy = value;
        }
    }

    public event EventHandler<CoordinatorProgressEventArgs>? ProgressChanged;
    public event EventHandler<StorageProgressEventArgs>? FileProgressChanged;
    public event EventHandler<FileConflictEventArgs>? ConflictDetected;
    public event EventHandler<CoordinatorSyncCompletedEventArgs>? SyncCompleted;

    public SyncCoordinator(
        IFileSyncApi api,
        string localFolderPath,
        string deviceId,
        ConflictPolicy policy = ConflictPolicy.KeepBoth,
        IConflictPromptHandler? promptHandler = null,
        ILoggerFactory? loggerFactory = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _localFolderPath = Path.GetFullPath(localFolderPath ?? throw new ArgumentNullException(nameof(localFolderPath)));
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? Environment.MachineName : deviceId;
        _promptHandler = promptHandler;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<SyncCoordinator>();

        InitializeEngine(policy);
    }

    private void InitializeEngine(ConflictPolicy policy)
    {
        var metaDir = Path.Combine(_localFolderPath, ".foldersyncer");
        if (!Directory.Exists(metaDir))
        {
            Directory.CreateDirectory(metaDir);
        }

        var dbPath = Path.Combine(metaDir, "sharpsync.db");
        var rawDb = new SqliteSyncDatabase(dbPath);

        _currentSnapshot = new RemoteSnapshot();
        _guardedDb = new GuardedSyncDatabase(rawDb, _failureRegistry, _currentSnapshot, _loggerFactory.CreateLogger<GuardedSyncDatabase>());
        _localStorage = new SafeLocalStorage(_localFolderPath, _loggerFactory.CreateLogger<SafeLocalStorage>());
        _remoteStorage = new RestRemoteStorage(
            _api,
            _currentSnapshot,
            _failureRegistry,
            path =>
            {
                var fullPath = Path.Combine(_localFolderPath, path.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath) : null;
            },
            _loggerFactory.CreateLogger<RestRemoteStorage>());
        _conflictResolver = new PolicyConflictResolver(_localFolderPath, _deviceId, policy, _promptHandler, _loggerFactory.CreateLogger<PolicyConflictResolver>());

        var filter = new SyncFilter();
        filter.AddExclusionPattern(".foldersyncer/**");
        filter.AddExclusionPattern(".foldersyncer");
        filter.AddExclusionPattern("* (conflict *");
        filter.AddExclusionPattern("~*");
        filter.AddExclusionPattern("*.tmp");
        filter.AddExclusionPattern("*.swp");
        filter.AddExclusionPattern("*.crswap");
        filter.AddExclusionPattern(".#*");

        var engineLogger = _loggerFactory.CreateLogger<SyncEngine>();

        _engine = new SyncEngine(
            localStorage: _localStorage,
            remoteStorage: _remoteStorage,
            database: _guardedDb,
            conflictResolver: _conflictResolver,
            filter: filter,
            logger: engineLogger,
            maxParallelism: 4,
            useChecksums: true,
            changeDetectionWindow: TimeSpan.FromSeconds(2));

        _engine.ProgressChanged += (s, e) =>
        {
            ProgressChanged?.Invoke(this, new CoordinatorProgressEventArgs
            {
                ProcessedItems = e.Progress.ProcessedItems,
                TotalItems = e.Progress.TotalItems,
                CurrentItem = e.CurrentItem ?? string.Empty,
                Percentage = e.Progress.Percentage
            });
        };

        _engine.ConflictDetected += (s, e) =>
        {
            ConflictDetected?.Invoke(this, e);
        };

        _localStorage.ProgressChanged += (s, e) => FileProgressChanged?.Invoke(this, e);
        _remoteStorage.ProgressChanged += (s, e) => FileProgressChanged?.Invoke(this, e);
    }

    public async Task<ClientSyncResult> RunSyncAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(SyncCoordinator));

        // Serialize execution: single-flight with pending queue (Mitigating Risk R13)
        if (!await _syncLock.WaitAsync(0, cancellationToken))
        {
            _logger.LogDebug("Sync already in progress; queuing follow-up sync.");
            _pendingSyncRequested = true;
            return new ClientSyncResult(false, 0, 0, 0, TimeSpan.Zero, "Sync already in progress.");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        IsSynchronizing = true;
        _failureRegistry.Clear();

        try
        {
            ClientSyncResult? lastResult = null;
            do
            {
                _pendingSyncRequested = false;

                // 0. Ensure database initialized
                if (!_dbInitialized)
                {
                    await _guardedDb!.InitializeAsync(cancellationToken);
                    _dbInitialized = true;
                }

                // 1. Preflight check: verify API connection and DB reachability
                var status = await _api.CheckConnectionAsync(cancellationToken);
                if (!status.IsConnected)
                {
                    var msg = $"Server unreachable: {status.ErrorMessage ?? status.Status}";
                    _logger.LogWarning("Preflight failed: {Error}", msg);
                    LastError = msg;
                    return new ClientSyncResult(false, 0, 0, 1, sw.Elapsed, msg);
                }

                // 2. Fetch fresh remote snapshot (Mitigating Risk R2)
                var remoteFiles = await _api.ListFilesAsync(includeDeleted: false, limit: 10000, ct: cancellationToken);
                _currentSnapshot!.Load(remoteFiles.Items, remoteFiles.SnapshotSeq, remoteFiles.ServerTimeUtc);

                // 3. Pre-sync seeding pass (Mitigating Risk R8: both-new untracked files)
                await ExecuteSeedingPassAsync(cancellationToken);

                // 4. Run SharpSync engine
                var options = new SyncOptions
                {
                    ChecksumOnly = false,
                    ConflictResolution = ConflictResolution.Ask, // Passes control to IConflictResolver
                    DeleteExtraneous = true,
                    UpdateExisting = true,
                    PreserveTimestamps = true,
                    PreservePermissions = false,
                    Verbose = true
                };

                // Temporarily pause watcher so sync engine downloads don't trigger self-loop sync events
                if (_watcher != null)
                {
                    _watcher.EnableRaisingEvents = false;
                }

                SyncResult syncResult;
                try
                {
                    syncResult = await _engine!.SynchronizeAsync(options, cancellationToken);
                }
                finally
                {
                    if (_watcher != null && !_isDisposed)
                    {
                        _watcher.EnableRaisingEvents = true;
                    }
                }

                LastSyncTimeUtc = DateTime.UtcNow;
                LastError = null;

                lastResult = new ClientSyncResult(
                    Success: syncResult.Success && _failureRegistry.GetAllFailures().Count == 0,
                    ItemsProcessed: (int)syncResult.TotalFilesProcessed,
                    ConflictsResolved: (int)syncResult.FilesConflicted,
                    ErrorsCount: _failureRegistry.GetAllFailures().Count + (syncResult.Error != null ? 1 : 0),
                    Duration: sw.Elapsed,
                    ErrorMessage: syncResult.Error?.Message ?? _failureRegistry.GetAllFailures().FirstOrDefault().Value?.Message
                );

                SyncCompleted?.Invoke(this, new CoordinatorSyncCompletedEventArgs { Result = lastResult });

            } while (_pendingSyncRequested && !cancellationToken.IsCancellationRequested);

            return lastResult ?? new ClientSyncResult(true, 0, 0, 0, sw.Elapsed, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync cycle failed with exception");
            LastError = ex.Message;
            var result = new ClientSyncResult(false, 0, 0, 1, sw.Elapsed, ex.Message);
            SyncCompleted?.Invoke(this, new CoordinatorSyncCompletedEventArgs { Result = result });
            return result;
        }
        finally
        {
            IsSynchronizing = false;
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Mitigating Risk R8: Pre-sync check for both-new untracked files.
    /// If a file exists locally and on remote with matching hashes, records it as Synced
    /// before the engine executes, preventing parallel upload + download races.
    /// </summary>
    private async Task ExecuteSeedingPassAsync(CancellationToken cancellationToken)
    {
        if (_guardedDb == null || _currentSnapshot == null || _localStorage == null)
            return;

        var remoteItems = _currentSnapshot.GetAllItems();
        foreach (var remote in remoteItems)
        {
            if (remote.IsDirectory || remote.IsDeleted)
                continue;

            var normPath = RemoteSnapshot.NormalizePath(remote.Path);
            var localFullPath = Path.Combine(_localFolderPath, normPath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(localFullPath))
                continue;

            var localHash = await _localStorage.ComputeHashAsync(normPath, cancellationToken);
            var remoteHash = HashConverter.HexToBase64(remote.Sha256) ?? string.Empty;

            if (string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
            {
                var existingState = await _guardedDb.GetSyncStateAsync(normPath, cancellationToken);
                if (existingState == null || existingState.Status != SyncStatus.Synced)
                {
                    _logger.LogInformation("Seeding pass: matched identical file '{Path}'", normPath);
                    var seeded = new SyncState
                    {
                        Path = normPath,
                        IsDirectory = false,
                        LocalHash = localHash,
                        RemoteHash = remoteHash,
                        LocalSize = new FileInfo(localFullPath).Length,
                        RemoteSize = remote.Size,
                        LocalModified = File.GetLastWriteTimeUtc(localFullPath),
                        RemoteModified = remote.LastModifiedUtc,
                        LastSyncTime = DateTime.UtcNow,
                        Status = SyncStatus.Synced,
                        ETag = remote.Version.ToString()
                    };

                    await _guardedDb.UpdateSyncStateAsync(seeded, cancellationToken);
                }
            }
        }
    }

    public void StartBackgroundSync(TimeSpan? periodicInterval = null)
    {
        if (_watcher != null) return;

        // 1. FileSystemWatcher with debouncing
        _watcher = new FileSystemWatcher(_localFolderPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
        };

        _watcher.Changed += OnFileSystemEvent;
        _watcher.Created += OnFileSystemEvent;
        _watcher.Deleted += OnFileSystemEvent;
        _watcher.Renamed += OnFileSystemEvent;
        _watcher.EnableRaisingEvents = true;

        // 2. Periodic background scan
        var interval = periodicInterval ?? TimeSpan.FromMinutes(5);
        _periodicTimer = new Timer(async _ =>
        {
            if (!IsPaused && !IsSynchronizing)
            {
                _logger.LogInformation("Periodic background sync timer fired.");
                await RunSyncAsync();
            }
        }, null, interval, interval);

        _logger.LogInformation("Background sync started for '{Folder}'", _localFolderPath);
    }

    public void StopBackgroundSync()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        _debounceTimer?.Dispose();
        _debounceTimer = null;

        _periodicTimer?.Dispose();
        _periodicTimer = null;

        _logger.LogInformation("Background sync stopped.");
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        // Ignore internal sync metadata folder
        if (e.FullPath.Contains(".foldersyncer"))
            return;

        // Ignore temporary editor files
        var name = e.Name ?? string.Empty;
        if (name.StartsWith("~") || name.StartsWith(".#") || name.EndsWith(".tmp") || name.EndsWith(".swp") || name.EndsWith(".crswap"))
            return;

        // Ignore conflict files created by resolver
        if (name.Contains("(conflict "))
            return;

        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(async _ =>
        {
            if (!IsPaused && !IsSynchronizing)
            {
                _logger.LogInformation("Debounced local change detected ({ChangeType}: '{Path}'); starting sync.", e.ChangeType, e.Name);
                await RunSyncAsync();
            }
            else
            {
                _pendingSyncRequested = true;
            }
        }, null, 1200, Timeout.Infinite);
    }

    public async Task PauseAsync()
    {
        if (_engine != null && !IsPaused)
        {
            await _engine.PauseAsync();
            IsPaused = true;
            _logger.LogInformation("Sync engine paused.");
        }
    }

    public async Task ResumeAsync()
    {
        if (_engine != null && IsPaused)
        {
            await _engine.ResumeAsync();
            IsPaused = false;
            _logger.LogInformation("Sync engine resumed.");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        StopBackgroundSync();
        _syncLock.Dispose();
        _guardedDb?.Dispose();
        _engine?.Dispose();
    }
}
