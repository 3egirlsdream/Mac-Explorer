using FKFinder.Models;

namespace FKFinder.Services;

public interface ISearchService
{
    IAsyncEnumerable<FileSystemEntry> SearchAsync(string directory, string pattern, CancellationToken cancellationToken = default);
}
