using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using SkiaSharp;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Memory")]
public sealed class MemoryBoundsTests : FileListViewModelTestBase
{
    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 90);
        return encoded.ToArray();
    }

    [Theory]
    [InlineData(1, 256, 256, 1, 256)]
    [InlineData(256, 1, 256, 256, 1)]
    [InlineData(16, 4096, 256, 1, 256)]
    [InlineData(4096, 16, 256, 256, 1)]
    [InlineData(8, 4, 256, 8, 4)]
    public void RasterDecodeFitsBothEdgesWithoutUpscaling(int width, int height, int edge, int expectedWidth, int expectedHeight)
    {
        using var decoded = BoundedImageDecoder.DecodeSkia(Png(width, height), edge);
        Assert.NotNull(decoded);
        Assert.Equal(expectedWidth, decoded.Width);
        Assert.Equal(expectedHeight, decoded.Height);
        Assert.Equal(SKColors.CornflowerBlue, decoded.GetPixel(0, 0));
    }

    [Fact]
    public void OversizedPngIsRejectedUsingHeaderBeforePixelAllocation()
    {
        var bytes = Png(1, 1);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), 12000);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), 12000);
        uint crc = 0xffffffff;
        foreach (var b in bytes.AsSpan(12, 17))
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(29, 4), ~crc);
        using var stream = new SKMemoryStream(bytes);
        using var codec = SKCodec.Create(stream);
        Assert.NotNull(codec);
        Assert.Equal(12000, codec.Info.Width);
        Assert.Null(BoundedImageDecoder.DecodeSkia(bytes, 1800));
        using var valid = BoundedImageDecoder.DecodeSkia(Png(4, 8), 256);
        Assert.NotNull(valid); // Rejection releases the shared decode slot.
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => BoundedImageDecoder.DecodeSkia(bytes, 256, cts.Token));
    }

    [AvaloniaFact]
    public async Task HomeAndFileListKeepThinThumbnailsSmall()
    {
        var bytes = Png(1, 256);
        var file = new FileSystemEntry { FullPath = "/tmp/memory-fixture.png", Name = "fixture.png" };
        Task<ThumbnailResult?> Provider(FileSystemEntry _, int __, CancellationToken ___) => Task.FromResult<ThumbnailResult?>(new(bytes, "fixture"));
        using var cache = new HomeThumbnailCache();
        using var lease = await cache.AcquireAsync(file, 256, Provider, default);
        Assert.NotNull(lease.Bitmap);
        Assert.Equal(1, lease.Bitmap.PixelSize.Width);
        Assert.InRange(lease.Bitmap.PixelSize.Height, 1, 256);
        var images = new FastFileListImages { ThumbnailProvider = Provider };
        try
        {
            images.UpdateVisible([file], 256);
            await WaitForPreviewAsync(() => images.PendingCount == 0 && images.CachedBytes > 0);
            Assert.InRange(images.CachedBytes, 4, 1024);
        }
        finally { images.Clear(); }
    }

    [AvaloniaFact]
    public async Task SearchHistoryIsBoundedAndRetainsQueriesForReload()
    {
        var navigation = new NavigationViewModel(new FakeFileService("/tmp/history"));
        NavigationHistoryEntry? old = null;
        for (var i = 0; i < 300; i++)
        {
            await navigation.NavigateToAsync($"/tmp/history/{i}");
            navigation.PushOrReplaceSearchHistory($"query-{i}", "/tmp/history", false,
                Enumerable.Range(0, 500).Select(n => new FileSystemEntry { FullPath = $"/tmp/history/{i}/{n}", Name = $"file-{n}" }).ToArray());
            if (i == 270) old = navigation.CurrentHistoryEntry;
        }
        Assert.Equal(NavigationViewModel.HistoryLimit, navigation.HistoryCount);
        Assert.InRange(navigation.SearchSnapshotCount, 1, NavigationViewModel.SearchSnapshotLimit);
        Assert.InRange(navigation.SearchSnapshotBytes, 1, NavigationViewModel.SearchSnapshotByteLimit);
        Assert.False(old!.HasSearchResultsSnapshot);
        Assert.Empty(old.SearchResults);
        Assert.Equal("query-270", old.SearchQuery);
        var steps = 0;
        while (navigation.CanGoBack) { await navigation.NavigateBackAsync(); steps++; }
        Assert.Equal(NavigationViewModel.HistoryLimit - 1, steps);
        Assert.True(navigation.CanGoForward);
    }

    [AvaloniaFact]
    public async Task SearchSnapshotBudgetIncludesInlineImageDataAndDoesNotOwnOriginalItem()
    {
        var navigation = new NavigationViewModel(new FakeFileService("/tmp/history"));
        await navigation.NavigateToAsync("/tmp/history");
        var original = new FileSystemEntry { Name = "file", FullPath = "/tmp/history/file" };
        navigation.PushOrReplaceSearchHistory("query", "/tmp/history", false, [original]);
        Assert.NotSame(original, Assert.Single(navigation.CurrentHistoryEntry!.SearchResults));
        original.IconUrl = new string('x', (int)NavigationViewModel.SearchSnapshotByteLimit);
        navigation.UpdateCurrentSearchResults([original]);
        Assert.False(navigation.CurrentHistoryEntry.HasSearchResultsSnapshot);
        Assert.Empty(navigation.CurrentHistoryEntry.SearchResults);
    }

    [AvaloniaFact]
    public async Task ReturningToEvictedSearchRequeriesOriginalScopeWithoutAddingHistory()
    {
        var files = new FakeFileService("/tmp/history");
        var navigation = new NavigationViewModel(files);
        var search = new HistorySearch();
        using var vm = CreateViewModel(files, navigation: navigation, searchService: search);
        await navigation.NavigateToAsync("/tmp/history/old");
        navigation.PushOrReplaceSearchHistory("original-query", "/tmp/history/original-scope", false, []);
        var old = navigation.CurrentHistoryEntry!;
        old.HasSearchResultsSnapshot = false;
        await navigation.NavigateToAsync("/tmp/history/new");
        var count = navigation.HistoryCount;
        await vm.NavigateBackAsync();
        Assert.Equal(("/tmp/history/original-scope", "original-query"), search.Request);
        Assert.Equal(count, navigation.HistoryCount);
        Assert.Same(old, navigation.CurrentHistoryEntry);
        Assert.True(old.HasSearchResultsSnapshot);
        Assert.Equal("returned.txt", Assert.Single(old.SearchResults).Name);
    }

    private sealed class HistorySearch : ISearchService
    {
        public (string Root, string Query)? Request;
        public async IAsyncEnumerable<FileSystemEntry> SearchAsync(string directory, string query, int maxResults = 500,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Request = (directory, query);
            await Task.CompletedTask;
            yield return new FileSystemEntry { FullPath = directory + "/returned.txt", Name = "returned.txt" };
        }
    }

    [AvaloniaFact]
    public async Task ReloadingAnEvictedSearchDoesNotSuppressTheNextNavigation()
    {
        var files = new FakeFileService("/tmp/history");
        var navigation = new NavigationViewModel(files);
        var search = new WaitingHistorySearch();
        using var vm = CreateViewModel(files, navigation: navigation, searchService: search);
        await navigation.NavigateToAsync("/tmp/history/old");
        navigation.PushOrReplaceSearchHistory("query", "/tmp/history", false, []);
        navigation.CurrentHistoryEntry!.HasSearchResultsSnapshot = false;
        await navigation.NavigateToAsync("/tmp/history/new");
        var reload = vm.NavigateBackAsync();
        try
        {
            await search.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await navigation.NavigateToAsync("/tmp/history/next");
            Assert.Equal("/tmp/history/next", navigation.CurrentHistoryEntry!.Path);
            Assert.False(navigation.CurrentHistoryEntry.IsSearchMode);
            Assert.True(navigation.CanGoBack);
            Assert.False(navigation.CanGoForward);
        }
        finally { search.Finish.TrySetResult(); await reload; }
    }

    private sealed class WaitingHistorySearch : ISearchService
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<FileSystemEntry> SearchAsync(string directory, string query, int maxResults = 500,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Finish.Task.WaitAsync(cancellationToken);
            yield break;
        }
    }

    [Fact]
    public async Task FailedRetryStateHasCountByteAndAgeBoundsAndReleasesCaptures()
    {
        var manager = new BackgroundTaskManager();
        var references = new List<WeakReference>();
        for (var i = 0; i < BackgroundTaskManager.FailedRetryLimit + 4; i++) references.Add(await FailWithPayload(manager, 1024 * 1024));
        Assert.Equal(BackgroundTaskManager.FailedRetryLimit, manager.Tasks.Count(t => t.CanRetry));
        Assert.All(manager.Tasks.Take(4), task => Assert.Equal(BackgroundTaskState.Failed, task.State));
        manager.TrimFailedTasks(DateTime.UtcNow.Add(BackgroundTaskManager.FailedRetryLifetime).AddSeconds(1));
        Assert.All(manager.Tasks, task => Assert.False(task.CanRetry));
        for (var i = 0; i < 10 && references.Any(reference => reference.IsAlive); i++)
        { await Task.Delay(1); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(references, reference => Assert.False(reference.IsAlive));
        await FailWithPayload(manager, (int)BackgroundTaskManager.FailedRetryByteLimit + 1);
        Assert.False(manager.Tasks.Last().CanRetry);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> FailWithPayload(BackgroundTaskManager manager, int bytes)
    {
        var payload = new byte[bytes];
        var reference = new WeakReference(payload);
        await Assert.ThrowsAsync<IOException>(() => manager.RunAsync<int>(new FileTaskRequest(BackgroundTaskKind.Copy, "fixture", []), context =>
        {
            context.Remember("payload", () => payload);
            context.ReportItem("source", "destination", FileTaskItemState.Failed);
            throw new IOException("offline");
        }));
        return reference;
    }

    [Fact]
    public void FailureSummariesAndLegacyRetryClosuresAreBounded()
    {
        var manager = new BackgroundTaskManager();
        for (var i = 0; i < 150; i++)
        {
            var task = manager.AddTask("fixture", BackgroundTaskKind.Copy, retryAction: () => Task.CompletedTask);
            manager.FailTask(task.Id, "offline");
        }
        Assert.Equal(BackgroundTaskManager.CompletedTaskLimit + BackgroundTaskManager.FailedRetryLimit, manager.Tasks.Count);
        Assert.All(manager.Tasks.Where(t => !t.CanRetry), task => Assert.Null(task.RetryAction));
    }

    [Fact]
    public async Task CompactSummaryPreservesBatchNameAndReleasesLegacyCallbacks()
    {
        var manager = new BackgroundTaskManager();
        await manager.RunAsync(new FileTaskRequest(BackgroundTaskKind.Copy, "batch", ["/tmp/one", "/tmp/two"]), _ => Task.FromResult(1));
        var batch = Assert.Single(manager.Tasks);
        Assert.Empty(batch.Request!.Sources);
        Assert.Equal(LocalizationText.Get("{0} 项", 2), batch.Request.DisplayName);
        var failed = manager.AddTask("legacy", onCompleted: () => Task.CompletedTask);
        manager.FailTask(failed.Id, "offline");
        Assert.Null(failed.OnCompleted);
    }

    [Fact]
    public void GitCacheIsBoundedAndExpiresWithoutRevisitingRepositories()
    {
        using var service = new GitStatusService();
        for (var i = 0; i < 100; i++) service.CacheStatus(new GitRepoStatus { RepoRoot = $"/tmp/repo-{i}", FileStatuses = new() { ["file"] = GitFileStatus.Modified } });
        var state = ((IMemoryDiagnosticSource)service).GetMemoryState();
        Assert.Equal(GitStatusService.CacheEntryLimit, state.Entries);
        Assert.InRange(state.Bytes, 1, GitStatusService.CacheByteLimit);
        service.TrimMemory(DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(0, ((IMemoryDiagnosticSource)service).GetMemoryState().Entries);
    }

    [Fact]
    public async Task RepeatedThumbnailInvalidationDoesNotRetainOldEvictionKeys()
    {
        var root = Path.Combine(Path.GetTempPath(), "fk-thumbnail-invalidate-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new MacExplorer.Platforms.MacCatalyst.Services.MacThumbnailService(root, 1024 * 1024, .8);
            var source = Path.Combine(root, "fixture.png");
            var bytes = Png(2, 2);
            var generations = 0;
            for (var i = 0; i < 500; i++)
            {
                var result = await service.GetOrCreateThumbnailAsync("image-v3:" + source + ":0:64", async (path, token) =>
                {
                    generations++;
                    await File.WriteAllBytesAsync(path, bytes, token);
                    return bytes;
                }, false, default);
                Assert.NotNull(result);
                service.EvictFromCache(source);
            }
            Assert.Equal(1, generations); // Disk reuse still works after memory invalidation.
            Assert.Equal(0, ((IMemoryDiagnosticSource)service).GetMemoryState().Entries);
            Assert.Equal(0, service.CacheOrderCount);
            service.ClearCache();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void DiagnosticFilesRotateAndContainValidJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "fk-memory-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var log = new RotatingMemoryLog(root, maxBytes: 512, maxFiles: 3))
                for (var i = 0; i < 100; i++) log.Write(JsonSerializer.Serialize(new { index = i, value = new string('x', 100) }));
            var files = Directory.GetFiles(root);
            Assert.Equal(3, files.Length);
            Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 512));
            foreach (var line in files.SelectMany(File.ReadAllLines)) using (JsonDocument.Parse(line)) { }
            Assert.Contains(files.SelectMany(File.ReadAllLines), line => line.Contains("\"index\":99"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void DiagnosticSessionRecordsProcessAndUiCountersWithoutRequiringAGc()
    {
        var root = Path.Combine(Path.GetTempPath(), "fk-memory-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var diagnostics = MemoryDiagnostics.Start(root))
            {
                diagnostics.Sample();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }
            var records = Directory.GetFiles(root).SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var sample = Assert.Single(records.Where(record => record.RootElement.GetProperty("name").GetString() == "memory.sample"));
                Assert.True(sample.RootElement.GetProperty("data").GetProperty("rssBytes").GetInt64() > 0);
                if (OperatingSystem.IsMacOS()) Assert.True(sample.RootElement.GetProperty("data").GetProperty("physicalFootprintBytes").GetInt64() > 0);
                Assert.Contains(records, record => record.RootElement.GetProperty("name").GetString() == "memory.ui");
                Assert.Contains(records, record => record.RootElement.GetProperty("name").GetString() == "session.stop");
            }
            finally { foreach (var record in records) record.Dispose(); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
