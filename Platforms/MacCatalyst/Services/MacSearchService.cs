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
    private static readonly HashSet<string> RecursiveSearchExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".svn",
        ".hg",
        "node_modules",
        "bin",
        "obj",
        ".cache",
        ".gradle",
        ".idea",
        ".vscode",
        "DerivedData",
        "Library"
    };

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
        int maxResults = 500,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            yield break;

        var yieldedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var yieldedCount = 0;
        var limit = Math.Max(1, maxResults);
        IReadOnlyList<FileSystemEntry>? rootEntries = null;

        try
        {
            rootEntries = await _fileService.GetDirectoryContentsAsync(directory, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            rootEntries = null;
        }

        if (rootEntries != null)
        {
            foreach (var entry in rootEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                    && yieldedPaths.Add(entry.FullPath))
                {
                    yieldedCount++;
                    yield return entry;
                    if (yieldedCount >= limit)
                        yield break;
                }
            }
        }

        // Try FTS5 index search first (skip if index is unavailable)
        if (_fileIndex != null && _indexConfig.ShouldIndex(directory))
        {
            List<FileSystemEntry>? indexResults = null;
            try
            {
                indexResults = new List<FileSystemEntry>(await _fileIndex.SearchByNameAsync(pattern, limit));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"FTS5 search failed, falling back to filesystem: {ex.Message}");
            }

            if (indexResults != null)
            {
                foreach (var entry in indexResults)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                        && IsPathWithinDirectory(entry.FullPath, directory)
                        && yieldedPaths.Add(entry.FullPath))
                    {
                        yieldedCount++;
                        yield return entry;
                        if (yieldedCount >= limit)
                            yield break;
                    }
                }

                // Also search AI tags for additional matches
                await foreach (var aiEntry in SearchAiTagsAsync(pattern, directory, yieldedPaths, limit - yieldedCount, cancellationToken))
                {
                    yieldedCount++;
                    yield return aiEntry;
                    if (yieldedCount >= limit)
                        yield break;
                }
            }
        }

        // Fallback: recursive file system search
        await foreach (var entry in SearchFileSystemRecursiveAsync(
                           directory,
                           pattern,
                           yieldedPaths,
                           new SearchState(limit - yieldedCount),
                           cancellationToken,
                           initialEntries: rootEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }
    }

    private async IAsyncEnumerable<FileSystemEntry> SearchFileSystemRecursiveAsync(
        string directory,
        string pattern,
        HashSet<string> yieldedPaths,
        SearchState state,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        int depth = 0,
        IReadOnlyList<FileSystemEntry>? initialEntries = null)
    {
        if (state.Remaining <= 0 || depth > 8)
            yield break;

        IReadOnlyList<FileSystemEntry> entries;
        if (initialEntries != null)
        {
            entries = initialEntries;
        }
        else
        {
            try
            {
                entries = await _fileService.GetDirectoryContentsAsync(directory, cancellationToken);
            }
            catch
            {
                yield break;
            }
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.Remaining <= 0)
                yield break;

            if (entry.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                && yieldedPaths.Add(entry.FullPath))
            {
                state.Remaining--;
                yield return entry;
            }

            if (ShouldRecurseInto(entry))
            {
                await foreach (var child in SearchFileSystemRecursiveAsync(
                                   entry.FullPath,
                                   pattern,
                                   yieldedPaths,
                                   state,
                                   cancellationToken,
                                   depth + 1))
                {
                    yield return child;
                }
            }
        }
    }

    private static bool ShouldRecurseInto(FileSystemEntry entry)
    {
        return entry.IsDirectory
            && !entry.IsSymbolicLink
            && !entry.Name.StartsWith('.')
            && !RecursiveSearchExcludedDirectories.Contains(entry.Name);
    }

    private async IAsyncEnumerable<FileSystemEntry> SearchAiTagsAsync(
        string pattern,
        string directory,
        HashSet<string> excludePaths,
        int maxResults,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (maxResults <= 0)
            yield break;

        IReadOnlyList<string> aiPaths;
        try
        {
            aiPaths = await _aiTagService.SearchByTagAsync(pattern, null, maxResults);
        }
        catch
        {
            yield break;
        }

        foreach (var path in aiPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsPathWithinDirectory(path, directory))
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

    private sealed class SearchState(int remaining)
    {
        public int Remaining { get; set; } = remaining;
    }

    private static bool IsPathWithinDirectory(string path, string directory)
    {
        var normalizedDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        if (normalizedDirectory.Length == 0)
            return normalizedPath.StartsWith(Path.DirectorySeparatorChar);

        return string.Equals(normalizedPath, normalizedDirectory, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }
}
