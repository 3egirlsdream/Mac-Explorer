using Foundation;
using MacExplorer.Services;
using System.Collections.Concurrent;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacDisplayNameService : IDisplayNameService
{
    private readonly ConcurrentDictionary<string, string> _cache = new();

    public string GetDisplayName(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return string.Empty;

        if (fullPath.StartsWith("__", StringComparison.Ordinal))
            return Path.GetFileName(fullPath);

        return _cache.GetOrAdd(fullPath, static path =>
        {
            var displayName = NSFileManager.DefaultManager.DisplayName(path);
            return string.IsNullOrEmpty(displayName) ? Path.GetFileName(path) : displayName;
        });
    }
}
