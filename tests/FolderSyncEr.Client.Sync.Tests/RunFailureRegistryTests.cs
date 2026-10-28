using System;
using FolderSyncEr.Client.Sync.Registry;
using Xunit;

namespace FolderSyncEr.Client.Sync.Tests;

public class RunFailureRegistryTests
{
    [Fact]
    public void RecordFailure_NormalizesPath_AndDetectsFailure()
    {
        var registry = new RunFailureRegistry();
        var ex = new InvalidOperationException("Transfer interrupted");

        registry.RecordFailure("subfolder\\file.txt", ex);

        Assert.True(registry.HasFailed("subfolder/file.txt"));
        Assert.True(registry.HasFailed("SUBFOLDER/FILE.TXT"));
        Assert.True(registry.TryGetFailure("subfolder/file.txt", out var retrieved));
        Assert.Same(ex, retrieved);

        Assert.False(registry.HasFailed("other.txt"));

        registry.Clear();
        Assert.False(registry.HasFailed("subfolder/file.txt"));
    }
}

