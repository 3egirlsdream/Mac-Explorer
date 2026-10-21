using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "FileTasks")]
public sealed class SessionOperationHistoryTests : IDisposable
{
    [Fact]
    public async Task HistoryRetainsOnlyTheMostRecentOperationGroups()
    {
        var history = new FileOperationHistoryService(_files);
        for (var i = 0; i < FileOperationHistoryService.HistoryLimit + 10; i++)
            await history.RecordIrreversibleAsync("/tmp/history-" + i, "deleted");
        Assert.Equal(FileOperationHistoryService.HistoryLimit, history.Records.Count);
        Assert.Equal("/tmp/history-10", history.Records[0].OriginalPath);
    }

    [Fact]
    public async Task HistoryLimitDoesNotSplitAnOperationThatIsStillRunning()
    {
        var manager = new BackgroundTaskManager();
        var history = new FileOperationHistoryService(_files, tasks: manager);
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var operation = manager.RunAsync(new(MacExplorer.Models.BackgroundTaskKind.Copy, "active", []), async _ =>
        {
            await history.RecordIrreversibleAsync("/tmp/active-history", "test");
            entered.SetResult(); await release.Task; return true;
        });
        await entered.Task;
        try
        {
            for (var i = 0; i < FileOperationHistoryService.HistoryLimit; i++)
                await history.RecordIrreversibleAsync("/tmp/later-history-" + i, "test");
            Assert.Contains(history.Records, record => record.OriginalPath == "/tmp/active-history");
            Assert.Equal(FileOperationHistoryService.HistoryLimit, history.Records.Count);
        }
        finally { release.SetResult(); await operation; }
    }
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fk-session-history-" + Guid.NewGuid().ToString("N"));
    private readonly MacFileService _files = new();
    private string PathAt(string name) { Directory.CreateDirectory(_root); return Path.Combine(_root, name); }

    [Fact]
    public async Task HistoryIsInstanceOnlyAndRejectsReplacingOrChangingCreatedFiles()
    {
        var path = PathAt("new.txt"); await File.WriteAllTextAsync(path, "initial");
        var history = new FileOperationHistoryService(_files);
        await history.RecordCreatedAsync(path);
        var record = Assert.Single(history.Records); Assert.True(history.CanUndo);
        await File.WriteAllTextAsync(path, "changed");
        var result = await history.UndoAsync(record.Id);
        Assert.False(result.Success); Assert.Equal("changed", await File.ReadAllTextAsync(path));
        Assert.True(history.CanUndo); Assert.Empty(new FileOperationHistoryService(_files).Records);
        history.ClearHistory(); Assert.False(history.CanUndo); Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task UndoByIdRestoresOnlyTheSelectedRenameAndChecksNewerDependencies()
    {
        var a = PathAt("a"); var b = PathAt("b"); var c = PathAt("c");
        await File.WriteAllTextAsync(a, "A"); await File.WriteAllTextAsync(b, "B");
        var history = new FileOperationHistoryService(_files);
        var renamedA = PathAt("renamed-a"); var renamedB = PathAt("renamed-b");
        File.Move(a, renamedA); await history.RecordRenameAsync(a, renamedA); var first = history.Records[0];
        File.Move(b, renamedB); await history.RecordRenameAsync(b, renamedB);
        Assert.True((await history.UndoAsync(first.Id)).Success);
        Assert.True(File.Exists(a)); Assert.True(File.Exists(renamedB));
        File.Move(renamedB, c); await history.RecordMoveAsync(renamedB, c);
        Assert.False((await history.UndoAsync(history.Records[1].Id)).Success);
        Assert.True((await history.UndoLastAsync())); Assert.True(File.Exists(renamedB));
    }

    [Fact]
    public async Task MissingTrashLocationAndOverwritesAreExplicitlyIrreversible()
    {
        var history = new FileOperationHistoryService(_files);
        await history.RecordTrashAsync(PathAt("deleted"), "");
        await history.RecordIrreversibleAsync(PathAt("overwrite"), "overwrite");
        Assert.False(history.CanUndo); Assert.All(history.Records, r => Assert.NotNull(r.UnavailableReason));
        Assert.False((await history.UndoAsync(history.Records[0].Id)).Success);
    }

    [Fact]
    public async Task RestoreConflictRetainsHistoryAndNeverOverwritesTheOccupant()
    {
        var original = PathAt("original"); var renamed = PathAt("renamed");
        await File.WriteAllTextAsync(original, "original data"); File.Move(original, renamed);
        var history = new FileOperationHistoryService(_files); await history.RecordRenameAsync(original, renamed);
        await File.WriteAllTextAsync(original, "new occupant");
        Assert.False(await history.UndoLastAsync()); Assert.True(history.CanUndo);
        Assert.Equal("new occupant", await File.ReadAllTextAsync(original));
        Assert.Equal("original data", await File.ReadAllTextAsync(renamed));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
