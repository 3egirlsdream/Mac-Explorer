using MacExplorer.Indexing;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Search;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MacExplorer.Tests;

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
        public int Calls;
        public string GetInitials(string name)
        {
            Interlocked.Increment(ref Calls);
            if (FailEntry) throw new IOException("simulated entry metadata failure");
            return "";
        }
    }

    private sealed class Changes : ISearchChangeSource
    {
        public bool FailFirst;
        public bool Block;
        public int Starts, Disposals;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new(false);
        public IDisposable Watch(string root, ulong sinceEventId, Action<SearchChange> onChange, bool fullScanFollows)
        {
            var attempt = Interlocked.Increment(ref Starts);
            Entered.TrySetResult();
            if (FailFirst && attempt == 1) throw new IOException("watcher temporarily unavailable");
            if (Block && !Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test did not release watcher");
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

    [Fact]
    public async Task FailedWatcherRetriesAfterCooldownAndReconcilesMissedChanges()
    {
        using var db = new Database();
        var clock = new Clock();
        var changes = new Changes { FailFirst = true };
        await using var indexer = new SearchIndexer(db.Catalog, new() { DatabasePath = db.PathName }, new Pinyin(), changes, clock);
        indexer.EnsureRoot(db.Root);
        Assert.Equal(SearchIndexPhase.Partial, (await WaitSettled(indexer, db.Root)).Phase);
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

    [Fact]
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

    [Fact]
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

    [Fact]
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
