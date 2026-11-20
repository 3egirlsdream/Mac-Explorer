using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FeatureUpgradeTests
{
    private sealed class Decisions(Func<FileConflict, int, FileConflictDecision> decide) : IFileConflictResolver
    {
        public int Calls { get; private set; }
        public Task<FileConflictDecision> ResolveAsync(FileConflict conflict, CancellationToken token)
            => Task.FromResult(decide(conflict, ++Calls));
    }

    [Theory]
    [InlineData("individual", 6)]
    [InlineData("all", 1)]
    [InlineData("both", 1)]
    [InlineData("cancel", 6)]
    [Trait("Area", "FileConflicts")]
    public async Task NestedCutPasteResolvesEntireSelectionBeforeItsFirstWrite(string mode, int expectedCalls)
    {
        string[] collisions = ["封面.png", "子目录/封面.png", "子目录/深层/封面.png"];
        var first = At("source/无冲突.txt");
        var source = Path.Combine(_root, "source/项目");
        var target = Path.Combine(_root, "target/项目");
        foreach (var path in collisions)
        {
            At("source/项目/" + path, "source-" + path);
            At("target/项目/" + path, "old-" + path);
        }
        At("source/项目/.hidden", "secret");
        var decisions = new Decisions((conflict, count) =>
        {
            Assert.True(File.Exists(first));
            Assert.False(File.Exists(Path.Combine(_root, "target/无冲突.txt")));
            Assert.False(File.Exists(Path.Combine(target, ".hidden")));
            foreach (var path in collisions)
            {
                Assert.Equal("source-" + path, File.ReadAllText(Path.Combine(source, path)));
                Assert.Equal("old-" + path, File.ReadAllText(Path.Combine(target, path)));
            }
            if (mode == "cancel" && count == expectedCalls) throw new OperationCanceledException();
            if (mode == "both") return new(FileConflictAction.KeepBoth);
            if (mode == "individual" && conflict.Source.FullPath == Path.Combine(source, collisions[1]))
                return new(FileConflictAction.Skip);
            return new(FileConflictAction.Replace, mode == "all");
        });
        var tasks = new BackgroundTaskManager();
        var files = new CompositeFileService(new MacFileService(), null!, tasks, conflicts: decisions);
        var clipboard = new Clipboard();
        clipboard.CutFiles([first, source]);
        var vm = new FileOpsViewModel(clipboard, files, tasks);
        vm.CutPaths.UnionWith([first, source]);
        var paste = vm.PasteAsync(Path.GetDirectoryName(target)!);
        if (mode == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => paste);
            Assert.Equal(new[] { first, source }, clipboard.GetClipboardEntry()!.SourcePaths);
            Assert.True(File.Exists(first));
            Assert.False(File.Exists(Path.Combine(target, ".hidden")));
        }
        else
        {
            await paste;
            if (mode == "individual")
            {
                Assert.Equal(new[] { source }, clipboard.GetClipboardEntry()!.SourcePaths);
                Assert.Contains(source, vm.CutPaths);
                Assert.True(File.Exists(Path.Combine(source, collisions[1])));
            }
            else { Assert.False(Directory.Exists(source)); Assert.Null(clipboard.GetClipboardEntry()); }
            Assert.True(File.Exists(Path.Combine(_root, "target/无冲突.txt")));
            Assert.True(File.Exists(Path.Combine(mode == "both" ? target + " (1)" : target, ".hidden")));
        }
        foreach (var path in collisions)
        {
            var kept = mode is "cancel" or "both" || mode == "individual" && path == collisions[1];
            Assert.Equal((kept ? "old-" : "source-") + path, File.ReadAllText(Path.Combine(target, path)));
            if (mode == "both") Assert.Equal("source-" + path, File.ReadAllText(Path.Combine(target + " (1)", path)));
        }
        Assert.Equal(expectedCalls, decisions.Calls);
        Assert.Single(tasks.Tasks);
        Assert.Empty(((IBackgroundTaskManager)tasks).PanelTasks);
        await tasks.ShutdownAsync();
    }

    [Theory]
    [InlineData(FileConflictAction.Skip)]
    [InlineData(FileConflictAction.KeepBoth)]
    [InlineData(FileConflictAction.Replace)]
    [Trait("Area", "FileConflicts")]
    public async Task DragBatchDetectsCollisionsBetweenItsOwnSources(FileConflictAction action)
    {
        var first = At("a/same.txt", "first");
        var second = At("b/same.txt", "second");
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        var decisions = new Decisions((conflict, _) =>
        {
            Assert.Equal(Path.Combine(target, "same.txt"), conflict.Destination.FullPath);
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
            return new(action);
        });
        var files = new CompositeFileService(new MacFileService(), null!, conflicts: decisions);
        await new MacDragDropBridge(files, new DirectoryChangeNotifier()).DropFilesAsync([first, second], target, true, false);
        Assert.Equal(1, decisions.Calls);
        Assert.Equal(action == FileConflictAction.Replace ? "second" : "first", File.ReadAllText(Path.Combine(target, "same.txt")));
        if (action == FileConflictAction.KeepBoth) Assert.Equal("second", File.ReadAllText(Path.Combine(target, "same (1).txt")));
    }

    [Fact]
    [Trait("Area", "FileConflicts")]
    public async Task LateTargetCollisionStopsBeforeCopyingEarlierItems()
    {
        var first = At("source/a");
        var second = At("source/b");
        var existing = At("target/b");
        var late = Path.Combine(_root, "target/a");
        var files = new CompositeFileService(new MacFileService(), null!, conflicts:
            new Decisions((_, _) => { File.WriteAllText(late, "external"); return new(FileConflictAction.Replace); }));
        await Assert.ThrowsAsync<IOException>(() => files.CopyBatchAsync([first, second], Path.GetDirectoryName(existing)!));
        Assert.Equal("external", File.ReadAllText(late));
        Assert.Equal("data", File.ReadAllText(existing));
    }

    [Fact]
    [Trait("Area", "FileConflicts")]
    public async Task TwoFoldersWithTheSameNameMergeChildrenProducedByTheFirstSource()
    {
        var one = At("a/project/sub/file", "first");
        var two = At("b/project/sub/file", "second");
        var target = Directory.CreateDirectory(Path.Combine(_root, "destination")).FullName;
        var first = Path.GetDirectoryName(Path.GetDirectoryName(one))!;
        var second = Path.GetDirectoryName(Path.GetDirectoryName(two))!;
        var decisions = new Decisions((conflict, _) =>
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
            return new(conflict.Source.IsDirectory ? FileConflictAction.Replace : FileConflictAction.KeepBoth);
        });
        var service = new CompositeFileService(new MacFileService(), null!, conflicts: decisions);
        await service.CopyBatchAsync([first, second], target);
        Assert.Equal(3, decisions.Calls);
        Assert.Equal("first", File.ReadAllText(Path.Combine(target, "project/sub/file")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(target, "project/sub/file (1)")));
    }

    private sealed class CrossVolume : MacFileService, IFileService
    {
        bool IFileService.IsCrossVolume(string source, string target) => true;
    }

    [Fact]
    [Trait("Area", "FileTasks")]
    public async Task CrossVolumeMoveReportsPayloadBytesAndPreservesDirectoryLinks()
    {
        var source = Path.GetDirectoryName(At("source/project/a", "hello"))!;
        At("source/project/b", "world");
        var outside = Path.GetDirectoryName(At("outside/keep"))!;
        Directory.CreateSymbolicLink(Path.Combine(source, "linked"), outside);
        var target = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
        var tasks = new BackgroundTaskManager();
        var files = new CompositeFileService(new CrossVolume(), null!, tasks, conflicts: new Choice(FileConflictAction.Skip));
        await files.MoveAsync(source, target);
        var display = Assert.Single(tasks.Tasks).Request!.Operation!;
        Assert.False(display.ByItems);
        Assert.Equal(10, display.TotalBytes);
        Assert.Equal(display.TotalBytes, display.DoneBytes);
        Assert.Equal(4, display.TotalItems);
        Assert.Equal(display.TotalItems, display.DoneItems);
        Assert.Equal(outside, new DirectoryInfo(Path.Combine(target, "project/linked")).LinkTarget);
        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Combine(outside, "keep")));
        await tasks.ShutdownAsync();
    }

    private sealed class TestTrash(string root, string item) : MacFileService, IFileService
    {
        internal int EmptyCalls;
        Task<IReadOnlyList<FileSystemEntry>> IFileService.GetDirectoryContentsAsync(string path, CancellationToken token)
            => path == TrashDirectory
                ? Task.FromResult<IReadOnlyList<FileSystemEntry>>([new() { FullPath = item }])
                : throw new InvalidOperationException(path);
        Task IFileService.EmptyTrashAsync()
        {
            var task = FileTaskContext.Current!.Task;
            Assert.False(task.CanCancel);
            Assert.True(task.Request!.Operation!.FinderRunning);
            Assert.Equal(2, task.Request.Operation.TotalItems);
            Assert.StartsWith(root, item, StringComparison.Ordinal);
            EmptyCalls++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    [Trait("Area", "FileTasks")]
    public async Task DeleteSelectionAndFinderTrashEachUseOneSession()
    {
        var root = Path.GetDirectoryName(At("data/folder/a"))!;
        var sibling = At("data/b");
        var outside = Path.GetDirectoryName(At("outside/keep"))!;
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);
        var tasks = new BackgroundTaskManager();
        var local = new TestTrash(_root, outside);
        var files = new CompositeFileService(local, null!, tasks);
        await files.PermanentlyDeleteBatchAsync([root, sibling]);
        Assert.False(Directory.Exists(root));
        Assert.False(File.Exists(sibling));
        Assert.True(File.Exists(Path.Combine(outside, "keep")));
        var display = Assert.Single(tasks.Tasks).Request!.Operation!;
        Assert.Equal(4, display.TotalItems);
        Assert.Equal(4, display.DoneItems);
        await files.EmptyTrashAsync();
        Assert.Equal(1, local.EmptyCalls);
        Assert.Equal(2, tasks.Tasks.Count);
        Assert.Empty(((IBackgroundTaskManager)tasks).PanelTasks);
        await tasks.ShutdownAsync();
    }

    private sealed class Clock : TimeProvider
    {
        public long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }

    [Fact]
    [Trait("Area", "FileTasks")]
    public void WindowDelayIncludesWaitingAndScanningAndExcludesConfirmation()
    {
        var clock = new Clock();
        var display = new FileOperationDisplay(FileOperationPresentation.Paste, clock);
        clock.Milliseconds = 799;
        Assert.False(display.ShouldShow(BackgroundTaskState.Running));
        display.BeginConfirmation();
        clock.Milliseconds += 5000;
        Assert.False(display.ShouldShow(BackgroundTaskState.Running));
        display.EndConfirmation();
        Assert.False(display.ShouldShow(BackgroundTaskState.Waiting));
        clock.Milliseconds++;
        Assert.True(display.Preparing);
        Assert.True(display.ShouldShow(BackgroundTaskState.Waiting));
        Assert.False(display.ShouldShow(BackgroundTaskState.Completed));
        Assert.True(display.ShouldShow(BackgroundTaskState.NeedsAttention));
    }
}
