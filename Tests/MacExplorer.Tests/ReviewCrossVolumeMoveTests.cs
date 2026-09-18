using System.Net.Sockets;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using Xunit;

namespace MacExplorer.Tests;

public sealed class ReviewCrossVolumeMoveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "move-" + Guid.NewGuid().ToString("N"));
    private readonly MacFileService _files = new();
    private string Source => Path.Combine(_root, "source");
    private string Target => Path.Combine(_root, "target");

    public ReviewCrossVolumeMoveTests()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Target);
    }

    private string SourceFile(string name, string text = "source data")
    {
        var path = Path.Combine(Source, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task ExistingTargetDirectoryIsNeverMergedOrDeleted()
    {
        var source = Directory.CreateDirectory(Path.Combine(Source, "folder")).FullName;
        File.WriteAllText(Path.Combine(source, "new.txt"), "new");
        var target = Directory.CreateDirectory(Path.Combine(Target, "folder")).FullName;
        var keep = Path.Combine(target, "keep.txt");
        File.WriteAllText(keep, "do not delete");
        await Assert.ThrowsAsync<IOException>(() => _files.MoveWithProgressAsync([source], Target));
        Assert.Equal("do not delete", File.ReadAllText(keep));
        Assert.True(File.Exists(Path.Combine(source, "new.txt")));
        Assert.False(File.Exists(Path.Combine(target, "new.txt")));
        AssertNoStaging();
    }

    [Fact]
    public async Task CancellationAfterFirstCommitKeepsItAndLeavesSecondSource()
    {
        var first = SourceFile("first");
        var second = SourceFile("second");
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(p => { if (p.Percentage == 50) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _files.MoveWithProgressAsync([first, second], Target, progress, cts.Token));
        Assert.False(File.Exists(first));
        Assert.Equal("source data", File.ReadAllText(Path.Combine(Target, "first")));
        Assert.True(File.Exists(second));
        Assert.False(File.Exists(Path.Combine(Target, "second")));
        AssertNoStaging();
    }

    [Fact]
    public async Task CancellationBeforePublicationLeavesOnlySource()
    {
        var source = SourceFile("file");
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(p => { if (p.Percentage >= 99.9) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _files.MoveWithProgressAsync([source], Target, progress, cts.Token));
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Target));
    }

    [Fact]
    public async Task TargetCreatedDuringCopyIsNeverOverwritten()
    {
        var source = SourceFile("file");
        var target = Path.Combine(Target, "file");
        var progress = new InlineProgress(p =>
        {
            if (p.Percentage >= 99.9 && !File.Exists(target)) File.WriteAllText(target, "other writer");
        });
        await Assert.ThrowsAsync<IOException>(() => _files.MoveWithProgressAsync([source], Target, progress));
        Assert.Equal("other writer", File.ReadAllText(target));
        Assert.Equal("source data", File.ReadAllText(source));
        AssertNoStaging();
    }

    [Fact]
    public async Task LaterMissingSourceDoesNotRollBackEarlierCommit()
    {
        var first = SourceFile("first");
        await Assert.ThrowsAnyAsync<IOException>(() =>
            _files.MoveWithProgressAsync([first, Path.Combine(Source, "missing")], Target));
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(Path.Combine(Target, "first")));
        AssertNoStaging();
    }

    [Fact]
    public async Task CompleteDirectoryMovesWithItsNestedContents()
    {
        var source = Directory.CreateDirectory(Path.Combine(Source, "folder")).FullName;
        var child = Directory.CreateDirectory(Path.Combine(source, "child")).FullName;
        File.WriteAllText(Path.Combine(child, "file"), "nested content");
        await _files.MoveWithProgressAsync([source], Target);
        Assert.False(Directory.Exists(source));
        Assert.Equal("nested content", File.ReadAllText(Path.Combine(Target, "folder", "child", "file")));
        AssertNoStaging();
    }

    [Fact]
    public async Task CancellationAfterFinalCommitDoesNotReportFailure()
    {
        var source = SourceFile("file");
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(p => { if (p.Percentage == 100) cts.Cancel(); });
        await _files.MoveWithProgressAsync([source], Target, progress, cts.Token);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(Target, "file")));
        AssertNoStaging();
    }

    [Fact]
    public async Task MoveToSameDirectoryIsANoOp()
    {
        var source = SourceFile("file");
        await _files.MoveWithProgressAsync([source], Source);
        Assert.Equal("source data", File.ReadAllText(source));
        Assert.Single(Directory.EnumerateFileSystemEntries(Source));
    }

    [Fact]
    public async Task MacDirectoryLinkIsMovedWithoutMovingItsTarget()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var linkedDirectory = Directory.CreateDirectory(Path.Combine(_root, "linked")).FullName;
        File.WriteAllText(Path.Combine(linkedDirectory, "keep"), "target content");
        var link = Path.Combine(Source, "link");
        Directory.CreateSymbolicLink(link, linkedDirectory);
        await _files.MoveWithProgressAsync([link], Target);
        Assert.Equal(linkedDirectory, new DirectoryInfo(Path.Combine(Target, "link")).LinkTarget);
        Assert.Equal("target content", File.ReadAllText(Path.Combine(linkedDirectory, "keep")));
        Assert.Null(new DirectoryInfo(link).LinkTarget);
        AssertNoStaging();
    }

    [Fact]
    public async Task MacRuntimeSocketCannotMakeAnIncompleteCopyDeleteTheSource()
    {
        if (!OperatingSystem.IsMacOS()) return;
        // Darwin limits Unix-domain socket paths; use a short /tmp fixture here.
        var socketRoot = Directory.CreateDirectory("/tmp/mv-" + Guid.NewGuid().ToString("N")[..8]).FullName;
        try
        {
            using (var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                socket.Bind(new UnixDomainSocketEndPoint(Path.Combine(socketRoot, "sock")));
                File.WriteAllText(Path.Combine(socketRoot, "keep"), "keep source");
                await Assert.ThrowsAsync<IOException>(() => _files.MoveWithProgressAsync([socketRoot], Target));
                Assert.Equal("keep source", File.ReadAllText(Path.Combine(socketRoot, "keep")));
                Assert.False(Directory.Exists(Path.Combine(Target, Path.GetFileName(socketRoot))));
                AssertNoStaging();
            }
        }
        finally { Directory.Delete(socketRoot, recursive: true); }
    }

    private void AssertNoStaging() => Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(Target),
        path => path.EndsWith(".fkfinder-tmp", StringComparison.Ordinal));

    private sealed class InlineProgress(Action<FileOperationProgress> report) : IProgress<FileOperationProgress>
    {
        public void Report(FileOperationProgress value) => report(value);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
