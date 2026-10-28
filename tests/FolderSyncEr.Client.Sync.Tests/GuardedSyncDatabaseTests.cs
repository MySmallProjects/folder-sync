using System;
using System.IO;
using System.Threading.Tasks;
using FolderSyncEr.Client.Sync.Database;
using FolderSyncEr.Client.Sync.Registry;
using FolderSyncEr.Client.Sync.Storage;
using FolderSyncEr.Contracts;
using Oire.SharpSync.Core;
using Oire.SharpSync.Database;
using Xunit;

namespace FolderSyncEr.Client.Sync.Tests;

public class GuardedSyncDatabaseTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteSyncDatabase _rawDb;
    private readonly RunFailureRegistry _registry;
    private readonly RemoteSnapshot _snapshot;
    private readonly GuardedSyncDatabase _guardedDb;

    public GuardedSyncDatabaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"guarded_{Guid.NewGuid():N}.db");
        _rawDb = new SqliteSyncDatabase(_dbPath);
        _registry = new RunFailureRegistry();
        _snapshot = new RemoteSnapshot();
        _guardedDb = new GuardedSyncDatabase(_rawDb, _registry, _snapshot);
    }

    [Fact]
    public async Task UpdateSyncState_SuppressesSyncedStatus_WhenRunFailureRecorded()
    {
        await _guardedDb.InitializeAsync();

        var path = "failed_upload.txt";
        _registry.RecordFailure(path, new InvalidOperationException("409 Conflict"));

        var state = new SyncState
        {
            Path = path,
            Status = SyncStatus.Synced,
            LocalHash = "hash1",
            RemoteHash = "hash2"
        };

        await _guardedDb.UpdateSyncStateAsync(state);

        var retrieved = await _guardedDb.GetSyncStateAsync(path);
        Assert.NotNull(retrieved);
        // Mitigating R1: State was not marked as Synced!
        Assert.Equal(SyncStatus.Error, retrieved.Status);
        Assert.Contains("409 Conflict", retrieved.ErrorMessage);
    }

    [Fact]
    public async Task UpdateSyncState_StampsServerVersionIntoETag_WhenSynced()
    {
        await _guardedDb.InitializeAsync();

        var path = "good_file.txt";
        var remoteDto = new FileDto("id-123", path, false, 100, "abc", 42, DateTime.UtcNow, DateTime.UtcNow, "dev", false, 1);
        _snapshot.AddOrUpdate(remoteDto);

        var state = new SyncState
        {
            Path = path,
            Status = SyncStatus.Synced,
            LocalHash = "hash1",
            RemoteHash = "hash1"
        };

        await _guardedDb.UpdateSyncStateAsync(state);

        var retrieved = await _guardedDb.GetSyncStateAsync(path);
        Assert.NotNull(retrieved);
        Assert.Equal(SyncStatus.Synced, retrieved.Status);
        // Server version 42 stamped into ETag column
        Assert.Equal("42", retrieved.ETag);
    }

    public void Dispose()
    {
        _guardedDb.Dispose();
        try
        {
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
        catch { }
    }
}
