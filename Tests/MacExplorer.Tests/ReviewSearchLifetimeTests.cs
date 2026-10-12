using System.Text.Json;
using MacExplorer.Indexing;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Search;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Integration")]
[Trait("Area", "Search")]
public sealed class ReviewSearchLifetimeTests
{
    private sealed class Database : IDisposable
    {
        public string Folder { get; } = Directory.CreateTempSubdirectory("macexplorer-search-review-").FullName;
        public string Root { get; }
        public string PathName => Path.Combine(Folder, "index.db");
        public SearchCatalog Catalog { get; }
        public Database()
        {
            Root = Directory.CreateDirectory(Path.Combine(Folder, "files")).FullName;
            Catalog = new SearchCatalog(new DatabaseConnectionFactory(PathName));
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Folder, true);
        }
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks = DateTimeOffset.UnixEpoch.Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }

    private sealed class Pinyin : IPinyinInitials
    {
        public bool FailEntry;
        public readonly HashSet<string> FailedNames = [];
        public int Calls;
        public string GetInitials(string name)
        {
            Interlocked.Increment(ref Calls);
            if (FailEntry || FailedNames.Contains(name)) throw new IOException("simulated entry metadata failure");
            return "";
        }
    }

    private sealed class Changes : ISearchChangeSource
    {
        public bool FailFirst;
        public bool Block;
        public int Starts, Disposals;
        private Action<SearchChange>? _onChange;
        private ulong _eventId;
        public void Emit(string path, bool directory = false)
            => _onChange!(new SearchChange(path, directory, false, ++_eventId));
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new(false);
        public IDisposable Watch(string root, ulong sinceEventId, Action<SearchChange> onChange, bool fullScanFollows)
        {
            var attempt = Interlocked.Increment(ref Starts);
            Entered.TrySetResult();
            if (FailFirst && attempt == 1) throw new IOException("watcher temporarily unavailable");
            if (Block && !Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test did not release watcher");
            _onChange = onChange;
            return new Registration(this);
        }
        private sealed class Registration(Changes owner) : IDisposable
        {
            public void Dispose() => Interlocked.Increment(ref owner.Disposals);
        }
    }

    private static async Task<SearchIndexStatus> WaitSettled(SearchIndexer indexer, string root)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var observation = indexer.Observe(root);
            if (!observation.Status.IsBusy) return observation.Status;
            await observation.Changed.WaitAsync(deadline.Token);
        }
    }

    [Fact(Explicit = true)]
    public async Task FailedWatcherRetriesAfterCooldownAndReconcilesMissedChanges()
    {
        using var db = new Database();
        var clock = new Clock();
        var changes = new Changes { FailFirst = true };
        await using var indexer = new SearchIndexer(db.Catalog, new() { DatabasePath = db.PathName }, new Pinyin(), changes, clock);
        indexer.EnsureRoot(db.Root);
        var failedWatcher = await WaitSettled(indexer, db.Root);
        Assert.Equal(SearchIndexPhase.Partial, failedWatcher.Phase);
        Assert.Equal(0, failedWatcher.FailedDirectories);
        Assert.Contains("变更监听不可用", failedWatcher.Description);
        var missed = Path.Combine(db.Root, "missed-report.txt");
        await File.WriteAllTextAsync(missed, "new while unwatched");
        indexer.EnsureRoot(db.Root);
        Assert.Equal(SearchIndexPhase.Partial, indexer.Observe(db.Root).Status.Phase);
        Assert.Equal(1, changes.Starts);
        clock.Advance(TimeSpan.FromSeconds(31));
        indexer.EnsureRoot(db.Root);
        Assert.True(indexer.Observe(db.Root).Status.IsBusy);
        Assert.Equal(SearchIndexPhase.Ready, (await WaitSettled(indexer, db.Root)).Phase);
        Assert.Equal(2, changes.Starts);
        var found = await db.Catalog.SearchAsync(db.Root, SearchQuery.Parse("report"), new(), 10, false);
        Assert.Equal(missed, Assert.Single(found).Path);
    }

    private static async Task<JsonElement> ReadDiagnosticsAsync(string path, SearchIndexPhase phase)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, deadline.Token));
                var root = document.RootElement.GetProperty("Roots")[0];
                if (root.GetProperty("Phase").GetString() == phase.ToString()) return root.Clone();
            }
            await Task.Delay(10, deadline.Token);
        }
    }

    [Fact(Explicit = true)]
    public async Task DiagnosticsRecordFailureDetailsAndIgnoreTheirOwnFileChanges()
    {
        using var db = new Database();
        var databasePath = Path.Combine(db.Root, "index.db");
        var catalog = new SearchCatalog(new DatabaseConnectionFactory(databasePath));
        var file = Path.Combine(db.Root, "bad-report.txt");
        await File.WriteAllTextAsync(file, "report");
        var pinyin = new Pinyin();
        pinyin.FailedNames.Add("bad-report.txt");
        var changes = new Changes();
        await using var indexer = new SearchIndexer(catalog, new() { DatabasePath = databasePath }, pinyin, changes);
        indexer.EnsureRoot(db.Root);
        Assert.Equal(SearchIndexPhase.Partial, (await WaitSettled(indexer, db.Root)).Phase);
        var path = Path.Combine(db.Root, "Logs", "search-index-status.json");
        var diagnostic = await ReadDiagnosticsAsync(path, SearchIndexPhase.Partial);
        Assert.Equal(db.Root, diagnostic.GetProperty("Root").GetString());
        Assert.True(diagnostic.GetProperty("WatcherActive").GetBoolean());
        var failure = Assert.Single(diagnostic.GetProperty("Failures").EnumerateArray());
        Assert.Equal(db.Root, failure.GetProperty("Directory").GetString());
        Assert.Equal(file, failure.GetProperty("Entry").GetString());
        Assert.Equal(nameof(IOException), failure.GetProperty("ExceptionType").GetString());
        Assert.Contains("simulated entry metadata failure", failure.GetProperty("Message").GetString());

        changes.Emit(path);
        changes.Emit(Path.GetDirectoryName(path)!, directory: true);
        Assert.False(indexer.Observe(db.Root).Status.IsBusy);
        indexer.Refresh(db.Root);
        Assert.Equal(SearchIndexPhase.Partial, (await WaitSettled(indexer, db.Root)).Phase);
        Assert.Empty(await catalog.SearchAsync(db.Root, SearchQuery.Parse("search-index-status"), new(), 10, false));

        pinyin.FailedNames.Clear();
        changes.Emit(file);
        Assert.Equal(SearchIndexPhase.Ready, (await WaitSettled(indexer, db.Root)).Phase);
        var recovered = await ReadDiagnosticsAsync(path, SearchIndexPhase.Ready);
        Assert.Empty(recovered.GetProperty("Failures").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, recovered.GetProperty("Error").ValueKind);
    }

    [Fact(Explicit = true)]
    public async Task IncrementalRecoveryClearsOnlyResolvedDirectoryErrors()
    {
        using var db = new Database();
        var first = Directory.CreateDirectory(Path.Combine(db.Root, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(db.Root, "second")).FullName;
        var firstFile = Path.Combine(first, "first.txt");
        var secondFile = Path.Combine(second, "second.txt");
        await File.WriteAllTextAsync(firstFile, "first");
        await File.WriteAllTextAsync(secondFile, "second");
        var pinyin = new Pinyin();
        pinyin.FailedNames.UnionWith(["first.txt", "second.txt"]);
        var changes = new Changes();
        await using var indexer = new SearchIndexer(db.Catalog, new() { DatabasePath = db.PathName }, pinyin, changes);
        indexer.EnsureRoot(db.Root);
        Assert.Equal(2, (await WaitSettled(indexer, db.Root)).FailedDirectories);

        changes.Emit(secondFile);
        var repeated = await WaitSettled(indexer, db.Root);
        Assert.Equal(SearchIndexPhase.Partial, repeated.Phase);
        Assert.Equal(2, repeated.FailedDirectories);

        pinyin.FailedNames.Remove("first.txt");
        changes.Emit(firstFile);
        var partial = await WaitSettled(indexer, db.Root);
        Assert.Equal(SearchIndexPhase.Partial, partial.Phase);
        Assert.Equal(1, partial.FailedDirectories);
        Assert.NotNull(partial.Error);

        pinyin.FailedNames.Remove("second.txt");
        changes.Emit(secondFile);
        var recovered = await WaitSettled(indexer, db.Root);
        Assert.Equal(SearchIndexPhase.Ready, recovered.Phase);
        Assert.Equal(0, recovered.FailedDirectories);
        Assert.Null(recovered.Error);
        Assert.Equal(1, changes.Starts);
        Assert.True(await db.Catalog.GetCheckpointAsync(db.Root) > 0);
    }

    [Fact(Explicit = true)]
    public async Task DeletingFailedDirectoryClearsItsOutstandingError()
    {
        using var db = new Database();
        var failed = Directory.CreateDirectory(Path.Combine(db.Root, "failed")).FullName;
        await File.WriteAllTextAsync(Path.Combine(failed, "report.txt"), "report");
        var pinyin = new Pinyin();
        pinyin.FailedNames.Add("report.txt");
        var changes = new Changes();
        await using var indexer = new SearchIndexer(db.Catalog, new() { DatabasePath = db.PathName }, pinyin, changes);
        indexer.EnsureRoot(db.Root);
        Assert.Equal(1, (await WaitSettled(indexer, db.Root)).FailedDirectories);
        Directory.Delete(failed, true);
        changes.Emit(failed, directory: true);
        var recovered = await WaitSettled(indexer, db.Root);
        Assert.Equal(SearchIndexPhase.Ready, recovered.Phase);
        Assert.Equal(0, recovered.FailedDirectories);
        Assert.Null(recovered.Error);
    }

    [Fact(Explicit = true)]
    public async Task PartialCoverageWithWorkingWatcherDoesNotRescanOnEveryQuery()
    {
        using var db = new Database();
        await File.WriteAllTextAsync(Path.Combine(db.Root, "unreadable-report.txt"), "existing");
        var clock = new Clock();
        var changes = new Changes();
        var pinyin = new Pinyin { FailEntry = true };
        await using var indexer = new SearchIndexer(db.Catalog, new() { DatabasePath = db.PathName }, pinyin, changes, clock);
        indexer.EnsureRoot(db.Root);
        Assert.Equal(SearchIndexPhase.Partial, (await WaitSettled(indexer, db.Root)).Phase);
        var calls = pinyin.Calls;
        clock.Advance(TimeSpan.FromMinutes(1));
        for (var i = 0; i < 10; i++) indexer.EnsureRoot(db.Root);
        Assert.Equal(SearchIndexPhase.Partial, indexer.Observe(db.Root).Status.Phase);
        Assert.Equal(calls, pinyin.Calls);
        Assert.Equal(1, changes.Starts);
    }

    [Fact(Explicit = true)]
    public async Task UserTrashIsExcludedFromParentIndexButExplicitScopeRemainsAvailable()
    {
        using var db = new Database();
        var trash = Directory.CreateDirectory(Path.Combine(db.Root, ".Trash")).FullName;
        var trashed = Path.Combine(trash, "trash-report.txt");
        var visible = Path.Combine(db.Root, "visible-report.txt");
        await File.WriteAllTextAsync(trashed, "trash");
        await File.WriteAllTextAsync(visible, "visible");
        await using var indexer = new SearchIndexer(db.Catalog, new() { DatabasePath = db.PathName }, new Pinyin(), new Changes());
        Assert.False(indexer.CanDescend(db.Root, trash));
        indexer.EnsureRoot(db.Root);
        Assert.Equal(SearchIndexPhase.Ready, (await WaitSettled(indexer, db.Root)).Phase);
        var parentResults = await db.Catalog.SearchAsync(db.Root, SearchQuery.Parse("report"), new(HideDotFiles: false, HideDotFolders: false), 10, false);
        Assert.Equal(visible, Assert.Single(parentResults).Path);

        Assert.True(indexer.CanDescend(trash, trash));
        indexer.EnsureRoot(trash);
        Assert.Equal(SearchIndexPhase.Ready, (await WaitSettled(indexer, trash)).Phase);
        var explicitResults = await db.Catalog.SearchAsync(trash, SearchQuery.Parse("report"), new(), 10, false);
        Assert.Equal(trashed, Assert.Single(explicitResults).Path);
    }

    [Fact(Explicit = true)]
    public async Task PrivateMoveStagingIsNotIndexedEvenWithHiddenFilesEnabled()
    {
        using var db = new Database();
        var staging = Directory.CreateDirectory(Path.Combine(db.Root, ".MacExplorer-move-test.fkfinder-tmp")).FullName;
        await File.WriteAllTextAsync(Path.Combine(staging, "private-report.txt"), "incomplete operation");
        var visible = Path.Combine(db.Root, "public-report.txt");
        await File.WriteAllTextAsync(visible, "complete");
        await using var indexer = new SearchIndexer(db.Catalog, new() { DatabasePath = db.PathName }, new Pinyin(), new Changes());
        Assert.False(indexer.CanDescend(db.Root, staging));
        indexer.EnsureRoot(db.Root);
        Assert.Equal(SearchIndexPhase.Ready, (await WaitSettled(indexer, db.Root)).Phase);
        var found = await db.Catalog.SearchAsync(db.Root, SearchQuery.Parse("report"),
            new(HideSystemFiles: false, HideDotFiles: false, HideDotFolders: false), 10, false);
        Assert.Equal(visible, Assert.Single(found).Path);
    }

    [Fact(Explicit = true)]
    public async Task ConcurrentDisposeCallsJoinTheSameWorkerAndDisposeWatchOnce()
    {
        using var db = new Database();
        var changes = new Changes { Block = true };
        var indexer = new SearchIndexer(db.Catalog, new() { DatabasePath = db.PathName }, new Pinyin(), changes);
        Task? first = null;
        Task? second = null;
        try
        {
            indexer.EnsureRoot(db.Root);
            await changes.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            first = indexer.DisposeAsync().AsTask();
            second = indexer.DisposeAsync().AsTask();
            Assert.Same(first, second);
            Assert.False(first.IsCompleted);
        }
        finally
        {
            changes.Release.Set();
            await indexer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            changes.Release.Dispose();
        }
        Assert.NotNull(first);
        Assert.NotNull(second);
        await Task.WhenAll(first!, second!);
        Assert.Equal(1, changes.Disposals);
        Assert.Throws<ObjectDisposedException>(() => indexer.EnsureRoot(db.Root));
    }

}
