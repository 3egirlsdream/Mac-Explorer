using MacExplorer.Platforms.MacCatalyst.Services;
using Xunit;

namespace MacExplorer.Tests;

public sealed class ReviewLocalMoveContractTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("macexplorer-move-review-").FullName;
    private readonly MacFileService _files = new();
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExistingDestinationIsNotReportedAsSuccessfulMove(bool sourceDirectory, bool targetDirectory)
    {
        var sourceParent = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(_root, "destination")).FullName;
        var source = Path.Combine(sourceParent, "item");
        var target = Path.Combine(destination, "item");
        if (sourceDirectory) Directory.CreateDirectory(source);
        else await File.WriteAllTextAsync(source, "source content");
        if (targetDirectory) Directory.CreateDirectory(target);
        else await File.WriteAllTextAsync(target, "destination content");
        await Assert.ThrowsAnyAsync<IOException>(() => _files.MoveAsync(source, destination));
        Assert.True(sourceDirectory ? Directory.Exists(source) : File.Exists(source));
        Assert.True(targetDirectory ? Directory.Exists(target) : File.Exists(target));
        if (!sourceDirectory) Assert.Equal("source content", await File.ReadAllTextAsync(source));
        if (!targetDirectory) Assert.Equal("destination content", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task MissingSourceCannotReportMoveOrRenameSuccess()
    {
        var destination = Directory.CreateDirectory(Path.Combine(_root, "destination")).FullName;
        var missing = Path.Combine(_root, "missing.txt");
        await Assert.ThrowsAsync<FileNotFoundException>(() => _files.MoveAsync(missing, destination));
        await Assert.ThrowsAsync<FileNotFoundException>(() => _files.RenameAsync(missing, "new.txt"));
    }

    [Fact]
    public async Task ExplicitOverwriteStillReplacesAnExistingFile()
    {
        var source = Path.Combine(_root, "item.txt");
        var destination = Directory.CreateDirectory(Path.Combine(_root, "destination")).FullName;
        var target = Path.Combine(destination, "item.txt");
        await File.WriteAllTextAsync(source, "new content");
        await File.WriteAllTextAsync(target, "old content");
        await _files.MoveAsync(source, destination, overwrite: true);
        Assert.False(File.Exists(source));
        Assert.Equal("new content", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task ExplicitDirectoryMergeStillMovesAndOverwritesItsFiles()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source", "folder")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(_root, "destination")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(destination, "folder")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "same.txt"), "new");
        await File.WriteAllTextAsync(Path.Combine(target, "same.txt"), "old");
        await File.WriteAllTextAsync(Path.Combine(target, "keep.txt"), "keep");
        await _files.MoveAsync(source, destination, overwrite: true);
        Assert.False(Directory.Exists(source));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(target, "same.txt")));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(target, "keep.txt")));
    }
}
