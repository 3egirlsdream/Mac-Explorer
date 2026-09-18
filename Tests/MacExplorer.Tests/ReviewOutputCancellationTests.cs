using MacExplorer.Models;
using MacExplorer.ViewModels;
using MacExplorer.PluginSdk;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Plugins;
using MacExplorer.Services.Search;
using Xunit;

namespace MacExplorer.Tests;

public sealed class ReviewOutputCancellationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("macexplorer-output-review-").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private async Task<(string Work, string Source, PluginOutput[] Outputs)> Prepare()
    {
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        var source = Path.Combine(_root, "source.txt");
        await File.WriteAllTextAsync(source, "original source");
        var a = Path.Combine(work, "a.txt");
        var b = Path.Combine(work, "b.txt");
        await File.WriteAllTextAsync(a, "complete first output");
        await File.WriteAllTextAsync(b, "complete second output");
        return (work, source, [new(a, "first.txt"), new(b, "second.txt")]);
    }

    [Fact]
    public async Task CancellationAfterFirstCommitStopsRemainingWritesAndPreservesCompleteOutput()
    {
        var (work, source, outputs) = await Prepare();
        using var cancellation = new CancellationTokenSource();
        var committed = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PluginOutputCommitter.CommitAsync(
            outputs, [new PluginFile(source)], work, cancellation.Token, path =>
            {
                committed.Add(path);
                cancellation.Cancel();
            }));
        Assert.Equal("complete first output", await File.ReadAllTextAsync(Assert.Single(committed)));
        Assert.False(File.Exists(Path.Combine(_root, "second.txt")));
        Assert.Equal("original source", await File.ReadAllTextAsync(source));
        Assert.Empty(Directory.GetFiles(_root, ".MacExplorer-create-*"));
    }

    [Fact]
    public async Task LaterIoFailureStillReportsTheDurablePrefix()
    {
        var (work, source, outputs) = await Prepare();
        var committed = new List<string>();
        await Assert.ThrowsAsync<FileNotFoundException>(() => PluginOutputCommitter.CommitAsync(
            outputs, [new PluginFile(source)], work, default, path =>
            {
                committed.Add(path);
                File.Delete(outputs[1].Path);
            }));
        Assert.Equal("complete first output", await File.ReadAllTextAsync(Assert.Single(committed)));
        Assert.False(File.Exists(Path.Combine(_root, "second.txt")));
        Assert.Empty(Directory.GetFiles(_root, ".MacExplorer-create-*"));
    }

    [Fact]
    public async Task InvalidLaterOutputIsRejectedBeforeAnyCommitOrCallback()
    {
        var (work, source, outputs) = await Prepare();
        outputs[1] = outputs[1] with { SuggestedName = "../escape.txt" };
        var committed = new List<string>();
        await Assert.ThrowsAsync<InvalidDataException>(() => PluginOutputCommitter.CommitAsync(
            outputs, [new PluginFile(source)], work, default, committed.Add));
        Assert.Empty(committed);
        Assert.False(File.Exists(Path.Combine(_root, "first.txt")));
    }

    [Fact]
    public async Task CancellationNeverRemovesAnExistingNameCollision()
    {
        var (work, source, outputs) = await Prepare();
        var collision = Path.Combine(_root, "first.txt");
        await File.WriteAllTextAsync(collision, "existing user's file");
        using var cancellation = new CancellationTokenSource();
        var committed = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PluginOutputCommitter.CommitAsync(
            outputs, [new PluginFile(source)], work, cancellation.Token, path =>
            {
                committed.Add(path);
                cancellation.Cancel();
            }));
        Assert.NotEqual(collision, Assert.Single(committed));
        Assert.Equal("existing user's file", await File.ReadAllTextAsync(collision));
        Assert.Equal("complete first output", await File.ReadAllTextAsync(committed[0]));
    }

    [Fact]
    public async Task StagingIsHiddenEvenWhenDotFilesAreShown()
    {
        var options = new SearchOptions(HideSystemFiles: false, HideDotFiles: false, HideDotFolders: false);
        var destination = Path.Combine(_root, "final.txt");
        string? staging = null;
        var result = await NewFileWriter.WriteAsync(destination, async (stream, token) =>
        {
            staging = Assert.IsType<FileStream>(stream).Name;
            Assert.EndsWith(".fkfinder-tmp", staging);
            Assert.False(options.IsVisible(staging, Path.GetFileName(staging), false, _root));
            var listFilter = new SortFilterViewModel { HideSystemFiles = false, HideDotFiles = false, HideDotFolders = false };
            Assert.False(listFilter.PassesVisibilityFilter(new FileSystemEntry { FullPath = staging, Name = Path.GetFileName(staging) }));
            await stream.WriteAsync("complete"u8.ToArray().AsMemory(), token);
        });
        Assert.Equal(destination, result);
        Assert.True(options.IsVisible(result, Path.GetFileName(result), false, _root));
        Assert.False(File.Exists(staging));
        Assert.Equal("complete", await File.ReadAllTextAsync(result));
    }
}
