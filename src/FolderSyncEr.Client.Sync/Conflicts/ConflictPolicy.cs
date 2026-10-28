namespace FolderSyncEr.Client.Sync.Conflicts;

public enum ConflictPolicy
{
    KeepBoth,
    NewestWins,
    Prompt
}

public enum ConflictResolutionChoice
{
    KeepLocal,
    KeepServer,
    KeepBoth
}

public sealed record ConflictPromptInfo(
    string Path,
    long LocalSize,
    System.DateTime LocalModifiedUtc,
    string? LocalHash,
    long RemoteSize,
    System.DateTime RemoteModifiedUtc,
    string? RemoteHash,
    string DeviceName
);

public interface IConflictPromptHandler
{
    System.Threading.Tasks.Task<ConflictResolutionChoice> RequestResolutionAsync(
        ConflictPromptInfo prompt,
        System.Threading.CancellationToken cancellationToken);
}

