using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Oire.SharpSync.Core;

namespace FolderSyncEr.Client.Sync.Conflicts;

/// <summary>
/// Pluggable conflict resolver adhering to the user-selected ConflictPolicy.
/// Handles:
/// - KeepBoth: clones local loser to 'name (conflict <device> <timestamp>).ext' then UseRemote.
/// - NewestWins: compares LastModified UTC timestamps.
/// - Prompt: requests interactive user decision via IConflictPromptHandler.
/// </summary>
public sealed class PolicyConflictResolver : IConflictResolver
{
    private readonly string _localRootPath;
    private readonly string _deviceId;
    private readonly IConflictPromptHandler? _promptHandler;
    private readonly ILogger<PolicyConflictResolver> _logger;

    public ConflictPolicy Policy { get; set; } = ConflictPolicy.KeepBoth;

    public PolicyConflictResolver(
        string localRootPath,
        string deviceId,
        ConflictPolicy policy = ConflictPolicy.KeepBoth,
        IConflictPromptHandler? promptHandler = null,
        ILogger<PolicyConflictResolver>? logger = null)
    {
        _localRootPath = Path.GetFullPath(localRootPath ?? throw new ArgumentNullException(nameof(localRootPath)));
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? Environment.MachineName : deviceId;
        Policy = policy;
        _promptHandler = promptHandler;
        _logger = logger ?? NullLogger<PolicyConflictResolver>.Instance;
    }

    public async Task<ConflictResolution> ResolveConflictAsync(FileConflictEventArgs conflict, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation("Conflict detected on '{Path}': Type={ConflictType}", conflict.Path, conflict.ConflictType);

        var localHash = conflict.LocalItem?.Hash;
        var remoteHash = conflict.RemoteItem?.Hash;

        // If local hash is missing, compute it directly from disk
        if (string.IsNullOrEmpty(localHash))
        {
            var relativePath = conflict.Path.Replace('/', Path.DirectorySeparatorChar);
            var localFullPath = Path.Combine(_localRootPath, relativePath);
            if (File.Exists(localFullPath))
            {
                try
                {
                    using var stream = new FileStream(localFullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                    using var sha256 = System.Security.Cryptography.SHA256.Create();
                    var hashBytes = await sha256.ComputeHashAsync(stream, cancellationToken);
                    localHash = Convert.ToBase64String(hashBytes);
                }
                catch { }
            }
        }

        // Normalize if either hash is in hex format (64 chars)
        if (!string.IsNullOrEmpty(localHash) && localHash.Length == 64 && !localHash.EndsWith("="))
        {
            localHash = Common.HashConverter.HexToBase64(localHash) ?? localHash;
        }
        if (!string.IsNullOrEmpty(remoteHash) && remoteHash.Length == 64 && !remoteHash.EndsWith("="))
        {
            remoteHash = Common.HashConverter.HexToBase64(remoteHash) ?? remoteHash;
        }

        // Identical hashes mean identical content; no need to overwrite or create conflict files
        if (!string.IsNullOrEmpty(localHash) &&
            !string.IsNullOrEmpty(remoteHash) &&
            string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Conflict on '{Path}' resolved automatically as Skip (hashes match)", conflict.Path);
            return ConflictResolution.Skip;
        }

        switch (Policy)
        {
            case ConflictPolicy.NewestWins:
                return ResolveNewestWins(conflict, localHash, remoteHash);

            case ConflictPolicy.Prompt when _promptHandler != null:
                return await ResolvePromptAsync(conflict, localHash, remoteHash, cancellationToken);

            case ConflictPolicy.KeepBoth:
            default:
                return ResolveKeepBoth(conflict, localHash, remoteHash);
        }
    }

    private ConflictResolution ResolveNewestWins(FileConflictEventArgs conflict, string? localHash, string? remoteHash)
    {
        if (!string.IsNullOrEmpty(localHash) &&
            !string.IsNullOrEmpty(remoteHash) &&
            string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
        {
            return ConflictResolution.Skip;
        }

        var localMtime = conflict.LocalItem?.LastModified ?? DateTime.MinValue;
        var remoteMtime = conflict.RemoteItem?.LastModified ?? DateTime.MinValue;

        if (remoteMtime > localMtime)
        {
            _logger.LogInformation("Conflict on '{Path}' resolved as UseRemote (server newer: {Remote} > {Local})",
                conflict.Path, remoteMtime, localMtime);
            return ConflictResolution.UseRemote;
        }
        else
        {
            _logger.LogInformation("Conflict on '{Path}' resolved as UseLocal (local newer: {Local} >= {Remote})",
                conflict.Path, localMtime, remoteMtime);
            return ConflictResolution.UseLocal;
        }
    }

    private async Task<ConflictResolution> ResolvePromptAsync(
        FileConflictEventArgs conflict,
        string? localHash,
        string? remoteHash,
        CancellationToken cancellationToken)
    {
        var info = new ConflictPromptInfo(
            Path: conflict.Path,
            LocalSize: conflict.LocalItem?.Size ?? 0,
            LocalModifiedUtc: conflict.LocalItem?.LastModified ?? DateTime.MinValue,
            LocalHash: localHash,
            RemoteSize: conflict.RemoteItem?.Size ?? 0,
            RemoteModifiedUtc: conflict.RemoteItem?.LastModified ?? DateTime.MinValue,
            RemoteHash: remoteHash,
            DeviceName: _deviceId
        );

        var choice = await _promptHandler!.RequestResolutionAsync(info, cancellationToken);
        return choice switch
        {
            ConflictResolutionChoice.KeepLocal => ConflictResolution.UseLocal,
            ConflictResolutionChoice.KeepServer => ConflictResolution.UseRemote,
            ConflictResolutionChoice.KeepBoth => ResolveKeepBoth(conflict, localHash, remoteHash),
            _ => ConflictResolution.Skip
        };
    }

    private ConflictResolution ResolveKeepBoth(FileConflictEventArgs conflict, string? localHash, string? remoteHash)
    {
        try
        {
            var relativePath = conflict.Path.Replace('/', Path.DirectorySeparatorChar);
            var localFullPath = Path.Combine(_localRootPath, relativePath);

            // Double check if local content is identical to remote before creating any conflict files
            if (!string.IsNullOrEmpty(localHash) &&
                !string.IsNullOrEmpty(remoteHash) &&
                string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("KeepBoth: Content is identical for '{Path}'; skipping conflict creation.", conflict.Path);
                return ConflictResolution.Skip;
            }

            if (File.Exists(localFullPath))
            {
                var dir = Path.GetDirectoryName(localFullPath) ?? _localRootPath;
                var fileName = Path.GetFileNameWithoutExtension(localFullPath);
                var extension = Path.GetExtension(localFullPath);
                var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");

                var conflictFileName = $"{fileName} (conflict {_deviceId} {timestamp}){extension}";
                var conflictFullPath = Path.Combine(dir, conflictFileName);

                File.Copy(localFullPath, conflictFullPath, overwrite: true);
                _logger.LogInformation("KeepBoth: Preserved local conflict copy as '{ConflictFile}'", conflictFileName);

                // Now use remote so that the original file is overwritten with the server version
                return ConflictResolution.UseRemote;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create conflict copy for '{Path}' during KeepBoth", conflict.Path);
        }

        // Fallback if copy fails
        return ConflictResolution.UseRemote;
    }
}

