using System.Text;
using MacExplorer.PluginSdk;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Plugins;
using Xunit;

namespace MacExplorer.Tests;

public sealed class ReviewDataIntegrityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("macexplorer-review-").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public async Task Sha256MatchesKnownVectors(string text, string expected)
    {
        var path = Path.Combine(_root, "hash.bin");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(text));
        Assert.Equal(expected, await FileHashCalculator.ComputeAsync(path));
    }

    [Fact]
    public async Task AutomaticHashBudgetDefersRatherThanReadingTheEntireFile()
    {
        var path = Path.Combine(_root, "hash.bin");
        await File.WriteAllTextAsync(path, "abcdef");
        Assert.Null(await FileHashCalculator.ComputeAsync(path, byteLimit: 2));
        Assert.NotNull(await FileHashCalculator.ComputeAsync(path));
    }

    [Fact]
    public async Task CancelledHashDoesNotOpenAMissingFile()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileHashCalculator.ComputeAsync(Path.Combine(_root, "missing"), cancellation.Token));
    }

    [Fact]
    public async Task HashDoesNotRequireExclusiveAccessAgainstAnExistingWriter()
    {
        var path = Path.Combine(_root, "hash.bin");
        await File.WriteAllTextAsync(path, "abc");
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        Assert.NotNull(await FileHashCalculator.ComputeAsync(path));
    }

    [Fact]
    public async Task NewFilesNeverReplaceAnUnlistedCollision()
    {
        var desired = Path.Combine(_root, "image.png");
        await File.WriteAllTextAsync(desired, "original");
        var result = await NewFileWriter.WriteAsync(desired, (stream, token) =>
            stream.WriteAsync("new"u8.ToArray().AsMemory(), token).AsTask());
        Assert.Equal("image 2.png", Path.GetFileName(result));
        Assert.Equal("original", await File.ReadAllTextAsync(desired));
        Assert.Equal("new", await File.ReadAllTextAsync(result));
    }

    [Fact]
    public async Task ConcurrentNewFilesReceiveDistinctNames()
    {
        var desired = Path.Combine(_root, "image.png");
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
            NewFileWriter.WriteAsync(desired, (stream, token) =>
                stream.WriteAsync(new byte[] { (byte)index }.AsMemory(), token).AsTask())));
        Assert.Equal(12, results.Distinct(StringComparer.Ordinal).Count());
        for (var index = 0; index < results.Length; index++)
            Assert.Equal(new byte[] { (byte)index }, await File.ReadAllBytesAsync(results[index]));
        Assert.Empty(Directory.GetFiles(_root, ".MacExplorer-create-*"));
    }

    [Fact]
    public async Task FailedWriteNeverExposesAPartialFinalFile()
    {
        var desired = Path.Combine(_root, "image.png");
        await Assert.ThrowsAsync<IOException>(() => NewFileWriter.WriteAsync(desired, async (stream, token) =>
        {
            await stream.WriteAsync(new byte[] { 1, 2, 3 }.AsMemory(), token);
            throw new IOException("simulated write failure");
        }));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task CancellationBeforeCommitRemovesStagingOnly()
    {
        using var cancellation = new CancellationTokenSource();
        var desired = Path.Combine(_root, "image.png");
        await File.WriteAllTextAsync(desired, "original");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewFileWriter.WriteAsync(desired, async (stream, token) =>
        {
            await stream.WriteAsync(new byte[] { 1 }.AsMemory(), token);
            cancellation.Cancel();
        }, cancellation.Token));
        Assert.Equal("original", await File.ReadAllTextAsync(desired));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData("image.png", "image 2.png")]
    [InlineData(".env", ".env 2")]
    [InlineData("README", "README 2")]
    [InlineData("archive.tar.gz", "archive.tar 2.gz")]
    public void CollisionNamesPreserveExtensionsAndDotfiles(string input, string expected)
        => Assert.Equal(expected, NewFileWriter.CandidateName(input, 2));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidLaterSourceDoesNotCommitTheValidFirstOutput(bool foreignSource)
    {
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        var first = Path.Combine(_root, "first.txt");
        var second = Path.Combine(_root, "second.txt");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");
        var a = Path.Combine(work, "a.txt");
        var b = Path.Combine(work, "b.txt");
        await File.WriteAllTextAsync(a, "a");
        await File.WriteAllTextAsync(b, "b");
        await Assert.ThrowsAsync<InvalidDataException>(() => PluginOutputCommitter.CommitAsync(
            [new(a, "first-copy.txt") { SourcePath = first },
             new(b, "second-copy.txt") { SourcePath = foreignSource ? Path.Combine(_root, "foreign.txt") : null }],
            [new PluginFile(first), new PluginFile(second)], work, default));
        Assert.False(File.Exists(Path.Combine(_root, "first-copy.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "second-copy.txt")));
        Assert.Empty(Directory.GetFiles(_root, ".MacExplorer-create-*"));
    }
}
