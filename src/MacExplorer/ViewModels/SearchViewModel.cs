using System.Collections.ObjectModel;
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
        IsSearchMode = false;
        SearchQuery = string.Empty;

        // Restore home page if search was initiated from there
        if (restoreHomePage && _wasHomePageBeforeSearch)
        {
            _wasHomePageBeforeSearch = false;
        }
    }

    public async Task SearchAsync(string query, string homeDirectory, string? currentPath, Action<ObservableCollection<FileSystemEntry>> setEntries, Action<string> setStatus)
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
            var results = new ObservableCollection<FileSystemEntry>();
            await foreach (var entry in _searchService.SearchAsync(searchPath, query, _searchCts.Token))
                results.Add(entry);
            setEntries(results);
            setStatus($"搜索 \"{query}\" — 找到 {results.Count} 项");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { setStatus($"搜索失败: {ex.Message}"); }
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