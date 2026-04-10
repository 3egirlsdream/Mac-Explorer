using FKFinder.Indexing;
using FKFinder.Models;
using FKFinder.Services;

namespace FKFinder.Platforms.MacCatalyst.Services;

/// <summary>
/// Mac Catalyst implementation of ISearchService.
/// Uses SQLite FTS5 index for fast search, falls back to file system enumeration.
/// </summary>
public class MacSearchService : ISearchService
{
    private readonly IFileIndex _fileIndex;
    private readonly IFileService _fileService;
    private readonly IndexConfiguration _indexConfig;

    public MacSearchService(IFileIndex fileIndex, IFileService fileService, IndexConfiguration indexConfig)
    {
        _fileIndex = fileIndex;
        _fileService = fileService;
        _indexConfig = indexConfig;
    }

    public async IAsyncEnumerable<FileSystemEntry> SearchAsync(
        string directory,
        string pattern,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            yield break;

        bool hasMatchingResults = false;

        // Try FTS5 index search first
        if (_indexConfig.ShouldIndex(directory))
        {
            var indexResults = await _fileIndex.SearchByNameAsync(pattern, 200);
            foreach (var entry in indexResults)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Filter results to current directory tree
                if (entry.FullPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
                {
                    hasMatchingResults = true;
                    yield return entry;
                }
            }

            // Only skip filesystem fallback if we found results IN the current directory
            if (hasMatchingResults)
                yield break;
        }

        // Fallback: file system search
        var fsResults = await SearchFileSystemAsync(directory, pattern, cancellationToken);
        foreach (var entry in fsResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }
    }

    private async Task<List<FileSystemEntry>> SearchFileSystemAsync(
        string directory,
        string pattern,
        CancellationToken cancellationToken)
    {
        var results = new List<FileSystemEntry>();
        await SearchFileSystemRecursiveAsync(directory, pattern, results, cancellationToken);
        return results;
    }

    private async Task SearchFileSystemRecursiveAsync(
        string directory,
        string pattern,
        List<FileSystemEntry> results,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<FileSystemEntry> entries;
        try
        {
            entries = await _fileService.GetDirectoryContentsAsync(directory, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(entry);
            }

            // Recurse into subdirectories (limited depth)
            if (entry.IsDirectory && !entry.Name.StartsWith('.'))
            {
                try
                {
                    await SearchFileSystemRecursiveAsync(entry.FullPath, pattern, results, cancellationToken);
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip inaccessible directories
                }
            }
        }
    }
}
