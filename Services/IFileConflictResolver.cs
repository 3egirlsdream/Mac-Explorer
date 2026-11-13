using MacExplorer.Models;

namespace MacExplorer.Services;

public enum FileConflictAction { Skip, KeepBoth, Replace, ReplaceOlder }
public sealed record FileConflict(FileSystemEntry Source, FileSystemEntry Destination, bool IsMove, bool IsRemote);
public sealed record FileConflictDecision(FileConflictAction Action, bool ApplyToRemaining = false);
public interface IFileConflictResolver
{
    Task<FileConflictDecision> ResolveAsync(FileConflict conflict, CancellationToken cancellationToken);
}
