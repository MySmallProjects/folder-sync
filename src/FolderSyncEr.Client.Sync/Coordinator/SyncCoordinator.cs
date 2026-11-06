using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSyncEr.Client.Api;
using FolderSyncEr.Client.Platform;
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
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly RunFailureRegistry _failureRegistry = new();

    private SafeLocalStorage? _localStorage;
    private RestRemoteStorage? _remoteStorage;
    private GuardedSyncDatabase? _guardedDb;
    private PolicyConflictResolver? _conflictResolver;
    private SyncEngine? _engine;
    private RemoteSnapshot? _currentSnapshot;

    private readonly object _debounceLock = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private Timer? _periodicTimer;
    private TimeSpan _periodicInterval = TimeSpan.FromMinutes(5);
    private DateTime? _firstChangeTimeUtc;
    private TimeSpan _cooldownDuration = TimeSpan.FromSeconds(30);
    private TimeSpan _maxWaitDuration = TimeSpan.FromSeconds(300);
    private bool _pendingSyncRequested;
    private bool _dbInitialized;
    private bool _isDisposed;

    public string LocalFolderPath => _localFolderPath;
    public bool IsSynchronizing { get; private set; }
    public bool IsPaused { get; private set; }
    public DateTime? LastSyncTimeUtc { get; private set; }
    public string? LastError { get; private set; }
    public BackoffRetryPolicy BackoffPolicy { get; }

    public TimeSpan CooldownDuration
    {
        get => _cooldownDuration;
        set => _cooldownDuration = value > TimeSpan.Zero ? value : TimeSpan.FromSeconds(1);
    }

    public TimeSpan MaxWaitDuration
    {
        get => _maxWaitDuration;
        set => _maxWaitDuration = value >= _cooldownDuration ? value : _cooldownDuration;
    }

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
        ILoggerFactory? loggerFactory = null,
        BackoffRetryPolicy? backoffPolicy = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _localFolderPath = Path.GetFullPath(localFolderPath ?? throw new ArgumentNullException(nameof(localFolderPath)));
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? Environment.MachineName : deviceId;
        _promptHandler = promptHandler;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<SyncCoordinator>();
        BackoffPolicy = backoffPolicy ?? new BackoffRetryPolicy();

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
        _localStorage = new SafeLocalStorage(_localFolderPath, _loggerFactory.CreateLogger<SafeLocalStorage>(), _failureRegistry);
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

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var ct = linkedCts.Token;

        // Serialize execution: single-flight with pending queue (Mitigating Risk R13)
        if (!await _syncLock.WaitAsync(0, ct))
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
                if (_isDisposed || ct.IsCancellationRequested)
                    break;

                _pendingSyncRequested = false;

                // 0. Ensure database initialized
                if (!_dbInitialized)
                {
                    await _guardedDb!.InitializeAsync(ct);
                    _dbInitialized = true;
                }

                // 1. Preflight check: verify API connection and DB reachability
                var status = await _api.CheckConnectionAsync(ct);
                if (!status.IsConnected)
                {
                    var msg = $"Server unreachable: {status.ErrorMessage ?? status.Status}";
                    _logger.LogWarning("Preflight failed: {Error}", msg);
                    LastError = msg;
                    var delay = BackoffPolicy.RecordFailure();
                    _logger.LogInformation("Applying truncated exponential backoff: next attempt in {DelaySeconds}s (attempt #{Attempt})",
                        delay.TotalSeconds, BackoffPolicy.ConsecutiveFailures);
                    try
                    {
                        _periodicTimer?.Change(delay, delay);
                    }
                    catch (ObjectDisposedException) { }

                    var unreachableResult = new ClientSyncResult(false, 0, 0, 1, sw.Elapsed, msg);
                    SyncCompleted?.Invoke(this, new CoordinatorSyncCompletedEventArgs { Result = unreachableResult });
                    return unreachableResult;
                }

                if (BackoffPolicy.IsInBackoff)
                {
                    BackoffPolicy.Reset();
                    try
                    {
                        _periodicTimer?.Change(_periodicInterval, _periodicInterval);
                    }
                    catch (ObjectDisposedException) { }
                }

                // 2. Fetch fresh remote snapshot (Mitigating Risk R2)
                var remoteFiles = await _api.ListFilesAsync(includeDeleted: false, limit: 10000, ct: ct);
                _currentSnapshot!.Load(remoteFiles.Items, remoteFiles.SnapshotSeq, remoteFiles.ServerTimeUtc);

                // 3. Pre-sync seeding pass (Mitigating Risk R8: both-new untracked files)
                await ExecuteSeedingPassAsync(ct);

                // 4. Run SharpSync engine
                var options = new SyncOptions
                {
                    ChecksumOnly = false,
                    ConflictResolution = ConflictResolution.Ask, // Passes control to IConflictResolver
                    DeleteExtraneous = false,
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
                    syncResult = await _engine!.SynchronizeAsync(options, ct);
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

            } while (_pendingSyncRequested && !_isDisposed && !ct.IsCancellationRequested);

            return lastResult ?? new ClientSyncResult(true, 0, 0, 0, sw.Elapsed, null);
        }
        catch (OperationCanceledException) when (_isDisposed || cancellationToken.IsCancellationRequested)
        {
            return new ClientSyncResult(false, 0, 0, 0, sw.Elapsed, "Sync cancelled.");
        }
        catch (Exception) when (_isDisposed)
        {
            return new ClientSyncResult(false, 0, 0, 0, sw.Elapsed, "Coordinator disposed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync cycle failed with exception");
            LastError = ex.Message;
            if (ex is System.Net.Http.HttpRequestException || ex is System.IO.IOException || ex is TimeoutException)
            {
                var delay = BackoffPolicy.RecordFailure();
                try { _periodicTimer?.Change(delay, delay); } catch (ObjectDisposedException) { }
            }
            var result = new ClientSyncResult(false, 0, 0, 1, sw.Elapsed, ex.Message);
            SyncCompleted?.Invoke(this, new CoordinatorSyncCompletedEventArgs { Result = result });
            return result;
        }
        finally
        {
            IsSynchronizing = false;
            if (_isDisposed)
            {
                _guardedDb?.Dispose();
                _engine?.Dispose();
            }
            try
            {
                _syncLock.Release();
            }
            catch (ObjectDisposedException)
            {
            }
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
            if (remote.IsDeleted)
                continue;

            var normPath = RemoteSnapshot.NormalizePath(remote.Path);
            var localFullPath = Path.Combine(_localFolderPath, normPath.Replace('/', Path.DirectorySeparatorChar));
            var existsLocally = remote.IsDirectory ? Directory.Exists(localFullPath) : File.Exists(localFullPath);

            if (!existsLocally)
            {
                // If the item is missing locally but has a newer server version than what we last
                // synced (e.g., it was restored from Trash), clear the stale SyncState so SharpSync
                // downloads it rather than treating the missing local file as a new deletion.
                var tracked = await _guardedDb.GetSyncStateAsync(normPath, cancellationToken);
                if (tracked != null && tracked.ETag != remote.Version.ToString())
                {
                    _logger.LogInformation("Clearing stale sync state for restored/updated remote item '{Path}' (tracked ETag={TrackedETag}, server v{ServerVersion})",
                        normPath, tracked.ETag, remote.Version);
                    await _guardedDb.DeleteSyncStateAsync(normPath, cancellationToken);
                }
                continue;
            }

            if (remote.IsDirectory)
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

    public void StartBackgroundSync(TimeSpan? periodicInterval = null, TimeSpan? cooldown = null, TimeSpan? maxWait = null)
    {
        if (_watcher != null) return;

        if (cooldown.HasValue) CooldownDuration = cooldown.Value;
        if (maxWait.HasValue) MaxWaitDuration = maxWait.Value;

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
        _periodicInterval = periodicInterval ?? TimeSpan.FromMinutes(5);
        var interval = _periodicInterval;
        _periodicTimer = new Timer(async _ =>
        {
            if (_isDisposed) return;
            try
            {
                if (!IsPaused && !IsSynchronizing)
                {
                    _logger.LogInformation("Periodic background sync timer fired.");
                    await RunSyncAsync();
                }
            }
            catch (ObjectDisposedException)
            {
                // Coordinator disposed while timer callback was in flight
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in periodic background sync timer.");
            }
        }, null, interval, interval);

        _logger.LogInformation("Background sync started for '{Folder}' (Periodic: {PeriodicSec}s, Cooldown: {CooldownSec}s, MaxWait: {MaxWaitSec}s)",
            _localFolderPath, interval.TotalSeconds, CooldownDuration.TotalSeconds, MaxWaitDuration.TotalSeconds);
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
        _firstChangeTimeUtc = null;

        _periodicTimer?.Dispose();
        _periodicTimer = null;

        _logger.LogInformation("Background sync stopped.");
    }

    public void TriggerFileSystemChange(string path, WatcherChangeTypes changeType)
    {
        OnFileSystemEvent(this, new FileSystemEventArgs(changeType, _localFolderPath, path));
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        if (_isDisposed)
            return;

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

        if (e is RenamedEventArgs renamed)
        {
            _logger.LogInformation("Local file/folder rename detected: '{OldPath}' -> '{NewPath}'", renamed.OldName, renamed.Name);
        }
        else
        {
            _logger.LogInformation("Local file/folder {ChangeType} detected: '{Path}'", e.ChangeType, e.Name);
        }

        lock (_debounceLock)
        {
            var now = DateTime.UtcNow;
            _firstChangeTimeUtc ??= now;

            // Compute delay until trigger: whichever is shorter between remaining cooldown and remaining max wait
            var elapsedSinceFirst = now - _firstChangeTimeUtc.Value;
            var remainingMaxWait = MaxWaitDuration - elapsedSinceFirst;
            if (remainingMaxWait < TimeSpan.Zero) remainingMaxWait = TimeSpan.Zero;

            var delay = remainingMaxWait < CooldownDuration ? remainingMaxWait : CooldownDuration;
            var delayMs = (int)Math.Max(50, delay.TotalMilliseconds);

            if (_debounceTimer == null)
            {
                _debounceTimer = new Timer(async _ =>
                {
                    if (_isDisposed) return;
                    try
                    {
                        lock (_debounceLock)
                        {
                            _firstChangeTimeUtc = null;
                        }

                        if (!IsPaused && !IsSynchronizing)
                        {
                            _logger.LogInformation("Cooldown elapsed for local changes; starting sync.");
                            await RunSyncAsync();
                        }
                        else
                        {
                            _pendingSyncRequested = true;
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        // Coordinator disposed while debounce callback was in flight
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in file watcher cooldown sync.");
                    }
                }, null, delayMs, Timeout.Infinite);
            }
            else
            {
                _debounceTimer.Change(delayMs, Timeout.Infinite);
            }
        }
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

        try
        {
            _disposeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        StopBackgroundSync();

        if (_syncLock.Wait(0))
        {
            try
            {
                _guardedDb?.Dispose();
                _engine?.Dispose();
            }
            finally
            {
                _syncLock.Release();
                _syncLock.Dispose();
                _disposeCts.Dispose();
            }
        }
    }
}
