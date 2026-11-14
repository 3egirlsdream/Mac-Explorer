namespace MacExplorer.Indexing;

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
        "/private/var",
        "/private/tmp",
        "/cores",
        "/.vol",
        "/dev",
        "/.Trashes"
    };

    /// <summary>
    /// How long a directory listing is considered "fresh" before re-scanning.
    /// </summary>
    public TimeSpan FreshnessThreshold { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Database file path.
    /// </summary>
    public string DatabasePath { get; set; } = RuntimePaths.DatabasePath;

    /// <summary>
    /// Minimum interval between two whole-root scans of the same root. A filesystem storm
    /// on a large root otherwise restarts a pass before the previous one finishes and never
    /// converges, rewriting the same index pages indefinitely.
    /// </summary>
    public TimeSpan FullScanCooldown { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Minimum interval between two writes of the search index status file.
    /// </summary>
    public TimeSpan DiagnosticWriteInterval { get; set; } = TimeSpan.FromSeconds(10);

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
