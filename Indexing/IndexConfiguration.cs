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

    public TimeSpan DiagnosticWriteInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan OverflowFullScanCooldown { get; set; } = TimeSpan.FromMinutes(10);
    internal TimeSpan EventMergeDelay { get; set; } = TimeSpan.FromMilliseconds(200);
    internal int PendingLimit { get; set; } = 4096;
    internal long PendingBytesLimit { get; set; } = 8 * 1024 * 1024;

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
