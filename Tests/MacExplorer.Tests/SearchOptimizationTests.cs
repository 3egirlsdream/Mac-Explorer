using System.Runtime.CompilerServices;
using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Search;
using MacExplorer.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MacExplorer.Tests;

public sealed class SearchOptimizationTests
{
    private static SearchEntry Entry(string path, string initials = "", bool directory = false, bool link = false)
    {
        var name = Path.GetFileName(path);
        var parent = Path.GetDirectoryName(path)!;
        return new(path, name, parent, Path.GetExtension(name), SearchQuery.Fold(name), initials,
            SearchQuery.Fold(parent), 12, directory, name.StartsWith('.'), DateTime.UnixEpoch.Ticks,
            DateTime.UnixEpoch.Ticks, link);
    }

    private sealed class Database : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "mac-search-test-" + Guid.NewGuid().ToString("N"));
        public string PathName => Path.Combine(Folder, "index.db");
        public DatabaseConnectionFactory Factory { get; }
        public SearchCatalog Catalog { get; }
        public Database(bool ai = false)
        {
            Directory.CreateDirectory(Folder);
            Factory = new(PathName);
            if (ai)
            {
                using var connection = Factory.GetConnection();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "CREATE TABLE ai_tags(file_path TEXT,tag_value TEXT); CREATE INDEX tag_path ON ai_tags(file_path)";
                cmd.ExecuteNonQuery();
            }
            Catalog = new(Factory);
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Folder, true);
        }
    }

    [Fact]
    public void ParserUsesAndTermsAndQuotedValues()
    {
        var q = SearchQuery.Parse("合同 2026 ext:pdf,PNG path:\"My Projects\" \"final version\"");
        Assert.Equal(new[] { "合同", "2026", "FINAL VERSION" }, q.NameTerms);
        Assert.Equal(new[] { "PDF", "PNG" }, q.Extensions);
        Assert.Equal(new[] { "MY PROJECTS" }, q.PathTerms);
    }

    [Theory]
    [InlineData("ext:")]
    [InlineData("path:")]
    [InlineData("  ")]
    public void IncompleteOperatorsDoNotSearchEverything(string input) => Assert.True(SearchQuery.Parse(input).IsEmpty);

    [Theory]
    [InlineData("合同", null)]
    [InlineData("ht", null)]
    [InlineData("a", null)]
    [InlineData("😀中", null)]
    public void ShortUnicodeTermsUseLiteralFallback(string input, string? expected) =>
        Assert.Equal(expected, SearchQuery.Parse(input).GetTrigramExpression(true));

    [Fact]
    public void NamesFoldButPathIdentityDoesNot()
    {
        Assert.Equal(SearchQuery.Fold("café"), SearchQuery.Fold("cafe\u0301"));
        Assert.False(SearchPath.IsWithin("/scope-two/a", "/scope"));
        Assert.False(SearchPath.IsWithin("/Scope/a", "/scope"));
        Assert.True(SearchPath.IsWithin("/scope/a/b", "/scope"));
        Assert.True(SearchPath.IsWithin("/scope", "/"));
    }

    [Theory]
    [InlineData("/private/tmp/Work/a.pdf", "/private/tmp/Work", "/tmp/Work", "/tmp/Work/a.pdf")]
    [InlineData("/private/tmp/Work", "/private/tmp/Work", "/tmp/Work", "/tmp/Work")]
    [InlineData("/private/tmp/Work2/a", "/private/tmp/Work", "/tmp/Work", "/private/tmp/Work2/a")]
    public void NativeEventPathsMapOnlyTheWatchedAlias(string path, string physical, string logical, string expected) =>
        Assert.Equal(expected, MacSearchChangeSource.MapEventPath(path, physical, logical));

    [Fact]
    public async Task DirectoryAndVisibilityAreAppliedBeforeTheLimit()
    {
        using var db = new Database();
        var records = Enumerable.Range(0, 600).Select(i => Entry($"/outside/a-report-{i}.pdf"))
            .Concat(Enumerable.Range(0, 600).Select(i => Entry($"/scope/.hidden/a-report-{i}.pdf")))
            .Append(Entry("/scope/zzz-report.pdf")).ToArray();
        await db.Catalog.UpsertBatchAsync(records, "one");
        var results = await db.Catalog.SearchAsync("/scope", SearchQuery.Parse("report"), new(), 1, false);
        Assert.Equal("/scope/zzz-report.pdf", Assert.Single(results).Path);
    }

    [Theory]
    [InlineData("100%_final", "100%_final.pdf")]
    [InlineData("a_b", "a_b.pdf")]
    [InlineData("100%", "100%_final.pdf")]
    [InlineData("合同", "项目合同.pdf")]
    [InlineData("目合同", "项目合同.pdf")]
    [InlineData("\"a\\\"b\"", "a\"b.pdf")]
    public async Task LiteralsHaveIdenticalMembershipWithTrigramCandidates(string input, string expected)
    {
        using var db = new Database();
        await db.Catalog.UpsertBatchAsync(new[] { "100%_final.pdf", "100XXfinal.pdf", "a_b.pdf", "axb.pdf", "项目合同.pdf", "a\"b.pdf" }
            .Select(name => Entry("/scope/" + name)).ToArray(), "one");
        var result = await db.Catalog.SearchAsync("/scope", SearchQuery.Parse(input), new(), 50, false);
        Assert.Equal(expected, Assert.Single(result).Name);
    }

    [Fact]
    public async Task PinyinAndMetadataUseOneQueryModel()
    {
        using var db = new Database();
        await db.Catalog.UpsertBatchAsync([
            Entry("/scope/My Projects/合同2026.PDF", "HT2026.PDF"),
            Entry("/scope/Other/合同2026.PDF", "HT2026.PDF"),
            Entry("/scope/My Projects/合同2026.png", "HT2026.PNG")], "one");
        var query = SearchQuery.Parse("ht 2026 ext:pdf path:\"My Projects\"");
        var hit = Assert.Single(await db.Catalog.SearchAsync("/scope", query, new(), 10, false));
        Assert.Equal(".PDF", hit.Extension);
        Assert.True(query.Matches(hit.Name, hit.ParentPath, hit.Extension, hit.InitialsKey, true));
        Assert.Empty(await db.Catalog.SearchAsync("/scope", query, new(UsePinyin: false), 10, false));
    }

    [Fact]
    public async Task CaseDistinctPathsAreBothReturned()
    {
        using var db = new Database();
        await db.Catalog.UpsertBatchAsync([Entry("/scope/Report.pdf"), Entry("/scope/report.pdf")], "one");
        Assert.Equal(2, (await db.Catalog.SearchAsync("/scope", SearchQuery.Parse("report"), new(), 20, false)).Count);
    }

    [Fact]
    public async Task RescanPreservesRowIdsAndFtsMembership()
    {
        using var db = new Database();
        var path = "/scope/report.pdf";
        await db.Catalog.UpsertBatchAsync([Entry(path)], "one");
        long Id()
        {
            using var c = db.Factory.GetConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id FROM search_entries LIMIT 1";
            return (long)cmd.ExecuteScalar()!;
        }
        var id = Id();
        await db.Catalog.UpsertBatchAsync([Entry(path) with { Size = 123 }], "two");
        await db.Catalog.CompleteDirectoryAsync("/scope", "two");
        Assert.Equal(id, Id());
        Assert.Single(await db.Catalog.SearchAsync("/scope", SearchQuery.Parse("report"), new(), 10, false));
    }

    [Fact]
    public async Task SuccessfulEmptyParentRemovesDeletedSubtrees()
    {
        using var db = new Database();
        await db.Catalog.UpsertBatchAsync([Entry("/scope/old", directory: true), Entry("/scope/old/secret.pdf")], "one");
        await db.Catalog.CompleteDirectoryAsync("/scope", "two");
        Assert.Empty(await db.Catalog.SearchAsync("/scope", SearchQuery.Parse("secret"), new(), 10, false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacingDirectoryWithFileOrLinkPrunesItsOldChildren(bool link)
    {
        using var db = new Database();
        await db.Catalog.UpsertBatchAsync([Entry("/scope/item", directory: true), Entry("/scope/item/secret.pdf")], "one");
        await db.Catalog.UpsertBatchAsync([Entry("/scope/item", directory: link, link: link)], "two");
        Assert.Empty(await db.Catalog.SearchAsync("/scope", SearchQuery.Parse("secret"), new(), 10, false));
    }

    [Fact]
    public async Task UnchangedExplicitLinkRootKeepsItsIndexedChildren()
    {
        using var db = new Database();
        await db.Catalog.UpsertBatchAsync([Entry("/scope/link", directory: true, link: true), Entry("/scope/link/report.pdf")], "one");
        await db.Catalog.UpsertBatchAsync([Entry("/scope/link", directory: true, link: true)], "two");
        Assert.Single(await db.Catalog.SearchAsync("/scope", SearchQuery.Parse("report"), new(), 10, false));
    }

    [Fact]
    public async Task InterruptedDirectoryScanDoesNotPruneOldRows()
    {
        using var db = new Database();
        await db.Catalog.UpsertBatchAsync([Entry("/scope/old-report.pdf")], "one");
        await db.Catalog.UpsertBatchAsync([Entry("/scope/new-report.pdf")], "two");
        // No CompleteDirectoryAsync: enumeration failed or was cancelled.
        Assert.Equal(2, (await db.Catalog.SearchAsync("/scope", SearchQuery.Parse("report"), new(), 10, false)).Count);
    }

    [Fact]
    public async Task AiTagsAreScopedBeforeLimitAndDoNotDuplicateNameHits()
    {
        using var db = new Database(ai: true);
        await db.Catalog.UpsertBatchAsync([Entry("/other/photo.png"), Entry("/scope/photo.png"), Entry("/scope/sunset.png")], "one");
        using (var c = db.Factory.GetConnection())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO ai_tags VALUES('/other/photo.png','sunset'),('/scope/photo.png','sunset'),('/scope/sunset.png','sunset')";
            cmd.ExecuteNonQuery();
        }
        var results = await db.Catalog.SearchAsync("/scope", SearchQuery.Parse("sunset ext:png"), new(), 2, true);
        Assert.Equal(2, results.Count);
        Assert.All(results, entry => Assert.StartsWith("/scope/", entry.Path));
        Assert.Equal(2, results.Select(entry => entry.Path).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task NameAndAiTermsCanBeCombined()
    {
        using var db = new Database(ai: true);
        await db.Catalog.UpsertBatchAsync([Entry("/scope/holiday.png"), Entry("/scope/other.png")], "one");
        using (var c = db.Factory.GetConnection())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO ai_tags VALUES('/scope/holiday.png','sunset'),('/scope/other.png','sunset')";
            cmd.ExecuteNonQuery();
        }
        var query = SearchQuery.Parse("holiday sunset");
        Assert.Equal("/scope/holiday.png", Assert.Single(await db.Catalog.SearchAsync("/scope", query, new(), 10, true)).Path);
        Assert.Empty(await db.Catalog.SearchAsync("/scope", query, new(), 10, false));
    }

    [Fact]
    public async Task CancellationIsNotReportedAsEmptyResults()
    {
        using var db = new Database();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            db.Catalog.SearchAsync("/scope", SearchQuery.Parse("report"), new(), 20, false, cts.Token));
    }

    [Fact]
    public async Task CheckpointPersistsUnsignedEventIdsWithoutLoss()
    {
        using var db = new Database();
        await db.Catalog.SaveCheckpointAsync("/scope", ulong.MaxValue - 1, SearchIndexPhase.Ready);
        var reopened = new SearchCatalog(db.Factory);
        Assert.Equal(ulong.MaxValue - 1, await reopened.GetCheckpointAsync("/scope"));
    }

    [Theory]
    [InlineData(1u, "/scope/deep", false)]
    [InlineData(2u, "/scope", false)]
    [InlineData(4u, "/scope", false)]
    [InlineData(8u, "/scope", true)]
    [InlineData(32u, "/scope", true)]
    [InlineData(128u, "/scope", true)]
    public void RecoveryFlagsBecomeRecursiveWork(uint flags, string expected, bool restart)
    {
        var change = MacSearchChangeSource.Decode("/scope", "/scope/deep", flags, 123);
        Assert.True(change.MustRescan);
        Assert.Equal(expected, change.Path);
        Assert.Equal(restart, change.RestartWatcher);
        Assert.Equal(123ul, change.EventId);
    }

    private sealed class NoPinyin : IPinyinInitials { public string GetInitials(string name) => ""; }
    private sealed class Changes : ISearchChangeSource
    {
        public int Starts;
        public Action<SearchChange>? Publish;
        public IDisposable Watch(string root, ulong sinceEventId, Action<SearchChange> onChange, bool fullScanFollows)
        {
            Starts++;
            Publish = onChange;
            return new Registration();
        }
        private sealed class Registration : IDisposable { public void Dispose() { } }
    }

    private static async Task WaitReady(SearchIndexer indexer, string root)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var observation = indexer.Observe(root);
            if (!observation.Status.IsBusy)
            {
                Assert.Equal(SearchIndexPhase.Ready, observation.Status.Phase);
                return;
            }
            await observation.Changed.WaitAsync(deadline.Token);
        }
    }

    [Fact]
    public async Task DeepEventsUpdateIndexWithoutASecondWatchOrPerQueryRescan()
    {
        using var db = new Database();
        var root = Path.Combine(db.Folder, "files");
        var deep = Path.Combine(root, "one", "two");
        Directory.CreateDirectory(deep);
        var changes = new Changes();
        await using var indexer = new SearchIndexer(db.Catalog, new() { DatabasePath = db.PathName }, new NoPinyin(), changes);
        indexer.EnsureRoot(root);
        await WaitReady(indexer, root);
        indexer.EnsureRoot(root);
        Assert.Equal(1, changes.Starts);
        var path = Path.Combine(deep, "new-report.txt");
        await File.WriteAllTextAsync(path, "hello");
        changes.Publish!(new(path, false, false, 10));
        await WaitReady(indexer, root);
        Assert.Single(await db.Catalog.SearchAsync(root, SearchQuery.Parse("report"), new(), 10, false));
        Assert.Equal(10ul, await db.Catalog.GetCheckpointAsync(root));
    }

    [Fact]
    public async Task MissingRootIsUnavailableNotComplete()
    {
        using var db = new Database();
        var root = Path.Combine(db.Folder, "missing");
        await using var indexer = new SearchIndexer(db.Catalog, new() { DatabasePath = db.PathName }, new NoPinyin(), new Changes());
        indexer.EnsureRoot(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (indexer.Observe(root) is var observation && observation.Status.IsBusy)
            await observation.Changed.WaitAsync(deadline.Token);
        Assert.Equal(SearchIndexPhase.Unavailable, indexer.Observe(root).Status.Phase);
        Assert.Equal(0ul, await db.Catalog.GetCheckpointAsync(root));
    }

    [Fact]
    public void MacPinyinProducesCommonInitials()
    {
        if (!OperatingSystem.IsMacOS()) return; // The repository's test RID is osx-arm64.
        Assert.Equal("HT2026.PDF", new MacPinyinInitials().GetInitials("合同2026.pdf"));
    }

    private sealed class UncooperativeSearch : ISearchService
    {
        public TaskCompletionSource<bool> OldStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseOld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<FileSystemEntry> SearchAsync(string directory, string pattern, int maxResults = 500,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (pattern == "old") { OldStarted.TrySetResult(true); await ReleaseOld.Task; }
            yield return new FileSystemEntry { Name = pattern, FullPath = "/scope/" + pattern };
        }
    }

    [Fact]
    public async Task OldQueryCannotOverwriteNewResultsEvenIfProviderIgnoresCancellation()
    {
        var service = new UncooperativeSearch();
        var vm = new SearchViewModel(service);
        IReadOnlyList<FileSystemEntry> visible = [];
        string status = "";
        var old = vm.SearchAsync("old", "/scope", "/scope", items => visible = items, text => status = text);
        await service.OldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.SearchAsync("new", "/scope", "/scope", items => visible = items, text => status = text);
        service.ReleaseOld.TrySetResult(true);
        await old;
        Assert.Equal("new", Assert.Single(visible).Name);
        Assert.Contains("new", status);
        Assert.DoesNotContain("old", status);
    }
}
