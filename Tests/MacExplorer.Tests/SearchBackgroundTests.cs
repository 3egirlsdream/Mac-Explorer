using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MacExplorer.Indexing;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Search;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "Search")]
public sealed class SearchBackgroundTests : FileListViewModelTestBase
{
    [AvaloniaFact(Explicit = true)]
    public async Task SearchWatchAndEnumerationLeaveTheUiResponsive()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateDirectory(Path.Combine(RuntimePaths.TestRoot ?? Path.GetTempPath(),
            "search-background-" + Guid.NewGuid().ToString("N")));
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(directory.FullName, "files")).FullName;
            await File.WriteAllTextAsync(Path.Combine(root, "report.txt"), "report", ct);
            var catalog = new SearchCatalog(new DatabaseConnectionFactory(Path.Combine(directory.FullName, "index.db")));
            using var changes = new BlockingChanges();
            var pinyin = new BackgroundPinyin();
            await using var indexer = new SearchIndexer(catalog,
                new() { DatabasePath = Path.Combine(directory.FullName, "index.db") }, pinyin, changes);
            var search = new MacSearchService(catalog, indexer, new FakeFileService(root), pinyin);
            await using var results = search.SearchGlobalAsync(root, "report", cancellationToken: ct).GetAsyncEnumerator(ct);
            var firstResult = results.MoveNextAsync().AsTask();
            try
            {
                await changes.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                Assert.True(Dispatcher.UIThread.CheckAccess());
                Assert.False(changes.WasOnUiThread);
                Assert.False(firstResult.IsCompleted);
                var input = new TextBox { Text = "still responsive" };
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("still responsive", input.Text);
            }
            finally { changes.Release.Set(); }
            Assert.True(await firstResult.WaitAsync(TimeSpan.FromSeconds(5), ct));
            Assert.Equal("report.txt", results.Current.Name);
            Assert.True(pinyin.Calls > 0);
            Assert.False(pinyin.WasOnUiThread);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    private sealed class BackgroundPinyin : IPinyinInitials
    {
        public int Calls;
        public bool WasOnUiThread;
        public string GetInitials(string name)
        {
            Calls++;
            WasOnUiThread |= Dispatcher.UIThread.CheckAccess();
            return "";
        }
    }

    private sealed class BlockingChanges : ISearchChangeSource, IDisposable
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new();
        public bool WasOnUiThread;
        public IDisposable Watch(string root, ulong sinceEventId, Action<SearchChange> onChange, bool fullScanFollows)
        {
            WasOnUiThread = Dispatcher.UIThread.CheckAccess();
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Search watcher was not released.");
            return new Registration();
        }
        public void Dispose() => Release.Dispose();
        private sealed class Registration : IDisposable { public void Dispose() { } }
    }
}
