using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using FolderSyncEr.Client.Sync.Common;
using FolderSyncEr.Contracts;
using Oire.SharpSync.Core;

namespace FolderSyncEr.Client.Sync.Storage;

/// <summary>
/// Immutable or thread-safe snapshot of remote file metadata for a single sync run.
/// Prevents SharpSync from hitting the network repeatedly during directory traversal (Mitigating Risk R2).
/// </summary>
public sealed class RemoteSnapshot
{
    private readonly ConcurrentDictionary<string, FileDto> _items = new(StringComparer.OrdinalIgnoreCase);

    public long SnapshotSeq { get; set; }
    public DateTime ServerTimeUtc { get; set; } = DateTime.UtcNow;

    public RemoteSnapshot()
    {
    }

    public RemoteSnapshot(IEnumerable<FileDto> initialItems, long snapshotSeq, DateTime serverTimeUtc)
    {
        SnapshotSeq = snapshotSeq;
        ServerTimeUtc = serverTimeUtc;
        foreach (var item in initialItems)
        {
            if (!item.IsDeleted)
            {
                var norm = NormalizePath(item.Path);
                _items[norm] = item;
            }
        }
    }

    public void AddOrUpdate(FileDto item)
    {
        if (item.IsDeleted)
        {
            Remove(item.Path);
        }
        else
        {
            _items[NormalizePath(item.Path)] = item;
        }
    }

    public void Remove(string path)
    {
        _items.TryRemove(NormalizePath(path), out _);
    }

    public bool TryGetFileDto(string path, out FileDto? dto)
    {
        return _items.TryGetValue(NormalizePath(path), out dto);
    }

    public SyncItem? GetItem(string path)
    {
        var norm = NormalizePath(path);
        if (string.IsNullOrEmpty(norm))
        {
            return new SyncItem
            {
                Path = "",
                IsDirectory = true,
                LastModified = ServerTimeUtc,
                Size = 0
            };
        }

        if (_items.TryGetValue(norm, out var dto))
        {
            return ToSyncItem(dto);
        }

        // Check if it exists as an implicit directory containing other items
        var prefix = norm + "/";
        if (_items.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return new SyncItem
            {
                Path = norm,
                IsDirectory = true,
                LastModified = ServerTimeUtc,
                Size = 0
            };
        }

        return null;
    }

    public IEnumerable<SyncItem> ListChildren(string path)
    {
        var norm = NormalizePath(path);
        var directChildren = new Dictionary<string, SyncItem>(StringComparer.OrdinalIgnoreCase);

        var prefix = string.IsNullOrEmpty(norm) ? "" : norm + "/";
        var prefixLen = prefix.Length;

        foreach (var (key, dto) in _items)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var remainder = key.Substring(prefixLen);
            if (string.IsNullOrEmpty(remainder))
                continue;

            var slashIdx = remainder.IndexOf('/');
            if (slashIdx < 0)
            {
                // Direct child
                directChildren[remainder] = ToSyncItem(dto);
            }
            else
            {
                // Implicit sub-directory
                var dirName = remainder.Substring(0, slashIdx);
                if (!directChildren.ContainsKey(dirName))
                {
                    var childPath = string.IsNullOrEmpty(norm) ? dirName : $"{norm}/{dirName}";
                    directChildren[dirName] = new SyncItem
                    {
                        Path = childPath,
                        IsDirectory = true,
                        LastModified = ServerTimeUtc,
                        Size = 0
                    };
                }
            }
        }

        return directChildren.Values;
    }

    public IReadOnlyCollection<FileDto> GetAllItems() => _items.Values.ToList();

    public static SyncItem ToSyncItem(FileDto dto)
    {
        return new SyncItem
        {
            Path = NormalizePath(dto.Path),
            IsDirectory = dto.IsDirectory,
            Size = dto.Size,
            LastModified = dto.LastModifiedUtc,
            Hash = HashConverter.HexToBase64(dto.Sha256) ?? string.Empty,
            ETag = dto.Version.ToString(),
            CloudFileId = dto.Id
        };
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return path.Replace('\\', '/').Trim('/');
    }
}
