using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using Xunit;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MacExplorer.Tests;

[SupportedOSPlatform("macos")]
public sealed class LocalFileCopyTests : IDisposable
{
    private readonly string _root = Path.Combine("/tmp", "fkfinder-copy-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MacFileService _files = new();

    private string DirectoryAt(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    [Fact]
    public async Task PasteSkipsGitDaemonSocketAndFifoButCopiesRegularFilesWithSameName()
    {
        var git = DirectoryAt("project/.git");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(Path.Combine(git, "fsmonitor--daemon.ipc")));
        Assert.Equal(0, MkFifo(Path.Combine(git, "pipe"), 0x180));
        await File.WriteAllTextAsync(Path.Combine(git, "HEAD"), "ref: refs/heads/main");
        var ordinary = Path.Combine(DirectoryAt("project/ordinary"), "fsmonitor--daemon.ipc");
        await File.WriteAllTextAsync(ordinary, "regular file");
        var source = Path.Combine(_root, "project");
        var target = DirectoryAt("target");
        var clipboard = new TestClipboard();
        clipboard.CopyFiles([source]);
        var manager = new BackgroundTaskManager();
        var vm = new FileOpsViewModel(clipboard, _files, manager);

        await vm.PasteAsync(target).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("ref: refs/heads/main", await File.ReadAllTextAsync(Path.Combine(target, "project/.git/HEAD")));
        Assert.False(File.Exists(Path.Combine(target, "project/.git/fsmonitor--daemon.ipc")));
        Assert.False(File.Exists(Path.Combine(target, "project/.git/pipe")));
        Assert.Equal("regular file", await File.ReadAllTextAsync(Path.Combine(target, "project/ordinary/fsmonitor--daemon.ipc")));
        var task = Assert.Single(manager.Tasks);
        Assert.Equal(BackgroundTaskState.Completed, task.State);
        Assert.Contains("已跳过 2 个运行时通信文件", task.Label);
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(string path, ushort mode);

    [Fact]
    public async Task CopyPreservesGitFilesPermissionsAndLinksWithoutFollowingThem()
    {
        var source = DirectoryAt("project/.git/objects");
        var gitObject = Path.Combine(source, "object");
        await File.WriteAllTextAsync(gitObject, "git data");
        File.SetUnixFileMode(gitObject, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        source = Path.Combine(_root, "project");
        var executable = Path.Combine(source, "run.sh");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(Path.Combine(source, "empty.txt"), "");
        Directory.CreateSymbolicLink(Path.Combine(source, "cycle"), ".");
        File.CreateSymbolicLink(Path.Combine(source, "broken"), "missing");
        File.CreateSymbolicLink(Path.Combine(source, "file-link"), "run.sh");
        var outside = DirectoryAt("outside");
        await File.WriteAllTextAsync(Path.Combine(outside, "external.txt"), "external");
        Directory.CreateSymbolicLink(Path.Combine(source, "external"), outside);
        var updates = new List<FileOperationProgress>();

        var destination = await _files.CopyWithProgressAsync(source, DirectoryAt("target"), new CallbackProgress(updates.Add));

        Assert.Equal("git data", await File.ReadAllTextAsync(Path.Combine(destination, ".git/objects/object")));
        Assert.Equal(File.GetUnixFileMode(gitObject), File.GetUnixFileMode(Path.Combine(destination, ".git/objects/object")));
        Assert.Equal(File.GetUnixFileMode(executable), File.GetUnixFileMode(Path.Combine(destination, "run.sh")));
        Assert.Equal(".", new DirectoryInfo(Path.Combine(destination, "cycle")).LinkTarget);
        Assert.Equal("missing", new FileInfo(Path.Combine(destination, "broken")).LinkTarget);
        Assert.Equal("run.sh", new FileInfo(Path.Combine(destination, "file-link")).LinkTarget);
        Assert.Equal(outside, new DirectoryInfo(Path.Combine(destination, "external")).LinkTarget);
        Assert.True(Directory.Exists(Path.Combine(destination, "empty")));
        Assert.Equal(0, new FileInfo(Path.Combine(destination, "empty.txt")).Length);
        Assert.Equal(0, updates[0].Percentage);
        Assert.Equal(100, updates[^1].Percentage);
    }

    [Fact]
    public async Task CopyPreservesFinderTagsOnFilesAndFolders()
    {
        var source = DirectoryAt("tagged");
        var file = Path.Combine(source, "file.txt");
        await File.WriteAllTextAsync(file, "content");
        var store = new MacFileTagStore();
        await store.WriteAsync(source, [new NativeFileTag("Folder", 2)]);
        await store.WriteAsync(file, [new NativeFileTag("File", 4)]);

        var destination = await _files.CopyWithProgressAsync(source, DirectoryAt("target"));

        Assert.Equal(await store.ReadAsync(source), await store.ReadAsync(destination));
        Assert.Equal(await store.ReadAsync(file), await store.ReadAsync(Path.Combine(destination, "file.txt")));
    }

    [Fact]
    public async Task CopyDanglingLinkAsTopLevelItem()
    {
        var source = Path.Combine(DirectoryAt("source"), "broken");
        File.CreateSymbolicLink(source, "missing");
        var destination = await _files.CopyWithProgressAsync(source, DirectoryAt("target"));
        Assert.Equal("missing", new FileInfo(destination).LinkTarget);
    }

    [Fact]
    public async Task CopyRejectsDescendantIncludingDestinationThroughSymlink()
    {
        var source = DirectoryAt("source");
        var child = DirectoryAt("source/child");
        var alias = Path.Combine(_root, "alias");
        Directory.CreateSymbolicLink(alias, child);
        await Assert.ThrowsAsync<IOException>(() => _files.CopyAsync(source, child));
        await Assert.ThrowsAsync<IOException>(() => _files.CopyAsync(source, alias));
        await Assert.ThrowsAsync<IOException>(() => _files.CopyAsync("/", child));
        Assert.Empty(Directory.EnumerateFileSystemEntries(child));
    }

    [Fact]
    public async Task CopyInSameParentKeepsDirectoryExtensionAndExistingContents()
    {
        var source = DirectoryAt("project.v1");
        await File.WriteAllTextAsync(Path.Combine(source, ".git"), "gitdir: ../worktree");
        var destination = await _files.CopyWithProgressAsync(source, _root);
        Assert.Equal(Path.Combine(_root, "project.v1 副本"), destination);
        Assert.Equal("gitdir: ../worktree", await File.ReadAllTextAsync(Path.Combine(destination, ".git")));
        Assert.Equal(Path.Combine(_root, "project.v1 副本 2"), await _files.CopyWithProgressAsync(source, _root));
    }

    [Fact]
    public async Task CancellationDuringFileCopyRemovesIncompleteFileAndKeepsSource()
    {
        var source = Path.Combine(DirectoryAt("source"), "large.bin");
        using (var file = File.Create(source)) file.SetLength(32 * 1024 * 1024);
        using var cts = new CancellationTokenSource();
        var target = DirectoryAt("target");
        var progress = new CallbackProgress(p =>
        {
            if (p.Percentage > 0 && p.Percentage < 100) cts.Cancel();
            // Ensure the next chunk crosses the reporting interval.
            else if (p.CurrentFile.StartsWith("正在统计")) Thread.Sleep(110);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _files.CopyWithProgressAsync(source, target, progress, cts.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        Assert.Equal(32 * 1024 * 1024, new FileInfo(source).Length);
    }

    [Fact]
    public async Task UnreadableFileDoesNotPreventOtherFilesAndTaskRetainsFailureDetails()
    {
        var source = DirectoryAt("project");
        var unreadable = Path.Combine(source, "unreadable");
        await File.WriteAllTextAsync(unreadable, "private");
        File.SetUnixFileMode(unreadable, UnixFileMode.None);
        await File.WriteAllTextAsync(Path.Combine(source, "good.txt"), "keep copying");
        var target = DirectoryAt("target");
        var manager = new BackgroundTaskManager();
        var clipboard = new TestClipboard();
        clipboard.CopyFiles([source]);
        var vm = new FileOpsViewModel(clipboard, _files, manager);

        await Assert.ThrowsAsync<AggregateException>(() => vm.PasteAsync(target));

        var task = Assert.Single(manager.Tasks);
        Assert.Equal(BackgroundTaskState.Failed, task.State);
        Assert.Contains(unreadable, task.ErrorDetail);
        Assert.True(task.Progress < 100);
        Assert.Equal("keep copying", await File.ReadAllTextAsync(Path.Combine(target, "project/good.txt")));
        Assert.True(clipboard.HasClipboardFiles);
    }

    [Fact]
    public async Task PasteThroughCompositeReportsRunningAndCompletedTask()
    {
        var source = DirectoryAt("source");
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "a");
        var manager = new BackgroundTaskManager();
        var clipboard = new TestClipboard();
        clipboard.CopyFiles([source]);
        var running = false;
        var fileReported = false;
        manager.TasksChanged += () =>
        {
            var item = manager.Tasks.Single();
            running |= item.State == BackgroundTaskState.Running;
            fileReported |= item.CurrentFile.Length > 0;
        };
        var vm = new FileOpsViewModel(clipboard, new CompositeFileService(_files, null!), manager);
        await vm.PasteAsync(DirectoryAt("target"));
        var task = Assert.Single(manager.Tasks);
        Assert.True(running);
        Assert.True(fileReported);
        Assert.Equal(BackgroundTaskKind.Copy, task.Kind);
        Assert.Equal(BackgroundTaskState.Completed, task.State);
        Assert.Equal(100, task.Progress);
    }

    [Fact]
    public async Task CancelledPasteRetainsCancelledTask()
    {
        var source = DirectoryAt("source");
        var clipboard = new TestClipboard();
        clipboard.CopyFiles([source]);
        var manager = new BackgroundTaskManager();
        manager.TasksChanged += () =>
        {
            var task = manager.Tasks.Single();
            if (task.State == BackgroundTaskState.Running) manager.CancelTask(task.Id);
        };
        var vm = new FileOpsViewModel(clipboard, _files, manager);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.PasteAsync(DirectoryAt("target")));
        Assert.Equal(BackgroundTaskState.Cancelled, Assert.Single(manager.Tasks).State);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", new EnumerationOptions
                 { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.Delete(_root, true);
    }

    private sealed class CallbackProgress(Action<FileOperationProgress> action) : IProgress<FileOperationProgress>
    {
        public void Report(FileOperationProgress value) => action(value);
    }

    private sealed class TestClipboard : IClipboardService
    {
        private ClipboardEntry? _entry;
        public bool HasClipboardFiles => _entry is { IsEmpty: false };
        public ClipboardEntry? GetClipboardEntry() => _entry;
        public void CopyFiles(string[] paths) => _entry = new ClipboardEntry { SourcePaths = paths.ToList(), Operation = ClipboardOperation.Copy };
        public void CutFiles(string[] paths) => _entry = new ClipboardEntry { SourcePaths = paths.ToList(), Operation = ClipboardOperation.Cut };
        public void Clear() => _entry = null;
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public Task PasteFilesAsync(string targetDirectory) => throw new NotSupportedException();
    }
}
