using System.Runtime.CompilerServices;
using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Search;

namespace MacExplorer.Platforms.MacCatalyst.Services;

/// <summary>One query model for the omnibox and full search; indexing has its own lifetime.</summary>
public sealed class MacSearchService : ISearchService, IGlobalSearchService, ISearchSessionService
{
    private readonly SearchCatalog _catalog;
    private readonly SearchIndexer _indexer;
    private readonly IFileService _files;
    private readonly IPinyinInitials _pinyin;
    private readonly ISettingsService? _settings;

    public MacSearchService(SearchCatalog catalog, SearchIndexer indexer, IFileService files,
        IPinyinInitials pinyin, ISettingsService? settings = null)
    {
        _catalog = catalog;
        _indexer = indexer;
        _files = files;
        _pinyin = pinyin;
        _settings = settings;
    }

    private SearchOptions CaptureOptions() => new(
        _settings?.Get("HideSystemFiles", true) ?? true,
        _settings?.Get("HideDotFiles", true) ?? true,
        _settings?.Get("HideDotFolders", true) ?? true,
        _settings?.Get("SearchPinyinEnabled", true) ?? true);

    public IAsyncEnumerable<FileSystemEntry> SearchAsync(string directory, string pattern, int maxResults = 500,
        CancellationToken cancellationToken = default) => SearchOnceAsync(directory, pattern, maxResults, cancellationToken);

    public IAsyncEnumerable<FileSystemEntry> SearchGlobalAsync(string directory, string pattern, int maxResults = 500,
        CancellationToken cancellationToken = default) => SearchOnceAsync(directory, pattern, maxResults, cancellationToken);

    private async IAsyncEnumerable<FileSystemEntry> SearchOnceAsync(string directory, string pattern, int maxResults,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maxResults <= 0) yield break;
        var query = SearchQuery.Parse(pattern);
        if (query.IsEmpty) yield break;
        var options = CaptureOptions();
        if (!Path.IsPathFullyQualified(directory))
        {
            await foreach (var snapshot in SearchLiveAsync(directory, query, options, maxResults, cancellationToken))
                if (!snapshot.Status.IsBusy)
                    foreach (var entry in snapshot.Entries) { cancellationToken.ThrowIfCancellationRequested(); yield return entry; }
            yield break;
        }
        var root = SearchPath.Normalize(RuntimePaths.ResolveSearchRoot(directory));
        _indexer.EnsureRoot(root);
        var entries = await _catalog.SearchAsync(root, query, options, maxResults,
            _settings?.Get("SearchIncludeAiTags", true) ?? true, cancellationToken).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry.ToFileSystemEntry();
        }
    }

    public async IAsyncEnumerable<SearchSnapshot> SearchSnapshotsAsync(string directory, string input, int limit = 500,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit <= 0) yield break;
        var query = SearchQuery.Parse(input);
        var options = CaptureOptions();
        if (query.IsEmpty)
        {
            yield return new([], new(directory, SearchIndexPhase.Ready), false);
            yield break;
        }
        if (!Path.IsPathFullyQualified(directory))
        {
            await foreach (var snapshot in SearchLiveAsync(directory, query, options, limit, cancellationToken)) yield return snapshot;
            yield break;
        }
        var root = SearchPath.Normalize(RuntimePaths.ResolveSearchRoot(directory));
        var includeAi = _settings?.Get("SearchIncludeAiTags", true) ?? true;
        _indexer.EnsureRoot(root);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Capture the notification BEFORE reading the database, avoiding missed updates.
            var observation = _indexer.Observe(root);
            var entries = await _catalog.SearchAsync(root, query, options, checked(limit + 1), includeAi,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(entries.Take(limit).Select(entry => entry.ToFileSystemEntry()).ToArray(),
                observation.Status, entries.Count > limit);
            if (!observation.Status.IsBusy && !observation.Changed.IsCompleted) yield break;
            await observation.Changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
    }

    // Remote/virtual providers are not represented by the local SQLite catalog.
    // Only an explicit search of such a provider enumerates it; global local queries never do.
    private async IAsyncEnumerable<SearchSnapshot> SearchLiveAsync(string root, SearchQuery query, SearchOptions options,
        int limit, [EnumeratorCancellation] CancellationToken ct)
    {
        var pending = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<FileSystemEntry>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(directory)) continue;
            await foreach (var batch in _files.EnumerateDirectoryBatchesAsync(directory, 256, ct).ConfigureAwait(false))
            {
                foreach (var entry in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!options.IsVisible(entry.FullPath, entry.Name, entry.IsDirectory, root)) continue;
                    if (query.Matches(entry.Name, _files.GetParentPath(entry.FullPath), entry.Extension,
                        options.UsePinyin ? _pinyin.GetInitials(entry.Name) : "", options.UsePinyin))
                    {
                        results.Add(entry);
                        if (results.Count > limit)
                        {
                            yield return new(results.Take(limit).ToArray(), new(root, SearchIndexPhase.Ready, IsLiveScan: true), true);
                            yield break;
                        }
                    }
                    if (entry.IsDirectory && !entry.IsSymbolicLink && entry.Name is not ".git" and not "node_modules")
                        pending.Enqueue(entry.FullPath);
                }
                yield return new(results.ToArray(), new(root, SearchIndexPhase.Building, IsLiveScan: true), false);
            }
        }
        yield return new(results.ToArray(), new(root, SearchIndexPhase.Ready, IsLiveScan: true), false);
    }
}
