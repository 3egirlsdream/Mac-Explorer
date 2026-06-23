using MacExplorer.Models;

namespace MacExplorer.Services;

public interface ISearchService
{
    IAsyncEnumerable<FileSystemEntry> SearchAsync(
        string directory,
        string pattern,
        int maxResults = 500,
        CancellationToken cancellationToken = default);
}
