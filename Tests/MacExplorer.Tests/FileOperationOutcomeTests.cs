using System.Reflection;
using MacExplorer.Models;
using MacExplorer.Services;
using Xunit;

namespace MacExplorer.Tests;

public sealed class FileOperationOutcomeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fk-operation-outcome-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CompletedMoveReturnsPathWhenTagAndHistoryUpdatesFail()
    {
        var sourceDir = Path.Combine(_root, "source");
        var destinationDir = Path.Combine(_root, "destination");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destinationDir);
        var source = Path.Combine(sourceDir, "one.txt");
        File.WriteAllText(source, "one");
        var files = Proxy<IFileService>((method, args) => method.Name switch
        {
            nameof(IFileService.MoveAsync) => Move((string)args![0]!, (string)args[1]!),
            nameof(IFileService.CombinePath) => Path.Combine((string)args![0]!, (string)args[1]!),
            _ => throw new NotSupportedException(method.Name)
        });
        var tags = FailingTags();
        var history = Proxy<IFileOperationHistoryService>((method, _) => method.Name switch
        {
            nameof(IFileOperationHistoryService.RecordMoveAsync) => Task.FromException(new IOException("history unavailable")),
            _ => throw new NotSupportedException(method.Name)
        });
        var service = new FileOperationService(files, null, null, tags, history, null, null);
        var status = string.Empty;
        var result = await service.MoveDetailedAsync(new FileSystemEntry { FullPath = source },
            new FileSystemEntry { FullPath = destinationDir, IsDirectory = true }, value => status = value);
        Assert.Equal([Path.Combine(destinationDir, "one.txt")], result.CompletedPaths);
        Assert.Empty(result.FailedPaths);
        Assert.Equal(2, result.Warnings.Count);
        Assert.Contains("已移动", status);
        Assert.True(File.Exists(result.CompletedPaths[0]));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task BatchCopyReportsCommittedPathAndUnfinishedPathSeparately()
    {
        var sourceDir = Path.Combine(_root, "source");
        var destinationDir = Path.Combine(_root, "destination");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destinationDir);
        var first = Path.Combine(sourceDir, "one.txt");
        var second = Path.Combine(sourceDir, "two.txt");
        File.WriteAllText(first, "one");
        File.WriteAllText(second, "two");
        var files = Proxy<IFileService>((method, args) => method.Name switch
        {
            nameof(IFileService.CopyWithProgressAsync) => Copy((string)args![0]!, (string)args[1]!),
            _ => throw new NotSupportedException(method.Name)
        });
        var service = new FileOperationService(files, null, null, FailingTags(), null, null, null);
        var error = await Assert.ThrowsAsync<FileOperationPartialException>(() =>
            service.CopyDetailedAsync([first, second], destinationDir));
        Assert.Equal([Path.Combine(destinationDir, "one.txt")], error.Result.CompletedPaths);
        Assert.Equal([second], error.Result.FailedPaths);
        Assert.Single(error.Result.Warnings);
        Assert.True(File.Exists(error.Result.CompletedPaths[0]));
        Assert.False(File.Exists(Path.Combine(destinationDir, "two.txt")));
    }

    [Fact]
    public async Task RemoteCopyAndMoveReturnServicePathsWithoutLocalPathConversion()
    {
        var source = VirtualPath.BuildRemotePath("server", "/source/one.txt");
        var destination = VirtualPath.BuildRemotePath("server", "/target");
        var expected = VirtualPath.BuildRemotePath("server", "/target/one.txt");
        var files = Proxy<IFileService>((method, args) => method.Name switch
        {
            nameof(IFileService.CopyWithProgressAsync) => Task.FromResult(expected),
            nameof(IFileService.MoveAsync) => Task.CompletedTask,
            nameof(IFileService.CombinePath) => expected,
            _ => throw new NotSupportedException(method.Name)
        });
        var service = new FileOperationService(files, null, null, null, null, null, null);

        var copied = await service.CopyDetailedAsync([source], destination);
        var moved = await service.MoveDetailedAsync(new FileSystemEntry { FullPath = source },
            new FileSystemEntry { FullPath = destination, IsDirectory = true });

        Assert.Equal([expected], copied.CompletedPaths);
        Assert.Equal([expected], moved.CompletedPaths);
        Assert.Empty(copied.Warnings);
        Assert.Empty(moved.Warnings);
    }

    private static Task Move(string source, string destination)
    {
        File.Move(source, Path.Combine(destination, Path.GetFileName(source)));
        return Task.CompletedTask;
    }

    private static Task<string> Copy(string source, string destination)
    {
        if (Path.GetFileName(source) == "two.txt") throw new IOException("simulated copy failure");
        var path = Path.Combine(destination, Path.GetFileName(source));
        File.Copy(source, path);
        return Task.FromResult(path);
    }

    private static IFileTagService FailingTags() => Proxy<IFileTagService>((method, _) => method.Name switch
    {
        nameof(IFileTagService.UpdatePathAsync) or nameof(IFileTagService.CopyPathAsync) =>
            Task.FromException(new IOException("tag store unavailable")),
        _ => throw new NotSupportedException(method.Name)
    });

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> action) where T : class
    {
        var proxy = DispatchProxy.Create<T, OperationProxy>();
        ((OperationProxy)(object)proxy).Action = action;
        return proxy;
    }

    public class OperationProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Action { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Action(targetMethod!, args);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
