using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderSyncEr.Client.Sync.Conflicts;
using Oire.SharpSync.Core;
using Xunit;

namespace FolderSyncEr.Client.Sync.Tests;

public class PolicyConflictResolverTests : IDisposable
{
    private readonly string _testDir;

    public PolicyConflictResolverTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"conflict_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public async Task MatchingHashes_ResolvesToSkip()
    {
        var resolver = new PolicyConflictResolver(_testDir, "test-pc", ConflictPolicy.KeepBoth);
        var conflict = new FileConflictEventArgs(
            "same.txt",
            new SyncItem { Path = "same.txt", Hash = "base64-hash-xyz", LastModified = DateTime.UtcNow },
            new SyncItem { Path = "same.txt", Hash = "base64-hash-xyz", LastModified = DateTime.UtcNow.AddMinutes(5) },
            ConflictType.BothModified);

        var resolution = await resolver.ResolveConflictAsync(conflict, default);
        Assert.Equal(ConflictResolution.Skip, resolution);
    }

    [Fact]
    public async Task NewestWins_ChoosesNewerTimestamp()
    {
        var resolver = new PolicyConflictResolver(_testDir, "test-pc", ConflictPolicy.NewestWins);

        // Remote newer
        var conflict1 = new FileConflictEventArgs(
            "remote_newer.txt",
            new SyncItem { Path = "remote_newer.txt", Hash = "hash-1", LastModified = DateTime.UtcNow.AddMinutes(-10) },
            new SyncItem { Path = "remote_newer.txt", Hash = "hash-2", LastModified = DateTime.UtcNow },
            ConflictType.BothModified);
        Assert.Equal(ConflictResolution.UseRemote, await resolver.ResolveConflictAsync(conflict1, default));

        // Local newer
        var conflict2 = new FileConflictEventArgs(
            "local_newer.txt",
            new SyncItem { Path = "local_newer.txt", Hash = "hash-1", LastModified = DateTime.UtcNow },
            new SyncItem { Path = "local_newer.txt", Hash = "hash-2", LastModified = DateTime.UtcNow.AddMinutes(-10) },
            ConflictType.BothModified);
        Assert.Equal(ConflictResolution.UseLocal, await resolver.ResolveConflictAsync(conflict2, default));
    }

    [Fact]
    public async Task KeepBoth_CreatesConflictCopy_AndReturnsUseRemote()
    {
        var resolver = new PolicyConflictResolver(_testDir, "laptop-42", ConflictPolicy.KeepBoth);
        var relativePath = "work/report.docx";
        var localFullPath = Path.Combine(_testDir, "work", "report.docx");
        Directory.CreateDirectory(Path.GetDirectoryName(localFullPath)!);
        await File.WriteAllTextAsync(localFullPath, "Local changes that must be preserved");

        var conflict = new FileConflictEventArgs(
            relativePath,
            new SyncItem { Path = relativePath, Hash = "local-hash", LastModified = DateTime.UtcNow },
            new SyncItem { Path = relativePath, Hash = "remote-hash", LastModified = DateTime.UtcNow },
            ConflictType.BothModified);

        var resolution = await resolver.ResolveConflictAsync(conflict, default);
        Assert.Equal(ConflictResolution.UseRemote, resolution);

        // Verify conflict file was created matching pattern: report (conflict laptop-42 <timestamp>).docx
        var workDir = Path.Combine(_testDir, "work");
        var conflictFiles = Directory.GetFiles(workDir, "report (conflict laptop-42 *).docx");
        Assert.Single(conflictFiles);
        var conflictContent = await File.ReadAllTextAsync(conflictFiles[0]);
        Assert.Equal("Local changes that must be preserved", conflictContent);
    }

    [Fact]
    public async Task Prompt_InvokesPromptHandler_AndMapsChoice()
    {
        var fakeHandler = new FakePromptHandler(ConflictResolutionChoice.KeepLocal);
        var resolver = new PolicyConflictResolver(_testDir, "desktop-1", ConflictPolicy.Prompt, promptHandler: fakeHandler);

        var conflict = new FileConflictEventArgs(
            "notes.txt",
            new SyncItem { Path = "notes.txt", Size = 10, Hash = "h1", LastModified = DateTime.UtcNow },
            new SyncItem { Path = "notes.txt", Size = 20, Hash = "h2", LastModified = DateTime.UtcNow },
            ConflictType.BothModified);

        var resolution = await resolver.ResolveConflictAsync(conflict, default);
        Assert.Equal(ConflictResolution.UseLocal, resolution);
        Assert.NotNull(fakeHandler.LastPrompt);
        Assert.Equal("notes.txt", fakeHandler.LastPrompt.Path);
        Assert.Equal(10, fakeHandler.LastPrompt.LocalSize);
        Assert.Equal(20, fakeHandler.LastPrompt.RemoteSize);
    }

    private class FakePromptHandler : IConflictPromptHandler
    {
        private readonly ConflictResolutionChoice _choiceToReturn;
        public ConflictPromptInfo? LastPrompt { get; private set; }

        public FakePromptHandler(ConflictResolutionChoice choiceToReturn)
        {
            _choiceToReturn = choiceToReturn;
        }

        public Task<ConflictResolutionChoice> RequestResolutionAsync(ConflictPromptInfo prompt, CancellationToken cancellationToken)
        {
            LastPrompt = prompt;
            return Task.FromResult(_choiceToReturn);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch { }
    }
}
