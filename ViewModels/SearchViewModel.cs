using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.ViewModels;

public partial class SearchViewModel : ObservableObject
{
    private readonly ISearchService? _searchService;

    private CancellationTokenSource? _searchCts;

    [ObservableProperty]
    private bool _isSearchMode;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    private bool _wasHomePageBeforeSearch;

    public bool WasHomePageBeforeSearch => _wasHomePageBeforeSearch;

    public SearchViewModel(ISearchService? searchService = null)
    {
        _searchService = searchService;
    }

    public void EnterSearchMode(bool isHomePage)
    {
        if (!IsSearchMode)
            _wasHomePageBeforeSearch = isHomePage;
        IsSearchMode = true;
    }

    public void ExitSearchMode(bool restoreHomePage)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        IsSearchMode = false;
        SearchQuery = string.Empty;

        // Restore home page if search was initiated from there
        if (restoreHomePage && _wasHomePageBeforeSearch)
        {
            _wasHomePageBeforeSearch = false;
        }
    }

    public async Task SearchAsync(
        string query,
        string homeDirectory,
        string? currentPath,
        Action<IReadOnlyList<FileSystemEntry>> setEntries,
        Action<string> setStatus)
    {
        if (string.IsNullOrWhiteSpace(query)) { ExitSearchMode(true); return; }
        if (_searchService == null) return;

        _searchCts?.Cancel(); _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();

        IsSearchMode = true;
        SearchQuery = query;

        // Use HomeDirectory as search root when there's no current path
        var searchPath = string.IsNullOrEmpty(currentPath) ? homeDirectory : currentPath;

        try
        {
            var results = new List<FileSystemEntry>();
            setEntries([]);
            setStatus($"正在搜索 \"{query}\"...");

            const int maxResults = 500;
            await foreach (var entry in _searchService.SearchAsync(searchPath, query, maxResults, _searchCts.Token))
            {
                results.Add(entry);
                if (results.Count == 1 || results.Count % 25 == 0)
                {
                    setEntries(results.ToArray());
                    setStatus($"正在搜索 \"{query}\" — 已找到 {results.Count} 项");
                }
            }
            if (results.Count == 0 || results.Count % 25 != 0)
                setEntries(results);
            setStatus(results.Count >= maxResults
                ? $"搜索 \"{query}\" — 显示前 {maxResults} 项"
                : $"搜索 \"{query}\" — 找到 {results.Count} 项");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { setStatus($"搜索失败: {ex.Message}"); }
    }

    public async Task<IReadOnlyList<FileSystemEntry>> GetSuggestionsAsync(
        string directory,
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (_searchService == null || string.IsNullOrWhiteSpace(query))
            return [];

        var results = new List<FileSystemEntry>();
        await foreach (var entry in _searchService.SearchAsync(
                           directory,
                           query,
                           maxResults,
                           cancellationToken))
        {
            results.Add(entry);
        }

        return results;
    }

    public void CancelSearch()
    {
        _searchCts?.Cancel();
    }

    public void Reset()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        IsSearchMode = false;
        SearchQuery = string.Empty;
    }
}
