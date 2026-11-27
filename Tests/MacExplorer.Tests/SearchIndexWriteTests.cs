using System.Text.Json;
using MacExplorer.Indexing;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Search;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Area", "Search")]
public sealed class SearchIndexWriteTests : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(RuntimePaths.TestRoot ?? Path.GetTempPath(),
        "search-write-tests-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly CancellationToken _cancellation = TestContext.Current.CancellationToken;
    private readonly DirectoryAccess _access;
    private readonly IDisposable _accessScope;
    private string Root => Path.Combine(_folder, "files");
    private string Database => Path.Combine(_folder, "index.db");
    private SearchCatalog Catalog() => new(new DatabaseConnectionFactory(Database));
    private static SearchEntry Entry(string path, long size = 1, bool directory = false) =>
        new(path, Path.GetFileName(path), Path.GetDirectoryName(path)!, Path.GetExtension(path),
            SearchQuery.Fold(Path.GetFileName(path)), "", SearchQuery.Fold(Path.GetDirectoryName(path)!),
            size, directory, false, 0, 0);

    public SearchIndexWriteTests()
    {
        Directory.CreateDirectory(Root);
        _access = new DirectoryAccess(Path.Combine(_folder, "access.json"), true, new Bookmarks(), [_folder]);
        _accessScope = DirectoryAccess.UseForTests(_access);
    }
    public void Dispose()
    {
        _accessScope.Dispose(); _access.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(_folder, true);
    }

    private sealed class Bookmarks : IBookmarkAccess
    {
        public string Create(string path, bool explicitScope) => path;
        public (IntPtr, string?, string?, bool) Open(string bookmark) => (new IntPtr(1), bookmark, null, false);
        public void Close(IntPtr scope) { }
        public string RealPath(string path) => Path.GetFullPath(path);
    }

    private long Scalar(string sql)
    {
        using var connection = new DatabaseConnectionFactory(Database).GetConnection();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task RepeatedReconciliationDoesNotUpdateRowsOrRewriteScanMarkers()
    {
        var catalog = Catalog(); var entry = Entry(Path.Combine(Root, "report.txt"));
        await catalog.ApplyEntriesAsync([entry], _cancellation);
        var id = Scalar("SELECT id FROM search_entries");
        using (var connection = new DatabaseConnectionFactory(Database).GetConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE search_entries SET scan_id='legacy';
                CREATE TABLE observed_updates(count INTEGER); INSERT INTO observed_updates VALUES(0);
                CREATE TRIGGER observe_entry_update AFTER UPDATE ON search_entries BEGIN UPDATE observed_updates SET count=count+1; END;
                """;
            command.ExecuteNonQuery();
        }
        for (var i = 0; i < 3; i++)
        {
            await using var scan = await catalog.BeginDirectoryScanAsync(Root, _cancellation);
            Assert.Equal(default, await scan.RecordAsync([entry], _cancellation));
            Assert.Equal(default, await scan.CompleteAsync(_cancellation));
        }
        Assert.Equal(0, Scalar("SELECT count FROM observed_updates"));
        Assert.Equal(id, Scalar("SELECT id FROM search_entries"));
        Assert.Equal(1, Scalar("SELECT count(*) FROM search_entries WHERE scan_id='legacy'"));
        Assert.Single(await catalog.SearchAsync(Root, SearchQuery.Parse("report"), new(), 10, false, _cancellation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Kind", "Unit")]
    public async Task SuccessfulSnapshotDeletesOnlyAbsentSubtrees(bool spill)
    {
        var catalog = Catalog(); if (spill) catalog.ScanMemoryLimitBytes = 1;
        var keep = Entry(Path.Combine(Root, "keep.txt"));
        var old = Entry(Path.Combine(Root, "old"), directory: true);
        var other = Entry(Path.Combine(Root, "old-other", "keep.txt"));
        await catalog.ApplyEntriesAsync([keep, old, Entry(Path.Combine(old.Path, "deleted.txt")), other], _cancellation);
        await using (var scan = await catalog.BeginDirectoryScanAsync(Root, _cancellation))
        {
            Assert.Equal(spill, scan.Spilled);
            Assert.Equal(default, await scan.RecordAsync([keep], _cancellation));
            Assert.Equal(2, (await scan.CompleteAsync(_cancellation)).Deleted);
        }
        Assert.Equal(2, Scalar("SELECT count(*) FROM search_entries"));
        Assert.Empty(Directory.Exists(Path.Combine(_folder, "Logs", "scan-scratch"))
            ? Directory.GetFiles(Path.Combine(_folder, "Logs", "scan-scratch")) : []);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Kind", "Unit")]
    public async Task InterruptedOrCancelledSnapshotRetainsOldRows(bool cancel)
    {
        var catalog = Catalog(); catalog.ScanMemoryLimitBytes = 1;
        await catalog.ApplyEntriesAsync([Entry(Path.Combine(Root, "old.txt"))], _cancellation);
        await using (var scan = await catalog.BeginDirectoryScanAsync(Root, _cancellation))
        {
            await scan.RecordAsync([Entry(Path.Combine(Root, "new.txt"))], _cancellation);
            if (cancel)
            {
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.CompleteAsync(cancellation.Token));
            }
        }
        Assert.Equal(2, Scalar("SELECT count(*) FROM search_entries"));
        await catalog.ApplyEntriesAsync([Entry(Path.Combine(Root, "after.txt"))], _cancellation);
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task ScratchFailureAllowsUpdatesButPreventsDeletion()
    {
        var catalog = Catalog(); catalog.ScanMemoryLimitBytes = 1;
        catalog.ScanScratchDirectory = Path.Combine(_folder, "not-a-directory");
        File.WriteAllText(catalog.ScanScratchDirectory, "occupied");
        await catalog.ApplyEntriesAsync([Entry(Path.Combine(Root, "old.txt"))], _cancellation);
        await using (var scan = await catalog.BeginDirectoryScanAsync(Root, _cancellation))
        {
            Assert.Equal(1, (await scan.RecordAsync([Entry(Path.Combine(Root, "new.txt"))], _cancellation)).Inserted);
            await Assert.ThrowsAsync<IOException>(() => scan.CompleteAsync(_cancellation));
        }
        Assert.Equal(2, Scalar("SELECT count(*) FROM search_entries"));
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task UnchangedCheckpointAndPhaseDoNotWrite()
    {
        var catalog = Catalog(); await catalog.SaveCheckpointAsync(Root, 7, SearchIndexPhase.Ready, _cancellation);
        using (var connection = new DatabaseConnectionFactory(Database).GetConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE search_roots SET updated_ticks=1"; command.ExecuteNonQuery();
        }
        await catalog.SaveCheckpointAsync(Root, 7, SearchIndexPhase.Ready, _cancellation);
        Assert.Equal(1, Scalar("SELECT updated_ticks FROM search_roots"));
        await catalog.SaveCheckpointAsync(Root, 8, SearchIndexPhase.Ready, _cancellation);
        Assert.Equal(8ul, await catalog.GetCheckpointAsync(Root, _cancellation));
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task SharedParentSearchKeyCanChangeWithoutRewritingFileRows()
    {
        var catalog = Catalog(); var entry = Entry(Path.Combine(Root, "report.txt"));
        await catalog.ApplyEntriesAsync([entry], _cancellation);
        var id = Scalar("SELECT id FROM search_entries");
        Assert.Equal(default, await catalog.ApplyEntriesAsync([entry with { ParentKey = "UPDATED_PARENT" }], _cancellation));
        Assert.Single(await catalog.SearchAsync(Root, SearchQuery.Parse("path:UPDATED_PARENT"), new(), 10, false, _cancellation));
        Assert.Equal(id, Scalar("SELECT id FROM search_entries"));
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task DirectoryLeaseSerializesIndexWritesWithoutKeepingASqlWriteTransactionOpen()
    {
        var catalog = Catalog();
        var scan = await catalog.BeginDirectoryScanAsync(Root, _cancellation);
        try
        {
            var waiting = catalog.ApplyEntriesAsync([Entry(Path.Combine(Root, "later.txt"))], _cancellation);
            Assert.False(waiting.IsCompleted);
            using (var connection = new DatabaseConnectionFactory(Database).GetConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA busy_timeout=0; CREATE TABLE independent_write(value INTEGER)";
                command.ExecuteNonQuery();
            }
            Assert.Empty(await catalog.SearchAsync(Root, SearchQuery.Parse("later"), new(), 10, false, _cancellation));
            await scan.DisposeAsync();
            Assert.Equal(1, (await waiting).Inserted);
        }
        finally { await scan.DisposeAsync(); }
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task QueryAndIndexerHonorRestrictedFolderGrants()
    {
        var catalog = Catalog(); var outside = Path.Combine(_folder, "outside"); Directory.CreateDirectory(outside);
        await catalog.ApplyEntriesAsync([Entry(Path.Combine(Root, "report.txt")), Entry(Path.Combine(outside, "secret-report.txt"))], _cancellation);
        using var access = new DirectoryAccess(Path.Combine(_folder, "restricted.json"), true, new Bookmarks(), []);
        access.RememberSelection(Root);
        using var scope = DirectoryAccess.UseForTests(access);
        Assert.Single(await catalog.SearchAsync(Root, SearchQuery.Parse("report"), new(), 10, false, _cancellation));
        Assert.Empty(await catalog.SearchAsync(outside, SearchQuery.Parse("report"), new(), 10, false, _cancellation));
        await using var indexer = new SearchIndexer(catalog, new() { DatabasePath = Database }, new Pinyin(), new Changes());
        Assert.Throws<UnauthorizedAccessException>(() => indexer.EnsureRoot(outside));
    }

    [Fact(Explicit = true)]
    [Trait("Kind", "Integration")]
    public async Task PackageCandidatesRespectSystemClassificationAndExplicitRoots()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Requires macOS");
        var folder = Directory.CreateDirectory(Path.Combine(Root, "NapCat.Framework")).FullName;
        var app = Directory.CreateDirectory(Path.Combine(Root, "Example.app")).FullName;
        File.WriteAllText(Path.Combine(folder, "probe-inside.txt"), "");
        File.WriteAllText(Path.Combine(app, "probe-private.txt"), "");
        var catalog = Catalog();
        await using var indexer = new SearchIndexer(catalog, new() { DatabasePath = Database }, new Pinyin(), new Changes());
        Assert.True(indexer.CanDescend(Root, Path.Combine(folder, "child")));
        Assert.False(indexer.CanDescend(Root, Path.Combine(app, "Contents")));
        Assert.True(indexer.CanDescend(app, Path.Combine(app, "Contents")));
        indexer.EnsureRoot(Root);
        await Ready(indexer, Root);
        var search = new MacSearchService(catalog, indexer, new MacFileService(), new Pinyin());
        var found = new List<string>();
        await foreach (var entry in search.SearchAsync(Root, "probe-", cancellationToken: _cancellation))
            found.Add(entry.Name);
        Assert.Contains("probe-inside.txt", found);
        Assert.DoesNotContain("probe-private.txt", found);
        var frameworks = new List<Models.FileSystemEntry>();
        await foreach (var entry in search.SearchAsync(Root, "NapCat", cancellationToken: _cancellation))
            frameworks.Add(entry);
        var result = Assert.Single(frameworks);
        Assert.Equal("NapCat.Framework", result.DisplayName);
        Assert.Equal("folder", result.DetailsIconSource.IconKey);
        found.Clear();
        await foreach (var entry in search.SearchAsync(app, "probe-private", cancellationToken: _cancellation))
            found.Add(entry.Name);
        Assert.Equal("probe-private.txt", Assert.Single(found));
    }

    [Fact(Explicit = true)]
    [Trait("Kind", "Stress")]
    public async Task LargeDirectoryUsesTheRealMemoryLimitAndCleansScratchAfterAbortAndCompletion()
    {
        var catalog = Catalog();
        SearchEntry Item(int index) => Entry(Path.Combine(Root, new string('x', 180) + $"-{index:00000}.txt"));
        for (var offset = 0; offset < 50000; offset += 250)
            await catalog.ApplyEntriesAsync(Enumerable.Range(offset, 250).Select(Item).ToArray(), _cancellation);
        var scratch = Path.Combine(_folder, "Logs", "scan-scratch");
        await using (var interrupted = await catalog.BeginDirectoryScanAsync(Root, _cancellation))
        {
            Assert.True(interrupted.Spilled);
            Assert.Single(Directory.GetFiles(scratch));
            await interrupted.RecordAsync(Enumerable.Range(0, 250).Select(Item).ToArray(), _cancellation);
        }
        Assert.Equal(50000, Scalar("SELECT count(*) FROM search_entries"));
        Assert.Empty(Directory.GetFiles(scratch));
        await using (var scan = await catalog.BeginDirectoryScanAsync(Root, _cancellation))
        {
            Assert.True(scan.Spilled);
            for (var offset = 0; offset < 49995; offset += 250)
                Assert.Equal(default, await scan.RecordAsync(Enumerable.Range(offset, Math.Min(250, 49995 - offset)).Select(Item).ToArray(), _cancellation));
            Assert.Equal(5, (await scan.CompleteAsync(_cancellation)).Deleted);
        }
        Assert.Equal(49995, Scalar("SELECT count(*) FROM search_entries"));
        Assert.Empty(Directory.GetFiles(scratch));
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public void PendingPressureKeepsLocalScopeAndDirectoryDoesNotCoverDeepEntries()
    {
        var pending = new SearchPendingWork("/scope", 4, 1024 * 1024);
        pending.Add(new("/scope/local", SearchWorkKind.Directory));
        for (var i = 0; i < 8; i++) pending.Add(new($"/scope/local/deep-{i}/file", SearchWorkKind.Entry));
        var work = pending.Take();
        Assert.DoesNotContain(work, item => item.Path == "/scope" && item.Kind == SearchWorkKind.Recursive);
        for (var i = 0; i < 8; i++) Assert.Contains(work, item => item.Covers(new($"/scope/local/deep-{i}/file", SearchWorkKind.Entry)));
        Assert.True(work.Length <= 4);
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public void BytePressureAndRecoveryPromotionRemainBounded()
    {
        var pending = new SearchPendingWork("/scope", 4096, 512);
        for (var i = 0; i < 20; i++) pending.Add(new($"/scope/{i}/file", SearchWorkKind.Entry));
        Assert.True(pending.Bytes <= 512);
        pending.Add(new("/scope", SearchWorkKind.Recursive, SearchWorkReason.Refresh));
        Assert.True(Assert.Single(pending.Take()).Urgent);
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public void FlatFilePressureNeedsOnlyAShallowDirectoryReconciliation()
    {
        var pending = new SearchPendingWork("/scope", 2, 1024 * 1024);
        for (var i = 0; i < 3; i++) pending.Add(new($"/scope/file-{i}", SearchWorkKind.Entry));
        var work = Assert.Single(pending.Take());
        Assert.Equal(SearchWorkKind.Directory, work.Kind);
        Assert.False(work.Covers(new("/scope/child/file", SearchWorkKind.Entry)));
    }

    [Theory]
    [InlineData(0x11000u, SearchChangeKind.Modified)]
    [InlineData(0x10100u, SearchChangeKind.Created)]
    [InlineData(0x10200u, SearchChangeKind.Removed)]
    [InlineData(0x10800u, SearchChangeKind.Renamed)]
    [InlineData(0x10900u, SearchChangeKind.Created | SearchChangeKind.Renamed)]
    [Trait("Kind", "Unit")]
    public void NativeFlagsPreserveEventKind(uint flags, SearchChangeKind kind) =>
        Assert.Equal(kind, MacSearchChangeSource.Decode("/scope", "/scope/file", flags, 1).Kind);

    private sealed class Changes : IDiagnosticSearchChangeSource
    {
        internal Action<SearchChange> Publish = null!;
        internal Action<string, uint, ulong>? OnRecovery;
        internal ulong Id;
        internal int WatchStarts;
        internal ulong LastSinceEventId;
        internal bool LastFullScanFollows;
        internal ulong NextInitialEventId;
        internal bool NextRequiresFullScan;
        public IDisposable Watch(string root, ulong sinceEventId, Action<SearchChange> onChange, bool fullScanFollows)
            => Watch(root, sinceEventId, onChange, fullScanFollows, null);
        public IDisposable Watch(string root, ulong sinceEventId, Action<SearchChange> onChange, bool fullScanFollows,
            Action<string, uint, ulong>? onRecoverySignal)
        {
            WatchStarts++;
            LastSinceEventId = sinceEventId;
            LastFullScanFollows = fullScanFollows;
            Publish = onChange;
            OnRecovery = onRecoverySignal;
            var handle = new Registration(NextInitialEventId, NextRequiresFullScan);
            NextRequiresFullScan = false;
            return handle;
        }
        internal void Emit(string path, bool directory = false, bool rescan = false, SearchChangeKind kind = SearchChangeKind.Unknown, bool restart = false) =>
            Publish(new(path, directory, rescan, ++Id, RestartWatcher: restart, Kind: kind));
        internal void EmitRawRecovery(string path, uint flags, bool restart = false)
        {
            var id = ++Id;
            OnRecovery?.Invoke(path, flags, id);
            Publish(new(path, true, true, id, RestartWatcher: restart));
        }
        private sealed class Registration(ulong initialEventId, bool requiresFullScan) : ISearchWatchHandle
        {
            public ulong InitialEventId { get; } = initialEventId;
            public bool RequiresFullScan { get; } = requiresFullScan;
            public void Dispose() { }
        }
    }
    private sealed class Pinyin : IPinyinInitials
    {
        internal int Calls;
        internal Action<string>? Inspect;
        public string GetInitials(string name) { Interlocked.Increment(ref Calls); Inspect?.Invoke(name); return ""; }
    }
    private sealed class Clock : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new();
        internal readonly TaskCompletionSource MergeScheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        public override long GetTimestamp() => _clock.GetTimestamp();
        public override long TimestampFrequency => _clock.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _clock.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromMilliseconds(200)) MergeScheduled.TrySetResult();
            return timer;
        }
        internal void Advance(TimeSpan elapsed) => _clock.Advance(elapsed);
    }
    private async Task Eventually(Func<Task<bool>> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (!await condition()) await Task.Delay(10, deadline.Token);
    }
    private async Task Ready(SearchIndexer indexer, string root)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var observation = indexer.Observe(root);
            if (!observation.Status.IsBusy) { Assert.Equal(SearchIndexPhase.Ready, observation.Status.Phase); return; }
            await observation.Changed.WaitAsync(deadline.Token);
        }
    }

    [Fact(Explicit = true)]
    [Trait("Kind", "Integration")]
    public async Task KnownFileModificationChecksAndUpdatesOnlyOneEntry()
    {
        for (var i = 0; i < 300; i++) File.WriteAllText(Path.Combine(Root, $"file-{i}.txt"), "old");
        var changes = new Changes(); var catalog = Catalog();
        await using var indexer = new SearchIndexer(catalog, new() { DatabasePath = Database }, new Pinyin(), changes);
        indexer.EnsureRoot(Root); await Ready(indexer, Root);
        var before = indexer.GetStatistics();
        File.WriteAllText(Path.Combine(Root, "file-0.txt"), "modified content");
        changes.Emit(Path.Combine(Root, "file-0.txt"), kind: SearchChangeKind.Modified);
        await Ready(indexer, Root);
        var after = indexer.GetStatistics();
        Assert.Equal(1, after.CheckedEntries - before.CheckedEntries);
        Assert.Equal(1, after.UpdatedEntries - before.UpdatedEntries);
        Assert.Equal(before.DirectoryScans, after.DirectoryScans);
        Assert.Equal(changes.Id, await catalog.GetCheckpointAsync(Root, _cancellation));
    }

    [Theory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Kind", "Integration")]
    public async Task UrgentRequestsWakeDeferredOverflowAndPreciseUpdatesContinue(bool journal)
    {
        var clock = new Clock(); var changes = new Changes(); var catalog = Catalog();
        var configuration = new IndexConfiguration
        { DatabasePath = Database, PendingLimit = 2, EventMergeDelay = TimeSpan.Zero, DiagnosticWriteInterval = TimeSpan.Zero };
        await using var indexer = new SearchIndexer(catalog, configuration, new Pinyin(), changes, clock);
        indexer.EnsureRoot(Root); await Ready(indexer, Root);
        configuration.EventMergeDelay = TimeSpan.FromMilliseconds(200);
        changes.Emit(Path.Combine(Root, "directory-0", "event.txt"));
        await clock.MergeScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), _cancellation);
        for (var i = 1; i < 3; i++) changes.Emit(Path.Combine(Root, $"directory-{i}", "event.txt"));
        Assert.Equal(1, indexer.GetStatistics().DeferredRoots);
        var added = Path.Combine(Root, "live.txt"); File.WriteAllText(added, "new");
        changes.Emit(added, kind: SearchChangeKind.Modified);
        configuration.EventMergeDelay = TimeSpan.Zero; clock.Advance(TimeSpan.FromMilliseconds(200));
        await Eventually(async () => (await catalog.SearchAsync(Root, SearchQuery.Parse("live"), new(), 10, false, _cancellation)).Count == 1);
        Assert.Equal(0ul, await catalog.GetCheckpointAsync(Root, _cancellation));
        if (journal) changes.Emit(Root, true, true); else indexer.Refresh(Root);
        await Ready(indexer, Root);
        Assert.Equal(0, indexer.GetStatistics().DeferredRoots);
        Assert.Equal(changes.Id, await catalog.GetCheckpointAsync(Root, _cancellation));
        var scans = indexer.GetStatistics().FullScans;
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(scans, indexer.GetStatistics().FullScans);
    }

    [Fact(Explicit = true)]
    [Trait("Kind", "Integration")]
    public async Task DeferredOverflowRunsAfterVirtualCooldown()
    {
        var clock = new Clock(); var changes = new Changes();
        var configuration = new IndexConfiguration
        { DatabasePath = Database, PendingLimit = 2, EventMergeDelay = TimeSpan.Zero, DiagnosticWriteInterval = TimeSpan.Zero };
        await using var indexer = new SearchIndexer(Catalog(), configuration, new Pinyin(), changes, clock);
        indexer.EnsureRoot(Root); await Ready(indexer, Root);
        configuration.EventMergeDelay = TimeSpan.FromMilliseconds(200);
        changes.Emit(Path.Combine(Root, "directory-0", "event.txt"));
        await clock.MergeScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), _cancellation);
        for (var i = 1; i < 3; i++) changes.Emit(Path.Combine(Root, $"directory-{i}", "event.txt"));
        Assert.Equal(1, indexer.GetStatistics().DeferredRoots);
        configuration.EventMergeDelay = TimeSpan.Zero;
        clock.Advance(TimeSpan.FromMinutes(10));
        await Ready(indexer, Root);
        Assert.Equal(1, indexer.GetStatistics().FullScanReasons["OverflowFallback"]);
    }

    [Fact(Explicit = true)]
    [Trait("Kind", "Integration")]
    public async Task OverflowCollectedDuringInitialScanWaitsAfterThatScanCompletes()
    {
        File.WriteAllText(Path.Combine(Root, "trigger.txt"), "entry");
        var clock = new FakeTimeProvider(); var changes = new Changes();
        var pinyin = new Pinyin { Inspect = _ =>
        {
            for (var i = 0; i < 3; i++) changes.Emit(Path.Combine(Root, $"directory-{i}", "event.txt"));
        } };
        var catalog = Catalog();
        await using var indexer = new SearchIndexer(catalog, new()
        { DatabasePath = Database, PendingLimit = 2, EventMergeDelay = TimeSpan.Zero }, pinyin, changes, clock);
        indexer.EnsureRoot(Root);
        await Eventually(() => Task.FromResult(indexer.GetStatistics().DeferredRoots == 1));
        Assert.Equal(1, indexer.GetStatistics().FullScans);
        Assert.Equal(0ul, await catalog.GetCheckpointAsync(Root, _cancellation));
        pinyin.Inspect = null;
        indexer.Refresh(Root); await Ready(indexer, Root);
        Assert.Equal(2, indexer.GetStatistics().FullScans);
        Assert.Equal(changes.Id, await catalog.GetCheckpointAsync(Root, _cancellation));
    }

    [Fact(Explicit = true)]
    [Trait("Kind", "Integration")]
    public async Task RecoveryReceivedDuringScanIsNotClearedByItsCompletion()
    {
        File.WriteAllText(Path.Combine(Root, "trigger.txt"), "entry");
        var changes = new Changes(); var pinyin = new Pinyin();
        await using var indexer = new SearchIndexer(Catalog(), new() { DatabasePath = Database }, pinyin, changes);
        indexer.EnsureRoot(Root); await Ready(indexer, Root);
        indexer.EntryInspected = _ => { indexer.EntryInspected = null; changes.Emit(Root, true, true); };
        indexer.Refresh(Root); await Ready(indexer, Root);
        Assert.Equal(3, indexer.GetStatistics().FullScans);
        Assert.Equal(1, indexer.GetStatistics().FullScanReasons["JournalRecovery"]);
    }

    [Fact(Explicit = true)]
    [Trait("Kind", "Integration")]
    public async Task NewRootRecoveryCoalescesIntoSingleTrailingPassWithoutRestartingMidTraversal()
    {
        var a = Directory.CreateDirectory(Path.Combine(Root, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(Root, "b")).FullName;
        File.WriteAllText(Path.Combine(a, "a.txt"), "a"); File.WriteAllText(Path.Combine(b, "b.txt"), "b");
        var changes = new Changes(); var pinyin = new Pinyin();
        await using var indexer = new SearchIndexer(Catalog(), new() { DatabasePath = Database }, pinyin, changes);
        indexer.EnsureRoot(Root); await Ready(indexer, Root);
        long firstVisitB = 0; var requested = false;
        indexer.EntryInspected = name =>
        {
            if (name == "a.txt" && !requested)
            {
                requested = true;
                for (var i = 0; i < 3; i++) changes.Emit(Root, true, true);
            }
            if (name == "b.txt" && firstVisitB == 0) firstVisitB = indexer.GetStatistics().FullScans;
        };
        indexer.Refresh(Root); await Ready(indexer, Root);
        Assert.Equal(2, firstVisitB);
        var stats = indexer.GetStatistics();
        Assert.Equal(3, stats.FullScans);
        Assert.Equal(1, stats.FullScanReasons["JournalRecovery"]);
        Assert.True(stats.CoalescedRecoveries >= 3);
    }

    [Fact(Explicit = true)]
    [Trait("Kind", "Integration")]
    public async Task CoalescedDirectoryCreateAndRemoveStillDiscoversItsFinalContents()
    {
        var changes = new Changes(); var catalog = Catalog();
        await using var indexer = new SearchIndexer(catalog, new() { DatabasePath = Database }, new Pinyin(), changes);
        indexer.EnsureRoot(Root); await Ready(indexer, Root);
        var directory = Directory.CreateDirectory(Path.Combine(Root, "recreated")).FullName;
        File.WriteAllText(Path.Combine(directory, "child-report.txt"), "child");
        changes.Emit(directory, true, kind: SearchChangeKind.Created | SearchChangeKind.Removed);
        await Ready(indexer, Root);
        Assert.Single(await catalog.SearchAsync(Root, SearchQuery.Parse("child-report"), new(), 10, false, _cancellation));
    }

    [Fact(Explicit = true)]
    [Trait("Kind", "Integration")]
    public async Task YoungerRecoveryCannotAcknowledgeOverflowDeferredDuringARootScan()
    {
        var a = Directory.CreateDirectory(Path.Combine(Root, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(Root, "b")).FullName;
        File.WriteAllText(Path.Combine(a, "a.txt"), "a"); File.WriteAllText(Path.Combine(b, "b.txt"), "b");
        var clock = new FakeTimeProvider(); var changes = new Changes(); var pinyin = new Pinyin(); var catalog = Catalog();
        await using var indexer = new SearchIndexer(catalog, new()
        { DatabasePath = Database, PendingLimit = 2, EventMergeDelay = TimeSpan.Zero }, pinyin, changes, clock);
        indexer.EnsureRoot(Root); await Ready(indexer, Root);
        var boundary = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var emitted = false;
        indexer.EntryInspected = name =>
        {
            if (name == "a.txt" && !emitted)
            {
                emitted = true;
                for (var i = 0; i < 3; i++)
                {
                    var directory = Directory.CreateDirectory(Path.Combine(Root, $"lost-{i}")).FullName;
                    var path = Path.Combine(directory, "lost-report.txt"); File.WriteAllText(path, "new"); changes.Emit(path);
                }
                changes.Emit(a, true, true);
            }
            if (name == "b.txt") boundary.TrySetResult(indexer.Observe(Root).Changed);
        };
        indexer.Refresh(Root);
        var finished = await boundary.Task.WaitAsync(TimeSpan.FromSeconds(5), _cancellation);
        await finished.WaitAsync(TimeSpan.FromSeconds(5), _cancellation);
        Assert.Equal(1, indexer.GetStatistics().DeferredRoots);
        Assert.Equal(0ul, await catalog.GetCheckpointAsync(Root, _cancellation));
        Assert.Empty(await catalog.SearchAsync(Root, SearchQuery.Parse("lost-report"), new(), 10, false, _cancellation));
        indexer.EntryInspected = null;
        indexer.Refresh(Root); await Ready(indexer, Root);
        Assert.Equal(3, (await catalog.SearchAsync(Root, SearchQuery.Parse("lost-report"), new(), 10, false, _cancellation)).Count);
        Assert.Equal(changes.Id, await catalog.GetCheckpointAsync(Root, _cancellation));
    }

    [Fact(Explicit = true)]
    [Trait("Kind", "Integration")]
    public async Task ThousandDeepEventsDoNotBecomeAWholeRootScan()
    {
        var changes = new Changes();
        await using var indexer = new SearchIndexer(Catalog(), new() { DatabasePath = Database }, new Pinyin(), changes);
        indexer.EnsureRoot(Root); await Ready(indexer, Root);
        for (var i = 0; i < 1000; i++)
        {
            var directory = Directory.CreateDirectory(Path.Combine(Root, "branch", i.ToString())).FullName;
            File.WriteAllText(Path.Combine(directory, "report.txt"), "new");
        }
        for (var i = 0; i < 1000; i++) changes.Emit(Path.Combine(Root, "branch", i.ToString(), "report.txt"));
        await Ready(indexer, Root);
        Assert.Equal(1, indexer.GetStatistics().FullScans);
        Assert.True(indexer.GetStatistics().PendingPeak > 512);
        Assert.Equal(1000, Scalar("SELECT count(*) FROM search_entries WHERE name='report.txt'"));
    }

    [Fact(Explicit = true)]
    [Trait("Kind", "Integration")]
    public async Task PriorityGenerationDoesNotAcknowledgeOlderWorkAndOtherRootsMakeProgress()
    {
        var first = Directory.CreateDirectory(Path.Combine(Root, "a")).FullName;
        var last = Directory.CreateDirectory(Path.Combine(Root, "b")).FullName;
        File.WriteAllText(Path.Combine(first, "a.txt"), "a"); File.WriteAllText(Path.Combine(last, "b.txt"), "b");
        var other = Directory.CreateDirectory(Path.Combine(_folder, "other")).FullName;
        File.WriteAllText(Path.Combine(other, "other.txt"), "other");
        using var releaseA = new ManualResetEventSlim(); using var releaseB = new ManualResetEventSlim();
        var enteredA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seenA = 0;
        var pinyin = new Pinyin { Inspect = name =>
        {
            if (name == "a.txt" && Interlocked.Increment(ref seenA) == 1)
            { enteredA.TrySetResult(); if (!releaseA.Wait(TimeSpan.FromSeconds(10), _cancellation)) throw new TimeoutException(); }
            if (name == "b.txt")
            { enteredB.TrySetResult(); if (!releaseB.Wait(TimeSpan.FromSeconds(10), _cancellation)) throw new TimeoutException(); }
        } };
        var changes = new Changes(); var catalog = Catalog();
        await using var indexer = new SearchIndexer(catalog, new() { DatabasePath = Database }, pinyin, changes);
        try
        {
            indexer.EnsureRoot(Root);
            await enteredA.Task.WaitAsync(TimeSpan.FromSeconds(5), _cancellation);
            changes.Emit(first, true, true);
            indexer.EnsureRoot(other);
            releaseA.Set();
            await enteredB.Task.WaitAsync(TimeSpan.FromSeconds(5), _cancellation);
            Assert.Equal(0ul, await catalog.GetCheckpointAsync(Root, _cancellation));
            Assert.Equal(SearchIndexPhase.Ready, indexer.Observe(other).Status.Phase);
            releaseB.Set(); await Ready(indexer, Root);
            Assert.Equal(1ul, await catalog.GetCheckpointAsync(Root, _cancellation));
        }
        finally { releaseA.Set(); releaseB.Set(); }
    }

    [Fact(Explicit = true)]
    [Trait("Kind", "Integration")]
    public async Task NativeEventsReconcileAtomicSaveRenameAndDirectoryDeletion()
    {
        var path = Path.Combine(Root, "native-report.txt"); File.WriteAllText(path, "original");
        var catalog = Catalog();
        await using var indexer = new SearchIndexer(catalog, new() { DatabasePath = Database }, new Pinyin(), new MacSearchChangeSource());
        indexer.EnsureRoot(Root); await Ready(indexer, Root);
        var temporary = path + ".saving"; File.WriteAllText(temporary, "atomic replacement"); File.Move(temporary, path, true);
        await Eventually(async () => (await catalog.SearchAsync(Root, SearchQuery.Parse("native-report"), new(), 10, false, _cancellation)).Any(entry => entry.Path == path && entry.Size == 18));
        var renamed = Path.Combine(Root, "renamed-report.txt"); File.Move(path, renamed);
        await Eventually(async () =>
        {
            var entries = await catalog.SearchAsync(Root, SearchQuery.Parse("report"), new(), 10, false, _cancellation);
            return entries.Count == 1 && entries[0].Path == renamed;
        });
        var directory = Directory.CreateDirectory(Path.Combine(Root, "nested")).FullName;
        File.WriteAllText(Path.Combine(directory, "child-report.txt"), "child");
        await Eventually(async () => (await catalog.SearchAsync(Root, SearchQuery.Parse("child-report"), new(), 10, false, _cancellation)).Count == 1);
        var moved = Path.Combine(Root, "moved"); Directory.Move(directory, moved);
        await Eventually(async () =>
        {
            var entries = await catalog.SearchAsync(Root, SearchQuery.Parse("child-report"), new(), 10, false, _cancellation);
            return entries.Count == 1 && entries[0].ParentPath == moved;
        });
        Directory.Delete(moved, true);
        await Eventually(async () => (await catalog.SearchAsync(Root, SearchQuery.Parse("child-report"), new(), 10, false, _cancellation)).Count == 0);
        Assert.True(indexer.GetStatistics().ReceivedEvents > 0);
    }

    [Fact(Explicit = true)]
    [Trait("Kind", "Integration")]
    public async Task NativeIndexArtifactsDoNotStartASelfSustainingScanLoop()
    {
        var ct = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(Root, "report.txt"), "fixture");
        await using var indexer = new SearchIndexer(Catalog(), new()
        { DatabasePath = Database, DiagnosticWriteInterval = TimeSpan.Zero }, new Pinyin(), new MacSearchChangeSource());
        indexer.EnsureRoot(_folder); await Ready(indexer, _folder);
        await Task.Delay(1200, ct);
        await Ready(indexer, _folder);
        var before = indexer.GetStatistics();
        await Task.Delay(1200, ct);
        var after = indexer.GetStatistics();
        Assert.Equal(before.DirectoryScans, after.DirectoryScans);
        Assert.Equal(before.DiagnosticsWrites, after.DiagnosticsWrites);
        Assert.Equal(1, after.FullScans);
    }

    [Fact(Explicit = true)]
    [Trait("Kind", "Integration")]
    public async Task PointRecoveryDoesNotHideOtherMetadataFailuresInTheSameDirectory()
    {
        File.WriteAllText(Path.Combine(Root, "a.txt"), "a"); File.WriteAllText(Path.Combine(Root, "b.txt"), "b");
        var failing = new HashSet<string>(["a.txt", "b.txt"]);
        var pinyin = new Pinyin { Inspect = name => { if (failing.Contains(name)) throw new IOException("metadata failed"); } };
        var changes = new Changes();
        await using var indexer = new SearchIndexer(Catalog(), new() { DatabasePath = Database }, pinyin, changes);
        indexer.EnsureRoot(Root);
        await Eventually(() => Task.FromResult(indexer.Observe(Root).Status.Phase == SearchIndexPhase.Partial));
        failing.Remove("b.txt"); changes.Emit(Path.Combine(Root, "b.txt"), kind: SearchChangeKind.Modified);
        await Eventually(() => Task.FromResult(indexer.Observe(Root).Status.Phase == SearchIndexPhase.Partial));
        Assert.Equal(0ul, await Catalog().GetCheckpointAsync(Root, _cancellation));
        failing.Clear(); changes.Emit(Path.Combine(Root, "a.txt"), kind: SearchChangeKind.Modified);
        await Ready(indexer, Root);
        Assert.Equal(changes.Id, await Catalog().GetCheckpointAsync(Root, _cancellation));
    }

    [Fact(Explicit = true)]
    [Trait("Kind", "Integration")]
    public async Task DiagnosticsCoalesceFlushFinalStateAndIgnoreTheirOwnEvents()
    {
        var clock = new FakeTimeProvider(); var changes = new Changes();
        var indexer = new SearchIndexer(Catalog(), new()
        { DatabasePath = Database, EventMergeDelay = TimeSpan.Zero }, new Pinyin(), changes, clock);
        try
        {
            indexer.EnsureRoot(Root); await Ready(indexer, Root);
            await Eventually(() => Task.FromResult(indexer.GetStatistics().DiagnosticsWrites == 1));
            var path = Path.Combine(_folder, "Logs", "search-index-status.json");
            changes.Emit(path); changes.Emit(path + ".tmp");
            for (var i = 0; i < 3; i++) { indexer.Refresh(Root); await Ready(indexer, Root); }
            Assert.Equal(1, indexer.GetStatistics().DiagnosticsWrites);
            clock.Advance(TimeSpan.FromSeconds(10));
            await Eventually(() => Task.FromResult(indexer.GetStatistics().DiagnosticsWrites == 2));
            indexer.Refresh(Root); await Ready(indexer, Root);
            await indexer.DisposeAsync();
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("Ready", document.RootElement.GetProperty("Roots")[0].GetProperty("Phase").GetString());
            Assert.Equal(3, indexer.GetStatistics().DiagnosticsWrites);
            var writes = indexer.GetStatistics().DiagnosticsWrites;
            clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(writes, indexer.GetStatistics().DiagnosticsWrites);
        }
        finally { await indexer.DisposeAsync(); }
    }

    [Theory]
    [InlineData(0ul, 500ul, true, 500ul, 500ul, false)]
    [InlineData(42ul, 500ul, true, 500ul, 500ul, false)]
    [InlineData(42ul, 500ul, false, 42ul, 42ul, false)]
    [InlineData(500ul, 500ul, false, 500ul, 500ul, false)]
    [InlineData(0ul, 500ul, false, 500ul, 500ul, true)]
    [InlineData(900ul, 500ul, false, 500ul, 500ul, true)]
    [Trait("Kind", "Unit")]
    public void ResolveStartEventIdHandlesValidWrappedAndFullScanCases(
        ulong sinceEventId, ulong currentEventId, bool fullScanFollows,
        ulong expectedSince, ulong expectedBaseline, bool expectedRequiresFullScan)
    {
        var resolved = MacSearchChangeSource.ResolveStartEventId(sinceEventId, currentEventId, fullScanFollows);
        Assert.Equal(expectedSince, resolved.Since);
        Assert.Equal(expectedBaseline, resolved.Baseline);
        Assert.Equal(expectedRequiresFullScan, resolved.RequiresFullScan);
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task ValidCheckpointResumesIncrementallyWithoutStartupFullScan()
    {
        File.WriteAllText(Path.Combine(Root, "existing.txt"), "initial");
        var catalog = Catalog();
        var firstChanges = new Changes { NextInitialEventId = 42 };
        await using (var first = new SearchIndexer(catalog, new() { DatabasePath = Database, EventMergeDelay = TimeSpan.Zero }, new Pinyin(), firstChanges))
        {
            first.EnsureRoot(Root);
            await Ready(first, Root);
            Assert.Equal(1, first.GetStatistics().FullScans);
            Assert.Equal(42ul, await catalog.GetCheckpointAsync(Root, _cancellation));
        }

        var resumedChanges = new Changes { Id = 42 };
        var resumedPinyin = new Pinyin();
        await using var resumed = new SearchIndexer(catalog, new() { DatabasePath = Database, EventMergeDelay = TimeSpan.Zero }, resumedPinyin, resumedChanges);
        resumed.EnsureRoot(Root);
        await Ready(resumed, Root);
        var stats = resumed.GetStatistics();
        Assert.Equal(0, stats.FullScans);
        Assert.Equal(0, stats.DirectoryScans);
        Assert.Equal(1, stats.IncrementalStarts);
        Assert.Equal(0, resumedPinyin.Calls);
        Assert.Equal(42ul, resumedChanges.LastSinceEventId);
        Assert.False(resumedChanges.LastFullScanFollows);

        var added = Path.Combine(Root, "replayed-missed.txt");
        File.WriteAllText(added, "created while closed");
        resumedChanges.Emit(added, kind: SearchChangeKind.Created);
        await Ready(resumed, Root);
        Assert.Equal(0, resumed.GetStatistics().FullScans);
        Assert.Equal(43ul, await catalog.GetCheckpointAsync(Root, _cancellation));
        Assert.Single(await catalog.SearchAsync(Root, SearchQuery.Parse("replayed-missed"), new(), 10, false, _cancellation));
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task InvalidOrIncompleteCheckpointFallsBackToStartupFullScan()
    {
        File.WriteAllText(Path.Combine(Root, "seed.txt"), "seed");
        var catalog = Catalog();

        // Case 1: Persisted Ready + checkpoint > 0, but catalog has no entries under root.
        await catalog.SaveCheckpointAsync(Root, 10, SearchIndexPhase.Ready, _cancellation);
        var emptyCatalogChanges = new Changes { NextInitialEventId = 20 };
        await using (var indexer = new SearchIndexer(catalog, new() { DatabasePath = Database, EventMergeDelay = TimeSpan.Zero }, new Pinyin(), emptyCatalogChanges))
        {
            indexer.EnsureRoot(Root);
            await Ready(indexer, Root);
            Assert.Equal(1, indexer.GetStatistics().FullScans);
            Assert.True(emptyCatalogChanges.LastFullScanFollows);
            Assert.Equal(20ul, await catalog.GetCheckpointAsync(Root, _cancellation));
        }

        // Case 2: Persisted phase is Partial -> must fall back to full scan.
        await catalog.SaveCheckpointAsync(Root, 20, SearchIndexPhase.Partial, _cancellation);
        var partialChanges = new Changes { NextInitialEventId = 30 };
        await using (var indexer = new SearchIndexer(catalog, new() { DatabasePath = Database, EventMergeDelay = TimeSpan.Zero }, new Pinyin(), partialChanges))
        {
            indexer.EnsureRoot(Root);
            await Ready(indexer, Root);
            Assert.Equal(1, indexer.GetStatistics().FullScans);
            Assert.True(partialChanges.LastFullScanFollows);
            Assert.Equal(30ul, await catalog.GetCheckpointAsync(Root, _cancellation));
        }

        // Case 3: Persisted checkpoint is wrapped/invalid (persisted 500 > new baseline 100) -> falls back and resets checkpoint to 100.
        await catalog.SaveCheckpointAsync(Root, 500, SearchIndexPhase.Ready, _cancellation);
        var wrappedChanges = new Changes { NextInitialEventId = 100, NextRequiresFullScan = true };
        await using (var indexer = new SearchIndexer(catalog, new() { DatabasePath = Database, EventMergeDelay = TimeSpan.Zero }, new Pinyin(), wrappedChanges))
        {
            indexer.EnsureRoot(Root);
            await Ready(indexer, Root);
            Assert.Equal(1, indexer.GetStatistics().FullScans);
            Assert.Equal(0, indexer.GetStatistics().IncrementalStarts);
            Assert.Equal(100ul, await catalog.GetCheckpointAsync(Root, _cancellation));
        }
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task RepeatedJournalRecoverySignalsCoalesceAndRespectCooldownWithDiagnostics()
    {
        File.WriteAllText(Path.Combine(Root, "base.txt"), "base");
        var clock = new FakeTimeProvider();
        var changes = new Changes();
        var catalog = Catalog();
        var configuration = new IndexConfiguration
        {
            DatabasePath = Database,
            EventMergeDelay = TimeSpan.Zero,
            DiagnosticWriteInterval = TimeSpan.Zero
        };
        var indexer = new SearchIndexer(catalog, configuration, new Pinyin(), changes, clock);
        try
        {
            indexer.EnsureRoot(Root);
            await Ready(indexer, Root);
            Assert.Equal(1, indexer.GetStatistics().FullScans);

            changes.EmitRawRecovery(Root, 0x40u, restart: true); // Mount
            await Ready(indexer, Root);
            Assert.Equal(2, indexer.GetStatistics().FullScans);
            Assert.Equal(1, indexer.GetStatistics().FullScanReasons["JournalRecovery"]);
            var firstRecoveryCheckpoint = await catalog.GetCheckpointAsync(Root, _cancellation);
            Assert.Equal(changes.Id, firstRecoveryCheckpoint);

            // Burst of repeated recovery signals within cooldown must coalesce and defer.
            changes.EmitRawRecovery(Root, 0x80u, restart: true); // Unmount
            changes.EmitRawRecovery(Root, 0x4u);                 // KernelDropped
            changes.EmitRawRecovery(Root, 0x2u);                 // UserDropped
            var duringCooldown = indexer.GetStatistics();
            Assert.Equal(1, duringCooldown.DeferredRoots);
            Assert.Equal(2, duringCooldown.FullScans);
            Assert.True(duringCooldown.CoalescedRecoveries >= 3);

            // Live incremental updates continue working while recovery is deferred, without advancing checkpoint.
            var live = Path.Combine(Root, "live-during-cooldown.txt");
            File.WriteAllText(live, "live");
            changes.Emit(live, kind: SearchChangeKind.Modified);
            await Eventually(async () => (await catalog.SearchAsync(Root, SearchQuery.Parse("live-during-cooldown"), new(), 10, false, _cancellation)).Count == 1);
            Assert.Equal(firstRecoveryCheckpoint, await catalog.GetCheckpointAsync(Root, _cancellation));

            // Cooldown expiry runs ONE reconciled JournalRecovery pass and advances checkpoint.
            clock.Advance(TimeSpan.FromMinutes(10));
            await Ready(indexer, Root);
            var afterCooldown = indexer.GetStatistics();
            Assert.Equal(0, afterCooldown.DeferredRoots);
            Assert.Equal(3, afterCooldown.FullScans);
            Assert.Equal(2, afterCooldown.FullScanReasons["JournalRecovery"]);
            Assert.Equal(changes.Id, await catalog.GetCheckpointAsync(Root, _cancellation));

            await indexer.DisposeAsync();
            var diagPath = Path.Combine(_folder, "Logs", "search-index-status.json");
            using var document = JsonDocument.Parse(File.ReadAllText(diagPath));
            var rootReport = document.RootElement.GetProperty("Roots")[0];
            var recoveries = rootReport.GetProperty("RecentRecoveries").EnumerateArray().ToArray();
            Assert.InRange(recoveries.Length, 4, 8);
            Assert.Contains(recoveries, r => r.GetProperty("RawFlags").GetUInt32() == 0x40u
                && r.GetProperty("Flags").GetString()!.Contains("Mount")
                && r.GetProperty("Scope").GetString() == Root);
            Assert.Contains(recoveries, r => r.GetProperty("RawFlags").GetUInt32() == 0x4u
                && r.GetProperty("Flags").GetString()!.Contains("KernelDropped"));
        }
        finally { await indexer.DisposeAsync(); }
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public void HanFreePinyinFastPathAllocatesZeroBytesAndPreservesHanInitials()
    {
        Assert.False(MacPinyinInitials.ContainsHan("package.json"));
        Assert.False(MacPinyinInitials.ContainsHan("café_résumé-2026.pdf"));
        Assert.False(MacPinyinInitials.ContainsHan("rocket🚀_한글.txt"));
        Assert.True(MacPinyinInitials.ContainsHan("合同2026.pdf"));
        Assert.True(MacPinyinInitials.ContainsHan("extB_\U00020000.txt"));

        var pinyin = new MacPinyinInitials();
        _ = pinyin.GetInitials("warmup.txt");
        var before = GC.GetAllocatedBytesForCurrentThread();
        var anyNonEmpty = false;
        for (var i = 0; i < 100; i++)
        {
            if (pinyin.GetInitials("src_components_Button.tsx").Length != 0)
                anyNonEmpty = true;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(anyNonEmpty);
        Assert.Equal(0L, allocated);

        if (OperatingSystem.IsMacOS())
            Assert.Equal("HT2026.PDF", pinyin.GetInitials("合同2026.pdf"));
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task UnchangedDirectoryReconciliationSkipsPinyinAndHandlesInsertUpdateRenameDelete()
    {
        var keep = Path.Combine(Root, "keep.txt");
        var modify = Path.Combine(Root, "modify.txt");
        var renameOld = Path.Combine(Root, "rename-old.txt");
        var delete = Path.Combine(Root, "delete.txt");
        var dirToFile = Path.Combine(Root, "dir-to-file");
        Directory.CreateDirectory(dirToFile);
        File.WriteAllText(keep, "unchanged");
        File.WriteAllText(modify, "old");
        File.WriteAllText(renameOld, "rename");
        File.WriteAllText(delete, "gone");
        File.WriteAllText(Path.Combine(dirToFile, "child.txt"), "nested");

        var catalog = Catalog();
        var pinyin = new Pinyin();
        var changes = new Changes();
        await using var indexer = new SearchIndexer(catalog, new() { DatabasePath = Database, EventMergeDelay = TimeSpan.Zero }, pinyin, changes);
        indexer.EnsureRoot(Root);
        await Ready(indexer, Root);

        // Unchanged full reconciliation must skip pinyin and perform zero writes.
        pinyin.Calls = 0;
        var beforeUnchanged = indexer.GetStatistics();
        indexer.Refresh(Root);
        await Ready(indexer, Root);
        var afterUnchanged = indexer.GetStatistics();
        Assert.Equal(0, pinyin.Calls);
        Assert.Equal(beforeUnchanged.InsertedEntries, afterUnchanged.InsertedEntries);
        Assert.Equal(beforeUnchanged.UpdatedEntries, afterUnchanged.UpdatedEntries);
        Assert.Equal(beforeUnchanged.DeletedEntries, afterUnchanged.DeletedEntries);

        // Mutate: keep one file untouched, modify one, rename one, delete one, add one, replace dir with file.
        File.WriteAllText(modify, "updated content with a different length");
        var renameNew = Path.Combine(Root, "rename-new.txt");
        File.Move(renameOld, renameNew);
        File.Delete(delete);
        var added = Path.Combine(Root, "added.txt");
        File.WriteAllText(added, "brand new");
        Directory.Delete(dirToFile, true);
        File.WriteAllText(dirToFile, "now a regular file");

        pinyin.Calls = 0;
        indexer.Refresh(Root);
        await Ready(indexer, Root);

        // Only the 4 new/modified entries should have computed pinyin (keep.txt was skipped).
        Assert.Equal(4, pinyin.Calls);
        Assert.Single(await catalog.SearchAsync(Root, SearchQuery.Parse("keep"), new(), 10, false, _cancellation));
        var modifiedEntry = Assert.Single(await catalog.SearchAsync(Root, SearchQuery.Parse("modify"), new(), 10, false, _cancellation));
        Assert.Equal(new FileInfo(modify).Length, modifiedEntry.Size);
        Assert.Single(await catalog.SearchAsync(Root, SearchQuery.Parse("rename-new"), new(), 10, false, _cancellation));
        Assert.Empty(await catalog.SearchAsync(Root, SearchQuery.Parse("rename-old"), new(), 10, false, _cancellation));
        Assert.Empty(await catalog.SearchAsync(Root, SearchQuery.Parse("delete"), new(), 10, false, _cancellation));
        Assert.Single(await catalog.SearchAsync(Root, SearchQuery.Parse("added"), new(), 10, false, _cancellation));
        var replaced = Assert.Single(await catalog.SearchAsync(Root, SearchQuery.Parse("dir-to-file"), new(), 10, false, _cancellation));
        Assert.False(replaced.IsDirectory);
        Assert.Empty(await catalog.SearchAsync(Root, SearchQuery.Parse("child"), new(), 10, false, _cancellation));
    }
}
