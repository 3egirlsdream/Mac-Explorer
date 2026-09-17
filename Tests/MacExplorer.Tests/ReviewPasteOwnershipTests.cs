using System.Reflection;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

public sealed class ReviewPasteOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldPasteNeverClearsANewerCopyOrCut(bool newCut)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clipboard = new ClipboardStub("/source/old.txt");
        var files = Stub<IFileService>((method, _) =>
        {
            Assert.Equal("MoveAsync", method.Name);
            entered.TrySetResult();
            return release.Task;
        });
        var vm = new FileOpsViewModel(clipboard.Service, files);
        vm.CutPaths.Add("/source/old.txt");
        var paste = vm.PasteAsync("/target");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            if (newCut) clipboard.Service.CutFiles(["/source/new.txt"]);
            else clipboard.Service.CopyFiles(["/source/new.txt"]);
            vm.CutPaths.Clear();
            if (newCut) vm.CutPaths.Add("/source/new.txt");
        }
        finally { release.TrySetResult(); }
        var newer = clipboard.Entry;
        await paste;
        Assert.Same(newer, clipboard.Entry);
        Assert.Equal(0, clipboard.ClearCount);
        Assert.Equal(newCut, vm.CutPaths.Contains("/source/new.txt"));
    }

    [Fact]
    public async Task PartialCutFailureRetainsOnlyUnmovedPathsAndNotifiesOnce()
    {
        var clipboard = new ClipboardStub("/source/a.txt", "/source/b.txt", "/source/c.txt");
        var moved = new List<string>();
        var failSecond = true;
        var changes = new List<string[]>();
        var files = Stub<IFileService>((method, args) =>
        {
            Assert.Equal("MoveAsync", method.Name);
            var path = (string)args[0]!;
            if (failSecond && path.EndsWith("b.txt")) return Task.FromException(new IOException("simulated failure"));
            moved.Add(path);
            return Task.CompletedTask;
        });
        var notifier = Stub<IDirectoryChangeNotifier>((method, args) =>
        {
            Assert.Equal("NotifyChanged", method.Name);
            changes.Add((string[])args[0]!);
            return null;
        });
        var vm = new FileOpsViewModel(clipboard.Service, files, directoryChangeNotifier: notifier);
        vm.CutPaths.UnionWith(clipboard.Entry!.SourcePaths);
        await Assert.ThrowsAsync<IOException>(() => vm.PasteAsync("/target"));
        Assert.Equal(new[] { "/source/b.txt", "/source/c.txt" }, clipboard.Entry!.SourcePaths);
        Assert.DoesNotContain("/source/a.txt", vm.CutPaths);
        Assert.Single(changes);
        Assert.Contains("/source", changes[0]);
        Assert.Contains("/target", changes[0]);
        failSecond = false;
        await vm.PasteAsync("/target");
        Assert.Equal(new[] { "/source/a.txt", "/source/b.txt", "/source/c.txt" }, moved);
        Assert.Null(clipboard.Entry);
        Assert.Empty(vm.CutPaths);
    }

    [Fact]
    public async Task MetadataFailureDoesNotKeepAnAlreadyMovedFileInTheCutClipboard()
    {
        var clipboard = new ClipboardStub("/source/a.txt");
        var files = Stub<IFileService>((_, _) => Task.CompletedTask);
        var tags = Stub<IFileTagService>((method, _) =>
        {
            Assert.Equal("UpdatePathAsync", method.Name);
            return Task.FromException(new IOException("tag update failed"));
        });
        var vm = new FileOpsViewModel(clipboard.Service, files, fileTagService: tags);
        vm.CutPaths.Add("/source/a.txt");
        await Assert.ThrowsAsync<IOException>(() => vm.PasteAsync("/target"));
        Assert.Null(clipboard.Entry);
        Assert.Empty(vm.CutPaths);
    }

    [Fact]
    public async Task FailedCopyNotifiesEvenWithoutABackgroundTaskManager()
    {
        var clipboard = new ClipboardStub();
        clipboard.Service.CopyFiles(["/source/a.txt"]);
        var files = Stub<IFileService>((method, _) =>
        {
            Assert.Equal("CopyWithProgressAsync", method.Name);
            return Task.FromException<string>(new IOException("partial copy"));
        });
        var changes = new List<string[]>();
        var notifier = Stub<IDirectoryChangeNotifier>((_, args) => { changes.Add((string[])args[0]!); return null; });
        var vm = new FileOpsViewModel(clipboard.Service, files, directoryChangeNotifier: notifier);
        await Assert.ThrowsAsync<IOException>(() => vm.PasteAsync("/target"));
        Assert.Single(changes);
        Assert.Contains("/target", changes[0]);
    }

    [Fact]
    public async Task NewFolderRefreshUsesTheNameActuallyReturnedByTheFileService()
    {
        var files = Stub<IFileService>((method, _) =>
        {
            Assert.Equal("CreateFolderAsync", method.Name);
            return Task.FromResult("/target/未命名文件夹 2");
        });
        var vm = new FileOpsViewModel(fileService: files);
        string? selected = null;
        await vm.CreateNewFolderAsync("/target", [], refreshCallback: name => { selected = name; return Task.CompletedTask; });
        Assert.Equal("未命名文件夹 2", selected);
    }

    private sealed class ClipboardStub
    {
        public ClipboardEntry? Entry { get; private set; }
        public int ClearCount { get; private set; }
        public IClipboardService Service { get; }
        public ClipboardStub(params string[] paths)
        {
            Entry = new() { SourcePaths = paths.ToList(), Operation = ClipboardOperation.Cut };
            Service = Stub<IClipboardService>((method, args) =>
            {
                switch (method.Name)
                {
                    case "get_HasClipboardFiles": return Entry is { IsEmpty: false };
                    case "GetClipboardEntry": return Entry;
                    case "Clear": Entry = null; ClearCount++; return null;
                    case "CopyFiles":
                    case "CutFiles":
                        Entry = new() { SourcePaths = ((string[])args[0]!).ToList(),
                            Operation = method.Name == "CutFiles" ? ClipboardOperation.Cut : ClipboardOperation.Copy };
                        return null;
                    default: throw new NotSupportedException(method.Name);
                }
            });
        }
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
