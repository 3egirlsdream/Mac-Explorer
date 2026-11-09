using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Memory")]
public sealed class MemoryDiagnosticsTests : FileListViewModelTestBase
{
    [AvaloniaFact]
    public async Task NavigationAndTabDisposalAreCorrelatedWithoutRecordingFileData()
    {
        var root = Path.Combine(Path.GetTempPath(), "fk-memory-navigation-" + Guid.NewGuid().ToString("N"));
        var fixture = Path.Combine(root, "private-fixture-path");
        Directory.CreateDirectory(fixture);
        try
        {
            long listId, navigationId, tabId;
            using (MemoryDiagnostics.Start(Path.Combine(root, "logs")))
            {
                var files = new FakeFileService(root);
                files.Seed(new FileSystemEntry { Name = "private-fixture-content.txt", FullPath = Path.Combine(fixture, "private-fixture-content.txt") });
                var navigation = new NavigationViewModel(files);
                using var viewModel = CreateViewModel(files, navigation: navigation);
                using var tab = new ExplorerTabViewModel(viewModel);
                listId = viewModel.DiagnosticId; navigationId = navigation.DiagnosticId; tabId = tab.DiagnosticId;
                await viewModel.NavigateToAsync(fixture);
                Assert.Single(viewModel.Entries);
                viewModel.GoHome();
            }
            var records = ReadRecords(Path.Combine(root, "logs"));
            var raw = string.Join("\n", records.Select(r => r.GetRawText()));
            Assert.DoesNotContain("private-fixture-path", raw);
            Assert.DoesNotContain("private-fixture-content", raw);
            var directory = Assert.Single(Events(records, "operation.end").Where(d => d.GetProperty("action").GetString() == "DirectoryLoad"));
            Assert.Equal("Completed", directory.GetProperty("outcome").GetString());
            Assert.Equal(1, directory.GetProperty("count").GetInt32());
            Assert.Equal(listId, directory.GetProperty("objectId").GetInt64());
            var lifetimes = Events(records, "lifetime.object").ToArray();
            Assert.Contains(lifetimes, d => d.GetProperty("objectId").GetInt64() == listId && d.GetProperty("relatedId").GetInt64() == navigationId);
            Assert.Contains(lifetimes, d => d.GetProperty("objectId").GetInt64() == tabId && d.GetProperty("relatedId").GetInt64() == listId);
            Assert.Contains(lifetimes, d => d.GetProperty("objectId").GetInt64() == listId && d.GetProperty("state").GetString() == "Disposed");
            Assert.Contains(lifetimes, d => d.GetProperty("objectId").GetInt64() == navigationId && d.GetProperty("state").GetString() == "Released");
            Assert.Contains(Events(records, "operation"), d => d.GetProperty("action").GetString() == "LocationChanged");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TimelineAndLifetimesUseAnonymousIdsAndDoNotSerializeOwners()
    {
        WithLog(() =>
        {
            var owner = new PrivateOwner();
            var lifetime = MemoryDiagnostics.Track(owner, DiagnosticObjectKind.FileList)!;
            lifetime.Link(42);
            MemoryDiagnostics.Operation(DiagnosticAction.ViewMode, lifetime.Id, mode: 2);
            using (var operation = MemoryDiagnostics.BeginOperation(DiagnosticAction.Navigate, lifetime.Id))
                operation!.Complete(count: 123);
            using (MemoryDiagnostics.BeginOperation(DiagnosticAction.DirectoryLoad, lifetime.Id)) { }
            lifetime.SetState(DiagnosticObjectState.Disposed);
            lifetime.SetState(DiagnosticObjectState.Attached);
            lifetime.Link(0);
            MemoryDiagnostics.Lifetimes.Sample();
            GC.KeepAlive(owner);
        }, records =>
        {
            var raw = string.Join("\n", records.Select(r => r.GetRawText()));
            Assert.DoesNotContain("private-fixture-path", raw);
            Assert.DoesNotContain("private-fixture-content", raw);
            var created = Assert.Single(Events(records, "lifetime.object").Where(d => d.GetProperty("change").GetString() == "created"));
            var id = created.GetProperty("objectId").GetInt64();
            Assert.True(id > 0);
            Assert.Contains(Events(records, "lifetime.object"), d => d.GetProperty("relatedId").GetInt64() == 42);
            var ends = Events(records, "operation.end").ToArray();
            Assert.Equal(2, ends.Length);
            Assert.All(ends, d => Assert.Equal(id, d.GetProperty("objectId").GetInt64()));
            Assert.Equal("Completed", ends[0].GetProperty("outcome").GetString());
            Assert.Equal(123, ends[0].GetProperty("count").GetInt32());
            Assert.Equal("Interrupted", ends[1].GetProperty("outcome").GetString());
            Assert.DoesNotContain(Events(records, "lifetime.object"), d => d.GetProperty("state").GetString() == "Attached");
            Assert.Equal(records.Length, records.Select(r => r.GetProperty("sequence").GetInt64()).Distinct().Count());
        });
    }

    [Fact]
    public void LifetimeRegistryIsBoundedAndDoesNotKeepOwnersAlive()
    {
        var registry = new MemoryDiagnosticLifetimes();
        var weak = TrackTemporaryOwner(registry);
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(weak.TryGetTarget(out _));
        for (var i = 0; i < MemoryDiagnosticLifetimes.Capacity + 10; i++)
            registry.Track(new object(), DiagnosticObjectKind.Tab);
        WithLog(() =>
        {
            registry.Sample();
        }, records =>
        {
            var summary = Assert.Single(Events(records, "lifetime.sample"));
            Assert.InRange(summary.GetProperty("tracked").GetInt32(), 0, MemoryDiagnosticLifetimes.Capacity);
            Assert.Equal(11, summary.GetProperty("evicted").GetInt64());
        });
    }

    [Fact]
    public void EndedObjectsAreReportedAfterNaturalCollectionWithoutCollectingDuringSample()
    {
        var owner = new object();
        var registry = new MemoryDiagnosticLifetimes();
        WithLog(() =>
        {
            var lifetime = registry.Track(owner, DiagnosticObjectKind.Workspace);
            lifetime.SetState(DiagnosticObjectState.Disposed);
            lifetime.StateAt = Stopwatch.GetTimestamp() - 60 * Stopwatch.Frequency;
            GC.Collect(2);
            var before = GC.CollectionCount(2);
            registry.Sample();
            Assert.Equal(before, GC.CollectionCount(2));
            GC.KeepAlive(owner);
        }, records =>
        {
            var retained = Assert.Single(Events(records, "lifetime.retained")).GetProperty("objects");
            var item = Assert.Single(retained.EnumerateArray());
            Assert.Equal("Disposed", item.GetProperty("state").GetString());
            Assert.True(item.GetProperty("gen2Collections").GetInt32() >= 1);
            Assert.True(item.GetProperty("ageSeconds").GetInt64() >= 30);
        });
    }

    [Fact]
    public void GcSnapshotsIncludeRareCollectionsAndDoNotRepeatUnchangedIndices()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        WithLog(() =>
        {
            var gc = new MemoryGcDiagnostics();
            gc.Sample();
            gc.Sample();
        }, records =>
        {
            var snapshots = Events(records, "memory.gc").ToArray();
            Assert.Contains(snapshots, d => d.GetProperty("kind").GetString() == "Ephemeral");
            Assert.Contains(snapshots, d => d.GetProperty("kind").GetString() == "Background");
            var full = Assert.Single(snapshots.Where(d => d.GetProperty("kind").GetString() == "FullBlocking"));
            Assert.True(full.GetProperty("available").GetBoolean());
            Assert.Equal(full.GetProperty("heapBytes").GetInt64() - full.GetProperty("fragmentedBytes").GetInt64(),
                full.GetProperty("liveAfterGcBytes").GetInt64());
            Assert.Equal(2, full.GetProperty("generation").GetInt32());
            Assert.Equal(5, full.GetProperty("generations").GetArrayLength());
            Assert.Equal(new[] { "gen0", "gen1", "gen2", "loh", "poh" }, full.GetProperty("generations").EnumerateArray()
                .Select(g => g.GetProperty("generation").GetString()).ToArray());
            Assert.True(full.TryGetProperty("finalizationPending", out _));
            Assert.True(full.TryGetProperty("pinnedObjects", out _));
            Assert.True(full.TryGetProperty("pauseDurationsMs", out _));
            Assert.Equal(snapshots.Length, snapshots.Select(d => (d.GetProperty("kind").GetString(), d.GetProperty("index").GetInt64())).Distinct().Count());
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object> TrackTemporaryOwner(MemoryDiagnosticLifetimes registry)
    {
        var owner = new PrivateOwner();
        registry.Track(owner, DiagnosticObjectKind.Preview);
        return new(owner);
    }

    private static IEnumerable<JsonElement> Events(JsonElement[] records, string name)
        => records.Where(r => r.GetProperty("name").GetString() == name).Select(r => r.GetProperty("data"));

    private static void WithLog(Action action, Action<JsonElement[]> verify)
    {
        var root = Path.Combine(Path.GetTempPath(), "fk-memory-diagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (MemoryDiagnostics.Start(root))
            {
                action();
            }
            verify(ReadRecords(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static JsonElement[] ReadRecords(string root)
        => Directory.GetFiles(root).SelectMany(File.ReadAllLines).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();

    private sealed class PrivateOwner
    {
        public string Path => "/private-fixture-path/personal-file.txt";
        public string Content => "private-fixture-content";
    }
}
