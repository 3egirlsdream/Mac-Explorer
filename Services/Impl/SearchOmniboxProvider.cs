using MacExplorer.Models;
using MacExplorer.Indexing;
using MacExplorer.ViewModels;
using AppIcons = MacExplorer.Assets.Icons;

namespace MacExplorer.Services.Impl;

/// <summary>Provides file search results from the FTS5 index.</summary>
public class SearchOmniboxProvider : IOmniboxProvider
{
    private const int MaxSearchSuggestions = 40;
    private readonly IFileIndex? _fileIndex;
    private readonly IGlobalSearchScopeService? _scopeService;
    private readonly ISearchService? _searchService;
    private readonly IGlobalSearchService? _globalSearchService;

    public SearchOmniboxProvider(
        IFileIndex? fileIndex = null,
        IGlobalSearchScopeService? scopeService = null,
        ISearchService? searchService = null)
    {
        _fileIndex = fileIndex;
        _scopeService = scopeService;
        _searchService = searchService;
        _globalSearchService = searchService as IGlobalSearchService;
    }

    public string Name => "Search";
    public int Priority => 10;

    public async Task<IReadOnlyList<OmniboxSuggestion>> GetSuggestionsAsync(
        FileListViewModel viewModel,
        string input,
        CancellationToken cancellationToken)
    {
        var value = input?.Trim() ?? string.Empty;
        if (value.Length == 0)
            return [];

        // Unlike an in-folder Finder search, global quick search deliberately
        // queries the complete local index. The index only contains locations that
        // Mac Explorer was permitted to read and has already indexed.
        var results = _searchService != null
            ? await SearchWithMacExplorerLogicAsync(viewModel, value, cancellationToken)
            : _fileIndex != null
                ? await SearchIndexForScopeAsync(viewModel, value, cancellationToken)
            : await viewModel.GetSearchSuggestionsAsync(value, MaxSearchSuggestions, cancellationToken);

        var suggestions = new List<OmniboxSuggestion>();
        foreach (var entry in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            suggestions.Add(new OmniboxSuggestion(
                OmniboxSuggestionKind.Result,
                entry.Name,
                entry.FullPath,
                entry.FullPath,
                entry.IsDirectory ? AppIcons.Folder : AppIcons.File,
                entry.IsDirectory ? "#54A3F7" : "#7C8798",
                entry));
        }

        return suggestions;
    }

    private async Task<IReadOnlyList<FileSystemEntry>> SearchWithMacExplorerLogicAsync(
        FileListViewModel viewModel,
        string query,
        CancellationToken cancellationToken)
    {
        var scope = _scopeService?.Scope ?? GlobalSearchScope.ThisMac;
        var root = scope switch
        {
            GlobalSearchScope.CurrentFolder => viewModel.CurrentPath,
            GlobalSearchScope.UserFolder => viewModel.HomeDirectory,
            GlobalSearchScope.CustomFolders => null,
            _ => Path.GetPathRoot(viewModel.CurrentPath)
        };

        if (scope == GlobalSearchScope.CustomFolders)
        {
            var customFolders = _scopeService?.CustomFolders ?? [];
            if (customFolders.Count == 0)
                return [];

            return await SearchRootsAsync(customFolders, query, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                   ?? Path.DirectorySeparatorChar.ToString();

        // Reuse the indexed Mac Explorer pipeline. It combines direct entries,
        // the FTS index and OCR/AI tags without recursively scanning the whole
        // disk for every query.
        return await SearchRootsAsync([root], query, cancellationToken);
    }

    private async Task<IReadOnlyList<FileSystemEntry>> SearchRootsAsync(
        IEnumerable<string> roots,
        string query,
        CancellationToken cancellationToken)
    {
        var results = new List<FileSystemEntry>(MaxSearchSuggestions);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var searchService = _searchService
            ?? throw new InvalidOperationException("Search service is unavailable.");

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            var searchResults = _globalSearchService != null
                ? _globalSearchService.SearchGlobalAsync(root, query, MaxSearchSuggestions, cancellationToken)
                : searchService.SearchAsync(root, query, MaxSearchSuggestions, cancellationToken);
            await foreach (var entry in searchResults)
            {
                if (!seen.Add(entry.FullPath)) continue;
                results.Add(entry);
                if (results.Count >= MaxSearchSuggestions)
                    return results;
            }
        }

        return results;
    }

    private async Task<IReadOnlyList<FileSystemEntry>> SearchIndexForScopeAsync(
        FileListViewModel viewModel,
        string query,
        CancellationToken cancellationToken)
    {
        // Query a larger candidate set before applying scope. Otherwise unrelated
        // matches at the top of the global index could hide valid local results.
        var candidates = await _fileIndex!.SearchByNameAsync(query, MaxSearchSuggestions * 8);
        cancellationToken.ThrowIfCancellationRequested();

        var scope = _scopeService?.Scope ?? GlobalSearchScope.ThisMac;
        var root = scope switch
        {
            GlobalSearchScope.CurrentFolder => viewModel.CurrentPath,
            GlobalSearchScope.UserFolder => viewModel.HomeDirectory,
            _ => null
        };

        return string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)
            ? candidates.Take(MaxSearchSuggestions).ToArray()
            : candidates.Where(entry => IsWithin(root, entry.FullPath))
                .Take(MaxSearchSuggestions)
                .ToArray();
    }

    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(path, normalizedRoot, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }
}
