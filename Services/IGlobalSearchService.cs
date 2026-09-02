using MacExplorer.Models;

namespace MacExplorer.Services;

/// <summary>
/// Fast app-wide search entry point. Implementations can use the local index
/// without forcing every keystroke to recurse through the entire file system.
/// </summary>
public interface IGlobalSearchService
{
    IAsyncEnumerable<FileSystemEntry> SearchGlobalAsync(
        string directory,
        string pattern,
        int maxResults = 500,
        CancellationToken cancellationToken = default);
}
