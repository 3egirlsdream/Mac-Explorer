namespace FKFinder.Services;

/// <summary>
/// Watches filesystem directories for external changes (via FSEvents on macOS).
/// Uses reference counting so multiple windows watching the same directory
/// share one watch.
/// </summary>
public interface IFSEventsWatcher
{
    /// <summary>Start watching a directory. Reference counted.</summary>
    void WatchDirectory(string path);

    /// <summary>Stop watching a directory. Decrements reference count; removes at zero.</summary>
    void UnwatchDirectory(string path);
}
