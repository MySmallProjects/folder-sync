using System;
using System.Collections.Generic;
using System.Linq;
using FolderSyncEr.Client.Sync.Storage;
using FolderSyncEr.Contracts;
using Xunit;

namespace FolderSyncEr.Client.Sync.Tests;

public class RemoteSnapshotTests
{
    [Fact]
    public void ListChildren_ReturnsDirectChildren_AndSynthesizesIntermediateDirectories()
    {
        var items = new List<FileDto>
        {
            new("id-1", "notes/daily/entry1.txt", false, 100, "e3b0c442", 1, DateTime.UtcNow, DateTime.UtcNow, "dev-1", false, 1),
            new("id-2", "notes/todo.txt", false, 50, "a1b2c3d4", 1, DateTime.UtcNow, DateTime.UtcNow, "dev-1", false, 2),
            new("id-3", "readme.md", false, 200, "f5e6d7c8", 1, DateTime.UtcNow, DateTime.UtcNow, "dev-1", false, 3)
        };

        var snapshot = new RemoteSnapshot(items, snapshotSeq: 3, DateTime.UtcNow);

        // Root children: 'notes' (directory) and 'readme.md' (file)
        var rootChildren = snapshot.ListChildren("").ToList();
        Assert.Equal(2, rootChildren.Count);
        Assert.Contains(rootChildren, c => c.Path == "notes" && c.IsDirectory);
        Assert.Contains(rootChildren, c => c.Path == "readme.md" && !c.IsDirectory);

        // 'notes' children: 'notes/daily' (directory) and 'notes/todo.txt' (file)
        var notesChildren = snapshot.ListChildren("notes").ToList();
        Assert.Equal(2, notesChildren.Count);
        Assert.Contains(notesChildren, c => c.Path == "notes/daily" && c.IsDirectory);
        Assert.Contains(notesChildren, c => c.Path == "notes/todo.txt" && !c.IsDirectory);

        // 'notes/daily' children: 'notes/daily/entry1.txt'
        var dailyChildren = snapshot.ListChildren("notes/daily").ToList();
        Assert.Single(dailyChildren);
        Assert.Equal("notes/daily/entry1.txt", dailyChildren[0].Path);
    }

    [Fact]
    public void AddOrUpdate_And_Remove_WorkCorrectly()
    {
        var snapshot = new RemoteSnapshot();
        var dto = new FileDto("id-1", "test.txt", false, 50, "e3b0c442", 1, DateTime.UtcNow, DateTime.UtcNow, "dev-1", false, 1);

        snapshot.AddOrUpdate(dto);
        Assert.True(snapshot.TryGetFileDto("test.txt", out var retrieved));
        Assert.NotNull(retrieved);
        Assert.Equal("id-1", retrieved.Id);

        // Tombstone update removes from active snapshot
        var deletedDto = dto with { IsDeleted = true };
        snapshot.AddOrUpdate(deletedDto);
        Assert.False(snapshot.TryGetFileDto("test.txt", out _));
    }
}
