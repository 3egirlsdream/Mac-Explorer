using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

/// <summary>
/// Mac Catalyst implementation of ISearchService.
/// Uses SQLite FTS5 index for fast search, falls back to file system enumeration.
/// </summary>
public class MacSearchService : ISearchService
{
    private readonly IFileIndex _fileIndex;
    private readonly IFileService _fileService;
    private readonly IndexConfiguration _indexConfig;
    private readonly IAiTagService _aiTagService;

    public MacSearchService(IFileIndex fileIndex, IFileService fileService, IndexConfiguration indexConfig, IAiTagService aiTagService)
    {
        _fileIndex = fileIndex;
        _fileService = fileService;
        _indexConfig = indexConfig;
        _aiTagService = aiTagService;
    }

    public async IAsyncEnumerable<FileSystemEntry> SearchAsync(
        string directory,
        string pattern,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            yield break;

        bool hasIndexResults = false;

        // Try FTS5 index search first (skip if index is unavailable)
        if (_fileIndex != null && _indexConfig.ShouldIndex(directory))
        {
            List<FileSystemEntry>? indexResults = null;
            try
            {
                indexResults = new List<FileSystemEntry>(await _fileIndex.SearchByNameAsync(pattern, 200));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"FTS5 search failed, falling back to filesystem: {ex.Message}");
            }

            if (indexResults != null)
            {
                var yieldedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in indexResults)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.FullPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
                    {
                        hasIndexResults = true;
                        yieldedPaths.Add(entry.FullPath);
                        yield return entry;
                    }
                }

                // Also search AI tags for additional matches
                await foreach (var aiEntry in SearchAiTagsAsync(pattern, directory, yieldedPaths, cancellationToken))
                {
                    hasIndexResults = true;
                    yield return aiEntry;
                }

                if (hasIndexResults)
                    yield break;
            }
        }

        // Fallback: recursive file system search
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

    private async IAsyncEnumerable<FileSystemEntry> SearchAiTagsAsync(
        string pattern,
        string directory,
        HashSet<string> excludePaths,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IReadOnlyList<string> aiPaths;
        try
        {
            aiPaths = await _aiTagService.SearchByTagAsync(pattern, null, 100);
        }
        catch
        {
            yield break;
        }

        foreach (var path in aiPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!path.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!excludePaths.Add(path))
                continue;
            if (!File.Exists(path))
                continue;

            var ext = Path.GetExtension(path);
            yield return new FileSystemEntry
            {
                FullPath = path,
                Name = Path.GetFileName(path),
                IsDirectory = false,
                Extension = ext,
                IconKey = SqliteFileIndex.ResolveIconKey(ext)
            };
        }
    }
}
