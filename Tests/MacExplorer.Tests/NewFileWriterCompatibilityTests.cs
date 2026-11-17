using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "FileTasks")]
public sealed class NewFileWriterCompatibilityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fk-new-file-copy-").FullName;

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("symlink")]
    public async Task ExclusiveCopyNeverReplacesAnExistingEntry(string kind)
    {
        var destination = Path.Combine(_root, "existing");
        if (kind == "file") await File.WriteAllTextAsync(destination, "original");
        else if (kind == "directory") Directory.CreateDirectory(destination);
        else File.CreateSymbolicLink(destination, "missing-target");
        using var source = new MemoryStream("new contents"u8.ToArray());

        Assert.False(await NewFileWriter.TryCopyNewFileAsync(source, destination));

        Assert.Equal(0, source.Position);
        if (kind == "file") Assert.Equal("original", await File.ReadAllTextAsync(destination));
        else if (kind == "directory") Assert.True(Directory.Exists(destination));
        else Assert.Equal("missing-target", new FileInfo(destination).LinkTarget);
    }

    [Fact]
    public async Task ConcurrentExclusiveCopiesKeepExactlyOneWinnersContents()
    {
        var destination = Path.Combine(_root, "winner.txt");
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(async index =>
        {
            using var source = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("writer-" + index));
            return (Index: index, Won: await NewFileWriter.TryCopyNewFileAsync(source, destination));
        }));
        var winner = Assert.Single(results.Where(result => result.Won));
        Assert.Equal("writer-" + winner.Index, await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task FailedCopyRemovesItsPartialFileAndKeepsTheOriginalError()
    {
        var destination = Path.Combine(_root, "failed.txt");
        var failure = new IOException("simulated source failure");
        using var source = new CallbackSource(async (output, token) =>
        {
            await output.WriteAsync("partial"u8.ToArray(), token);
            throw failure;
        });

        var error = await Assert.ThrowsAsync<IOException>(() => NewFileWriter.TryCopyNewFileAsync(source, destination));

        Assert.Same(failure, error);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task CancellationDuringCopyRemovesOnlyItsPartialFile()
    {
        var destination = Path.Combine(_root, "cancelled.txt");
        using var cancellation = new CancellationTokenSource();
        using var source = new CallbackSource(async (output, token) =>
        {
            await output.WriteAsync("partial"u8.ToArray(), token);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewFileWriter.TryCopyNewFileAsync(source, destination, cancellation.Token));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task CleanupDoesNotRemoveAFileThatReplacedOurOutput()
    {
        var destination = Path.Combine(_root, "replaced.txt");
        using var source = new CallbackSource(async (output, token) =>
        {
            await output.WriteAsync("partial"u8.ToArray(), token);
            File.Move(destination, Path.Combine(_root, "our-original-output"));
            await File.WriteAllTextAsync(destination, "other writer");
            throw new IOException("simulated source failure");
        });

        await Assert.ThrowsAsync<IOException>(() => NewFileWriter.TryCopyNewFileAsync(source, destination));

        Assert.Equal("other writer", await File.ReadAllTextAsync(destination));
    }

    internal sealed class CallbackSource(Func<Stream, CancellationToken, Task> copy) : MemoryStream
    {
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            => copy(destination, cancellationToken);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
