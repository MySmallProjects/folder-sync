using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderSyncEr.Client.Sync.Registry;
using FolderSyncEr.Client.Sync.Storage;
using FolderSyncEr.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Oire.SharpSync.Core;
using Oire.SharpSync.Database;

namespace FolderSyncEr.Client.Sync.Database;

/// <summary>
/// Decorates ISyncDatabase with:
/// 1. Suppression of incorrect 'Synced' status for failed/conflicted files (Mitigating Risk R1).
/// 2. Version tagging in SyncState.ETag for UI display without extra databases.
/// </summary>
public sealed class GuardedSyncDatabase : ISyncDatabase
{
    private readonly ISyncDatabase _inner;
    private readonly RunFailureRegistry _failureRegistry;
    private readonly RemoteSnapshot _snapshot;
    private readonly ILogger<GuardedSyncDatabase> _logger;

    public GuardedSyncDatabase(
        ISyncDatabase inner,
        RunFailureRegistry failureRegistry,
        RemoteSnapshot snapshot,
        ILogger<GuardedSyncDatabase>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _failureRegistry = failureRegistry ?? throw new ArgumentNullException(nameof(failureRegistry));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _logger = logger ?? NullLogger<GuardedSyncDatabase>.Instance;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return _inner.InitializeAsync(cancellationToken);
    }

    public Task<SyncState?> GetSyncStateAsync(string path, CancellationToken cancellationToken = default)
    {
        return _inner.GetSyncStateAsync(path, cancellationToken);
    }

    public async Task UpdateSyncStateAsync(SyncState state, CancellationToken cancellationToken = default)
    {
        if (state == null) return;

        // Mitigate R1: Check if this file failed during the current run
        if (_failureRegistry.TryGetFailure(state.Path, out var failure) && failure != null)
        {
            if (state.Status == SyncStatus.Synced)
            {
                _logger.LogWarning("Prevented marking '{Path}' as Synced due to recorded failure: {Error}", state.Path, failure.Message);
                state.Status = SyncStatus.Error;
                state.ErrorMessage = failure.Message;
            }
        }
        else if (state.Status == SyncStatus.Synced)
        {
            // Stamp server version into ETag column for UI display
            if (_snapshot.TryGetFileDto(state.Path, out var dto) && dto != null)
            {
                state.ETag = dto.Version.ToString();
            }
            else if (string.IsNullOrEmpty(state.LocalHash) && string.IsNullOrEmpty(state.RemoteHash))
            {
                // Completed deletion: remove the state entry so a later server-side restore
                // is detected as a new remote item and downloaded rather than re-deleted.
                await _inner.DeleteSyncStateAsync(state.Path, cancellationToken);
                return;
            }
        }

        await _inner.UpdateSyncStateAsync(state, cancellationToken);
    }

    public Task DeleteSyncStateAsync(string path, CancellationToken cancellationToken = default)
    {
        return _inner.DeleteSyncStateAsync(path, cancellationToken);
    }

    public Task<IEnumerable<SyncState>> GetAllSyncStatesAsync(CancellationToken cancellationToken = default)
    {
        return _inner.GetAllSyncStatesAsync(cancellationToken);
    }

    public Task<IEnumerable<SyncState>> GetSyncStatesByPrefixAsync(string pathPrefix, CancellationToken cancellationToken = default)
    {
        return _inner.GetSyncStatesByPrefixAsync(pathPrefix, cancellationToken);
    }

    public Task<IEnumerable<SyncState>> GetPendingSyncStatesAsync(CancellationToken cancellationToken = default)
    {
        return _inner.GetPendingSyncStatesAsync(cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        return _inner.ClearAsync(cancellationToken);
    }

    public Task<DatabaseStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        return _inner.GetStatsAsync(cancellationToken);
    }

    public Task LogOperationAsync(
        string path,
        SyncActionType actionType,
        bool isDirectory,
        long size,
        ChangeSource source,
        DateTime startedAt,
        DateTime completedAt,
        bool success,
        string? errorMessage = null,
        string? renamedFrom = null,
        string? renamedTo = null,
        CancellationToken cancellationToken = default)
    {
        return _inner.LogOperationAsync(
            path, actionType, isDirectory, size, source, startedAt, completedAt, success, errorMessage, renamedFrom, renamedTo, cancellationToken);
    }

    public Task<IReadOnlyList<CompletedOperation>> GetRecentOperationsAsync(int limit, DateTime? since = null, CancellationToken cancellationToken = default)
    {
        return _inner.GetRecentOperationsAsync(limit, since, cancellationToken);
    }

    public Task<int> ClearOperationHistoryAsync(DateTime olderThan, CancellationToken cancellationToken = default)
    {
        return _inner.ClearOperationHistoryAsync(olderThan, cancellationToken);
    }

    public void Dispose()
    {
        _inner.Dispose();
    }
}
