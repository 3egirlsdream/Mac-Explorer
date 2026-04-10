namespace FKFinder.Indexing;

public class IndexConfiguration
{
    /// <summary>
    /// Directories to exclude from indexing.
    /// </summary>
    public HashSet<string> ExcludedPaths { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "/System",
        "/Library",
        "/usr",
        "/bin",
        "/sbin",
        "/var/folders",
        "/private/var/folders",
        "/.vol",
        "/dev",
        "/.Trashes"
    };

    /// <summary>
    /// How long a directory listing is considered "fresh" before re-scanning.
    /// </summary>
    public TimeSpan FreshnessThreshold { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Database file path.
    /// </summary>
    public string DatabasePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FKFinder",
        "index.db"
    );

    /// <summary>
    /// Check if a path should be indexed.
    /// </summary>
    public bool ShouldIndex(string path)
    {
        foreach (var excluded in ExcludedPaths)
        {
            if (path.StartsWith(excluded, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}
