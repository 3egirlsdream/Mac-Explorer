namespace MacExplorer.Models;

public enum GitFileStatus
{
    None,
    Ignored,
    Unmodified,
    Modified,
    Staged,
    Added,
    Deleted,
    Renamed,
    Untracked,
    Conflicted
}

public class GitRepoStatus
{
    public string RepoRoot { get; init; } = string.Empty;
    public Dictionary<string, GitFileStatus> FileStatuses { get; init; } = new(StringComparer.Ordinal);
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    public bool IsValid => (DateTime.UtcNow - LastUpdated) < TimeSpan.FromSeconds(5);

    public bool HasAnyChange(string directoryPath)
    {
        var prefix = directoryPath.EndsWith('/') ? directoryPath : directoryPath + '/';
        foreach (var (path, status) in FileStatuses)
        {
            if (status == GitFileStatus.Ignored) continue;
            if (path.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
