using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderSyncEr.Client.Api;
using FolderSyncEr.Client.Sync.Common;
using FolderSyncEr.Client.Sync.Registry;
using FolderSyncEr.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Oire.SharpSync.Core;
using Oire.SharpSync.Storage;

namespace FolderSyncEr.Client.Sync.Storage;

/// <summary>
/// Custom remote storage adapter translating SharpSync engine storage operations into REST Web API calls.
/// Mitigates R1 (failures registered), R2 (snapshot caching), R7 (hex/base64 hash bridging), and R10 (optimistic concurrency).
/// </summary>
public sealed class RestRemoteStorage : ISyncStorage
{
    private readonly IFileSyncApi _api;
    private readonly RemoteSnapshot _snapshot;
    private readonly RunFailureRegistry _failureRegistry;
    private readonly ILogger<RestRemoteStorage> _logger;

    public RestRemoteStorage(
        IFileSyncApi api,
        RemoteSnapshot snapshot,
        RunFailureRegistry failureRegistry,
        ILogger<RestRemoteStorage>? logger = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _failureRegistry = failureRegistry ?? throw new ArgumentNullException(nameof(failureRegistry));
        _logger = logger ?? NullLogger<RestRemoteStorage>.Instance;
    }

    public StorageType StorageType => StorageType.WebDav;

    public string RootPath => _api.ServerUrl;

    public event EventHandler<StorageProgressEventArgs>? ProgressChanged;

    public Task<IEnumerable<SyncItem>> ListItemsAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var children = _snapshot.ListChildren(path);
        return Task.FromResult(children);
    }

    public async Task<SyncItem?> GetItemAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var item = _snapshot.GetItem(path);
        if (item != null)
        {
            return item;
        }

        // Snapshot miss (e.g. freshly created file): query server metadata
        try
        {
            var dto = await _api.GetFileMetaByPathAsync(path, cancellationToken);
            if (dto != null && !dto.IsDeleted)
            {
                _snapshot.AddOrUpdate(dto);
                return RemoteSnapshot.ToSyncItem(dto);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Metadata query failed for path '{Path}'", path);
        }

        return null;
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var item = await GetItemAsync(path, cancellationToken);
        return item != null;
    }

    public Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var item = _snapshot.GetItem(path);
        if (item != null && !string.IsNullOrEmpty(item.Hash))
        {
            return Task.FromResult(item.Hash);
        }

        return Task.FromResult(string.Empty);
    }

    public async Task<Stream> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_snapshot.TryGetFileDto(path, out var dto) || dto == null)
        {
            dto = await _api.GetFileMetaByPathAsync(path, cancellationToken);
        }

        if (dto == null || dto.IsDeleted)
        {
            throw new FileNotFoundException($"Remote file '{path}' does not exist.");
        }

        var stream = await _api.DownloadFileStreamAsync(dto.Id, null, cancellationToken);

        return new ProgressTrackingStream(
            stream,
            path,
            dto.Size,
            StorageOperation.Download,
            args => ProgressChanged?.Invoke(this, args));
    }

    public async Task WriteFileAsync(string path, Stream content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            string? ifMatchSha256 = null;
            string? ifNoneMatch = null;
            long? baseVersion = null;

            if (_snapshot.TryGetFileDto(path, out var existing) && existing != null)
            {
                baseVersion = existing.Version;
                if (!string.IsNullOrEmpty(existing.Sha256))
                {
                    ifMatchSha256 = existing.Sha256;
                }
            }
            else
            {
                ifNoneMatch = "*";
            }

            var progressStream = new ProgressTrackingStream(
                content,
                path,
                content.CanSeek ? content.Length : -1,
                StorageOperation.Upload,
                args => ProgressChanged?.Invoke(this, args));

            var result = await _api.UploadFileStreamAsync(
                path: path,
                contentStream: progressStream,
                sha256Hex: null,
                ifMatchSha256: ifMatchSha256,
                ifNoneMatch: ifNoneMatch,
                baseVersion: baseVersion,
                ct: cancellationToken);

            if (result.IsConflict)
            {
                var conflictEx = new InvalidOperationException($"Server conflict 409 for '{path}': {result.ErrorMessage}");
                _logger.LogWarning("Remote 409 Conflict during upload for '{Path}'", path);
                _failureRegistry.RecordFailure(path, conflictEx);
                throw conflictEx;
            }

            if (!result.IsSuccess || result.File == null)
            {
                var errorEx = new IOException($"Remote upload failed for '{path}': {result.ErrorMessage}");
                _logger.LogError("Remote write failed for '{Path}': {Error}", path, result.ErrorMessage);
                _failureRegistry.RecordFailure(path, errorEx);
                throw errorEx;
            }

            _snapshot.AddOrUpdate(result.File);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remote write failed for '{Path}'", path);
            _failureRegistry.RecordFailure(path, ex);
            throw;
        }
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var dto = await _api.CreateDirectoryAsync(path, cancellationToken);
            _snapshot.AddOrUpdate(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create remote directory '{Path}'", path);
            _failureRegistry.RecordFailure(path, ex);
            throw;
        }
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!_snapshot.TryGetFileDto(path, out var dto) || dto == null)
            {
                dto = await _api.GetFileMetaByPathAsync(path, cancellationToken);
            }

            if (dto != null)
            {
                var success = await _api.DeleteFileAsync(dto.Id, ifMatchSha256: dto.Sha256, ct: cancellationToken);
                if (success)
                {
                    _snapshot.Remove(path);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete remote file '{Path}'", path);
            _failureRegistry.RecordFailure(path, ex);
            throw;
        }
    }

    public async Task MoveAsync(string sourcePath, string targetPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!_snapshot.TryGetFileDto(sourcePath, out var dto) || dto == null)
            {
                dto = await _api.GetFileMetaByPathAsync(sourcePath, cancellationToken);
            }

            var updated = await _api.MoveFileAsync(sourcePath, targetPath, ifMatchSha256: dto?.Sha256, ct: cancellationToken);
            if (updated != null)
            {
                _snapshot.Remove(sourcePath);
                _snapshot.AddOrUpdate(updated);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move remote file '{Source}' to '{Target}'", sourcePath, targetPath);
            _failureRegistry.RecordFailure(sourcePath, ex);
            throw;
        }
    }

    public async Task<StorageInfo> GetStorageInfoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var info = await _api.GetStorageInfoAsync(cancellationToken);
            return new StorageInfo
            {
                TotalSpace = info.TotalSpace,
                UsedSpace = info.UsedSpace,
                Quota = info.Quota
            };
        }
        catch
        {
            return new StorageInfo
            {
                TotalSpace = 0,
                UsedSpace = 0
            };
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await _api.CheckConnectionAsync(cancellationToken);
            return status.IsConnected;
        }
        catch
        {
            return false;
        }
    }

    public Task SetLastModifiedAsync(string path, DateTime lastModified, CancellationToken cancellationToken = default)
    {
        // Last-modified is passed during streamed upload headers (X-Last-Modified-Utc);
        // separate PATCH is not required for normal sync.
        return Task.CompletedTask;
    }

    public Task SetPermissionsAsync(string path, string permissions, CancellationToken cancellationToken = default)
    {
        // Permissions not supported by REST backend; no-op
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChangeInfo>> GetRemoteChangesAsync(DateTime since, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<ChangeInfo>>(Array.Empty<ChangeInfo>());
    }
}
