namespace MacExplorer.Models;

public sealed record GitPathState(string Path, char Index, char WorkTree, string? OriginalPath = null)
{
    public bool IsUntracked => Index == '?' && WorkTree == '?';
    public bool IsConflicted => Index == 'U' || WorkTree == 'U' || (Index, WorkTree) is ('A', 'A') or ('D', 'D');
    public bool HasStaged => !IsUntracked && Index != ' ';
    public bool HasUnstaged => IsUntracked || WorkTree != ' ';
}

public sealed record GitContext(string RepoRoot, IReadOnlyList<string> Paths, IReadOnlyList<GitPathState> Changes, string? Head, bool FollowFile)
{
    public bool HasStaged => Changes.Any(s => s.HasStaged);
    public bool HasUnstaged => Changes.Any(s => !s.IsUntracked && s.HasUnstaged);
    public bool CanStage => !HasConflicts && Changes.Any(s => s.HasUnstaged);
    public bool HasConflicts => Changes.Any(s => s.IsConflicted);
    public IReadOnlyList<string> DiscardPaths => Changes.Where(s => !s.IsUntracked && s.HasUnstaged).Select(s => s.Path).ToArray();
}

public enum GitMutation { Stage, Unstage, Discard }

public sealed record GitCommit(string Id, string Parents, string Author, DateTimeOffset Date, string Subject, IReadOnlyList<string> Paths)
{
    public string ShortId => Id[..Math.Min(8, Id.Length)];
    public string Detail => $"{Author} · {Date.ToLocalTime():g} · {ShortId}";
}

public sealed record GitHistoryPage(IReadOnlyList<GitCommit> Commits, bool HasMore);
