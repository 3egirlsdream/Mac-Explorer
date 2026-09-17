using CommunityToolkit.Mvvm.ComponentModel;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Search;

namespace MacExplorer.ViewModels;

public partial class SearchViewModel : ObservableObject
{
    private readonly ISearchService? _searchService;
    private CancellationTokenSource? _searchCts;
    private long _generation;

    [ObservableProperty] private bool _isSearchMode;
    [ObservableProperty] private string _searchQuery = string.Empty;
    private bool _wasHomePageBeforeSearch;
    public bool WasHomePageBeforeSearch => _wasHomePageBeforeSearch;

    public SearchViewModel(ISearchService? searchService = null) => _searchService = searchService;

    public void EnterSearchMode(bool isHomePage)
    {
        if (!IsSearchMode) _wasHomePageBeforeSearch = isHomePage;
        IsSearchMode = true;
    }

    public void RestoreSearchMode(string query, bool wasHomePageBeforeSearch)
    {
        CancelSearch();
        _wasHomePageBeforeSearch = wasHomePageBeforeSearch;
        IsSearchMode = true;
        SearchQuery = query;
    }

    public void ExitSearchMode(bool restoreHomePage)
    {
        CancelSearch();
        IsSearchMode = false;
        SearchQuery = string.Empty;
        if (restoreHomePage && _wasHomePageBeforeSearch) _wasHomePageBeforeSearch = false;
    }

    public async Task SearchAsync(string query, string homeDirectory, string? currentPath,
        Action<IReadOnlyList<FileSystemEntry>> setEntries, Action<string> setStatus)
    {
        if (string.IsNullOrWhiteSpace(query)) { ExitSearchMode(true); return; }
        if (_searchService == null) return;
        CancelSearch();
        using var cts = new CancellationTokenSource();
        _searchCts = cts;
        var generation = _generation;
        var ct = cts.Token;
        IsSearchMode = true;
        SearchQuery = query;
        var root = string.IsNullOrEmpty(currentPath) ? homeDirectory : currentPath;
        bool IsCurrent() => !ct.IsCancellationRequested && generation == _generation;

        try
        {
            setStatus($"正在搜索 \"{query}\"...");
            await Task.Delay(120, ct); // Typing debounce; the CTS is owned by this invocation.
            const int maxResults = 500;
            if (_searchService is ISearchSessionService sessions)
            {
                await foreach (var snapshot in sessions.SearchSnapshotsAsync(root, query, maxResults, ct))
                {
                    if (!IsCurrent()) return;
                    setEntries(snapshot.Entries);
                    if (!IsCurrent()) return;
                    var count = snapshot.HasMore ? $"显示前 {maxResults} 项" : $"找到 {snapshot.Entries.Count} 项";
                    setStatus($"搜索 \"{query}\" — {count} · {snapshot.Status.Description}");
                }
            }
            else
            {
                var results = new List<FileSystemEntry>();
                await foreach (var entry in _searchService.SearchAsync(root, query, maxResults, ct))
                {
                    if (!IsCurrent()) return;
                    results.Add(entry);
                    if (results.Count == 1 || results.Count % 25 == 0) setEntries(results.ToArray());
                }
                if (!IsCurrent()) return;
                setEntries(results.ToArray());
                if (!IsCurrent()) return;
                setStatus(results.Count >= maxResults
                    ? $"搜索 \"{query}\" — 显示前 {maxResults} 项"
                    : $"搜索 \"{query}\" — 找到 {results.Count} 项");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { if (IsCurrent()) setStatus($"搜索失败: {ex.Message}"); }
        finally { if (ReferenceEquals(_searchCts, cts)) _searchCts = null; }
    }

    public async Task<IReadOnlyList<FileSystemEntry>> GetSuggestionsAsync(string directory, string query,
        int maxResults, CancellationToken cancellationToken)
    {
        if (_searchService == null || string.IsNullOrWhiteSpace(query)) return [];
        var results = new List<FileSystemEntry>();
        await foreach (var entry in _searchService.SearchAsync(directory, query, maxResults, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(entry);
        }
        return results;
    }

    public void CancelSearch()
    {
        _generation++;
        _searchCts?.Cancel();
        // Do not dispose another invocation's CTS while it is registering callbacks.
        _searchCts = null;
    }

    public void Reset()
    {
        CancelSearch();
        IsSearchMode = false;
        SearchQuery = string.Empty;
    }
}
