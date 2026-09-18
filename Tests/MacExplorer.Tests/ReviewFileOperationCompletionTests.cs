using System.Reflection;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

public sealed class ReviewFileOperationCompletionTests
{
    private static FileSystemEntry Entry(string path, bool directory = false) =>
        new() { FullPath = path, Name = Path.GetFileName(path), IsDirectory = directory };

    [Fact]
    public async Task DeleteUsesTheOriginalSelectionAcrossAwaits()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleted = new List<string>();
        var selection = new List<FileSystemEntry> { Entry("/source/a"), Entry("/source/b") };
        var files = Stub<IFileService>((method, args) =>
        {
            Assert.Equal("DeleteAsync", method.Name);
            deleted.Add((string)args[0]!);
            if (deleted.Count != 1) return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        });
        var vm = new FileOpsViewModel(fileService: files);
        var operation = vm.DeleteSelectedAsync(selection, "/source");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            selection.Clear();
            selection.Add(Entry("/source/unrelated"));
        }
        finally { release.TrySetResult(); }
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "/source/a", "/source/b" }, deleted);
    }

    [Fact]
    public async Task FailedDeleteCleansOnlySuccessfulItemsAndNotifies()
    {
        var cleaned = new List<string>();
        var aiCleaned = new List<string>();
        var changes = new List<string[]>();
        var failure = new IOException("delete b failed");
        var files = Stub<IFileService>((_, args) => (string)args[0]! == "/source/a"
            ? Task.CompletedTask : Task.FromException(failure));
        var tags = Stub<IFileTagService>((method, args) =>
        {
            Assert.Equal("DeletePathAsync", method.Name);
            cleaned.Add((string)args[0]!);
            return Task.CompletedTask;
        });
        var ai = Stub<IAiTagService>((method, args) =>
        {
            Assert.Equal("DeleteAnalysisForFilesAsync", method.Name);
            aiCleaned.AddRange((IEnumerable<string>)args[0]!);
            return Task.CompletedTask;
        });
        var notifier = Stub<IDirectoryChangeNotifier>((_, args) =>
        {
            changes.Add((string[])args[0]!);
            Assert.Null(args[1]);
            return null;
        });
        var vm = new FileOpsViewModel(fileService: files, fileTagService: tags,
            aiTagService: ai, directoryChangeNotifier: notifier);
        string? status = null;
        var thrown = await Assert.ThrowsAsync<IOException>(() => vm.DeleteSelectedAsync(
            [Entry("/source/a"), Entry("/source/b"), Entry("/other/c")], "/virtual", value => status = value));
        Assert.Same(failure, thrown);
        Assert.Equal(new[] { "/source/a" }, cleaned);
        Assert.Equal(cleaned, aiCleaned);
        Assert.Contains("1/3", status!);
        Assert.Contains("/source", Assert.Single(changes));
    }

    [Fact]
    public async Task FailedHistoryDoesNotPreventTheRemainingDeletes()
    {
        var deleted = new List<string>();
        var files = Stub<IFileService>((_, args) => { deleted.Add((string)args[0]!); return Task.CompletedTask; });
        var history = Stub<IFileOperationHistoryService>((method, _) =>
        {
            Assert.Equal("RecordTrashAsync", method.Name);
            return Task.FromException(new IOException("history unavailable"));
        });
        var vm = new FileOpsViewModel(fileService: files, fileOperationHistoryService: history);
        string? status = null;
        await vm.DeleteSelectedAsync([Entry("/source/a"), Entry("/source/b")], "/source", value => status = value);
        Assert.Equal(new[] { "/source/a", "/source/b" }, deleted);
        Assert.Contains("已删除 2 项", status!);
        Assert.Contains("更新失败", status!);
    }

    [Fact]
    public async Task CleanupFailureDoesNotMaskTheDeleteErrorOrSkipOtherCleanup()
    {
        var calls = 0;
        var failure = new IOException("primary delete error");
        var files = Stub<IFileService>((_, _) => ++calls < 3 ? Task.CompletedTask : Task.FromException(failure));
        var cleaned = new List<string>();
        var tags = Stub<IFileTagService>((_, args) =>
        {
            cleaned.Add((string)args[0]!);
            return Task.FromException(new IOException("secondary tag error"));
        });
        var changes = 0;
        var notifier = Stub<IDirectoryChangeNotifier>((_, _) => { changes++; return null; });
        var vm = new FileOpsViewModel(fileService: files, fileTagService: tags, directoryChangeNotifier: notifier);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => vm.DeleteSelectedAsync(
            [Entry("/source/a"), Entry("/source/b"), Entry("/source/c")], "/source")));
        Assert.Equal(new[] { "/source/a", "/source/b" }, cleaned);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task FirstDeleteFailureStillNotifiesButDoesNotCleanMetadata()
    {
        var files = Stub<IFileService>((_, _) => Task.FromException(new IOException("partial recursive delete")));
        var tags = Stub<IFileTagService>((_, _) => throw new InvalidOperationException("must not clean unconfirmed deletion"));
        var changes = 0;
        var notifier = Stub<IDirectoryChangeNotifier>((_, _) => { changes++; return null; });
        var vm = new FileOpsViewModel(fileService: files, fileTagService: tags, directoryChangeNotifier: notifier);
        await Assert.ThrowsAsync<IOException>(() => vm.DeleteSelectedAsync([Entry("/source/folder", true)], "/source"));
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task PartialMoveNotifiesBothSourceAndDestination()
    {
        var changes = new List<string[]>();
        var files = Stub<IFileService>((method, args) => method.Name switch
        {
            "IsCrossVolume" => false,
            "MoveAsync" => (string)args[0]! == "/source/a" ? Task.CompletedTask : Task.FromException(new IOException("second move failed")),
            _ => throw new NotSupportedException(method.Name)
        });
        var notifier = Stub<IDirectoryChangeNotifier>((_, args) => { changes.Add((string[])args[0]!); return null; });
        var vm = new FileOpsViewModel(fileService: files, directoryChangeNotifier: notifier);
        await Assert.ThrowsAsync<IOException>(() => vm.MoveEntriesAsync(
            [Entry("/source/a"), Entry("/source/b")], Entry("/target", true)));
        var change = Assert.Single(changes);
        Assert.Contains("/source", change);
        Assert.Contains("/target", change);
    }

    [Fact]
    public async Task CrossVolumeMoveWithoutTaskManagerStillUsesTheCrossVolumeProvider()
    {
        var moved = new List<string>();
        var files = Stub<IFileService>((method, args) =>
        {
            if (method.Name == "IsCrossVolume") return true;
            Assert.Equal("MoveWithProgressAsync", method.Name);
            moved.AddRange((IReadOnlyList<string>)args[0]!);
            return Task.CompletedTask;
        });
        var vm = new FileOpsViewModel(fileService: files);
        await vm.MoveEntriesAsync([Entry("/source/a"), Entry("/source/b")], Entry("/target", true));
        Assert.Equal(new[] { "/source/a", "/source/b" }, moved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataFailureAfterSingleMoveOrRenameStillNotifies(bool rename)
    {
        var files = Stub<IFileService>((method, _) =>
        {
            Assert.Equal(rename ? "RenameAsync" : "MoveAsync", method.Name);
            return Task.CompletedTask;
        });
        var tags = Stub<IFileTagService>((method, _) =>
        {
            Assert.Equal("UpdatePathAsync", method.Name);
            return Task.FromException(new IOException("metadata unavailable"));
        });
        var changes = 0;
        var notifier = Stub<IDirectoryChangeNotifier>((_, _) => { changes++; return null; });
        var vm = new FileOpsViewModel(fileService: files, fileTagService: tags, directoryChangeNotifier: notifier);
        await Assert.ThrowsAsync<IOException>(() => rename
            ? vm.RenameEntryAsync(Entry("/source/a"), "b", false)
            : vm.MoveEntryAsync(Entry("/source/a"), Entry("/target", true)));
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task MixedSourceVolumesChooseTheCorrectProviderForEachItem()
    {
        var calls = new List<string>();
        var files = Stub<IFileService>((method, args) =>
        {
            if (method.Name == "IsCrossVolume") return ((string)args[0]!).StartsWith("/Volumes/", StringComparison.Ordinal);
            if (method.Name == "MoveAsync") calls.Add("local:" + args[0]);
            else if (method.Name == "MoveWithProgressAsync") calls.Add("cross:" + ((IReadOnlyList<string>)args[0]!)[0]);
            else throw new NotSupportedException(method.Name);
            return Task.CompletedTask;
        });
        var vm = new FileOpsViewModel(fileService: files);
        await vm.MoveEntriesAsync([Entry("/source/a"), Entry("/Volumes/USB/b")], Entry("/target", true));
        Assert.Equal(new[] { "local:/source/a", "cross:/Volumes/USB/b" }, calls);
    }

    private static T Stub<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var value = DispatchProxy.Create<T, InterfaceStub>();
        ((InterfaceStub)(object)value).Handler = handler;
        return value;
    }

    public class InterfaceStub : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = (_, _) => throw new NotSupportedException();
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }
}
