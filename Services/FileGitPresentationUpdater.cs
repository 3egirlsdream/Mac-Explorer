using MacExplorer.Models;

namespace MacExplorer.Services;

/// <summary>Call on the UI thread, after validating the directory generation.</summary>
public static class FileGitPresentationUpdater
{
    public static int Apply(IReadOnlyList<FileSystemEntry> current, IReadOnlyList<FileSystemEntry> resolved)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(resolved);
        var byPath = new Dictionary<string, FileSystemEntry>(StringComparer.Ordinal);
        foreach (var entry in resolved) byPath[entry.FullPath] = entry;
        var changed = 0;
        foreach (var entry in current)
        {
            if (!byPath.TryGetValue(entry.FullPath, out var state)
                || entry.LastModified != state.LastModified) continue;
            if (entry.GitStatus == state.GitStatus && entry.HasGitChanges == state.HasGitChanges) continue;
            entry.GitStatus = state.GitStatus;
            entry.HasGitChanges = state.HasGitChanges;
            changed++;
        }
        return changed;
    }
}
