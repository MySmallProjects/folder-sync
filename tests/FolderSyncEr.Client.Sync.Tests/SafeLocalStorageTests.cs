using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FolderSyncEr.Client.Sync.Storage;
using Xunit;

namespace FolderSyncEr.Client.Sync.Tests;

public class SafeLocalStorageTests : IDisposable
{
    private readonly string _testDir;
    private readonly SafeLocalStorage _storage;

    public SafeLocalStorageTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "foldersyncer_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _storage = new SafeLocalStorage(_testDir);
    }

    [Fact]
    public async Task WriteFileAsync_WritesAtomically_AndPreservesContent()
    {
        var relativePath = "sub/atomic_test.txt";
        var content = "Hello atomic transfer!";
        var bytes = Encoding.UTF8.GetBytes(content);

        using (var ms = new MemoryStream(bytes))
        {
            await _storage.WriteFileAsync(relativePath, ms, default);
        }

        var fullPath = Path.Combine(_testDir, "sub", "atomic_test.txt");
        Assert.True(File.Exists(fullPath));
        var readContent = await File.ReadAllTextAsync(fullPath);
        Assert.Equal(content, readContent);
    }

    [Fact]
    public async Task DeleteAsync_MovesFileToTrash_RatherThanHardDeleting()
    {
        var relativePath = "docs/report.txt";
        var fullPath = Path.Combine(_testDir, "docs", "report.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "Important user document");

        await _storage.DeleteAsync(relativePath, default);

        // Original file path is gone
        Assert.False(File.Exists(fullPath));

        // But file exists inside .foldersyncer/trash/
        var trashBase = Path.Combine(_testDir, ".foldersyncer", "trash");
        Assert.True(Directory.Exists(trashBase));

        var trashFiles = Directory.GetFiles(trashBase, "report.txt", SearchOption.AllDirectories);
        Assert.Single(trashFiles);
        var trashContent = await File.ReadAllTextAsync(trashFiles[0]);
        Assert.Equal("Important user document", trashContent);
    }

    [Fact]
    public async Task ComputeHashAsync_CachesHashBasedOnSizeAndMtime()
    {
        var relativePath = "cached.txt";
        var fullPath = Path.Combine(_testDir, relativePath);
        await File.WriteAllTextAsync(fullPath, "Hash cache test");

        var hash1 = await _storage.ComputeHashAsync(relativePath, default);
        Assert.False(string.IsNullOrEmpty(hash1));

        // Second compute should hit the in-memory cache
        var hash2 = await _storage.ComputeHashAsync(relativePath, default);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public async Task ReadFileAsync_WhenFileIsLocked_RecordsFailureInRegistry()
    {
        var failureRegistry = new FolderSyncEr.Client.Sync.Registry.RunFailureRegistry();
        var storageWithRegistry = new SafeLocalStorage(_testDir, failureRegistry: failureRegistry);

        var relativePath = "locked.txt";
        var fullPath = Path.Combine(_testDir, relativePath);
        await File.WriteAllTextAsync(fullPath, "Locked file content");

        // Open with FileShare.None to simulate exclusive lock from another process
        using (var lockStream = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => storageWithRegistry.ReadFileAsync(relativePath, default));
            Assert.True(failureRegistry.HasFailed(relativePath));
            Assert.True(failureRegistry.TryGetFailure(relativePath, out var ex));
            Assert.IsAssignableFrom<IOException>(ex);
        }
    }

    [Fact]
    public async Task ComputeHashAsync_WhenFileIsLocked_RecordsFailureInRegistry()
    {
        var failureRegistry = new FolderSyncEr.Client.Sync.Registry.RunFailureRegistry();
        var storageWithRegistry = new SafeLocalStorage(_testDir, failureRegistry: failureRegistry);

        var relativePath = "locked_hash.txt";
        var fullPath = Path.Combine(_testDir, relativePath);
        await File.WriteAllTextAsync(fullPath, "Locked file content for hash");

        // Open with FileShare.None to simulate exclusive lock from another process
        using (var lockStream = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => storageWithRegistry.ComputeHashAsync(relativePath, default));
            Assert.True(failureRegistry.HasFailed(relativePath));
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // Ignore temp cleanup errors
        }
    }
}

