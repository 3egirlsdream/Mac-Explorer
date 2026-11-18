using MacExplorer.Controls;
using MacExplorer.Models;
using Xunit;

namespace MacExplorer.UnitTests;

[Trait("Kind", "Unit")]
[Trait("Area", "FileList")]
public sealed class FileNameTypeAheadTests
{
    private static FileSystemEntry[] Rows(params string[] names) =>
        names.Select(name => new FileSystemEntry { Name = name }).ToArray();

    [Fact]
    public void FirstAndRepeatedLetterStartAfterCurrentAndWrap()
    {
        var rows = Rows("Alpha", "Beta", "apricot");
        var matcher = new FileNameTypeAhead();

        Assert.Equal(2, matcher.Find(rows, 0, "A", 0));
        Assert.Equal(0, matcher.Find(rows, 2, "a", 100));
        Assert.Equal(2, matcher.Find(rows, 0, "A", 200));
    }

    [Fact]
    public void ExtensionChecksCurrentThenOtherRowsUsingTimeSinceLastInput()
    {
        var rows = Rows("Alpine", "Alpha");
        var matcher = new FileNameTypeAhead();

        Assert.Equal(0, matcher.Find(rows, -1, "a", 0));
        Assert.Equal(0, matcher.Find(rows, 0, "L", 999));
        Assert.Equal(1, matcher.Find(rows, 0, "ph", 1_998));
    }

    [Fact]
    public void UnmatchedExtensionStartsANewPrefixAndRepeatedLettersStillCycle()
    {
        var rows = Rows("Alpha", "Beta", "Bravo", "Charlie");
        var matcher = new FileNameTypeAhead();

        Assert.Equal(0, matcher.Find(rows, -1, "a", 0));
        Assert.Equal(1, matcher.Find(rows, 0, "b", 100));
        Assert.Equal(2, matcher.Find(rows, 1, "B", 200));
        Assert.Equal(-1, matcher.Find(rows, 2, "x", 300));
        Assert.Equal(3, matcher.Find(rows, 2, "c", 400));
        Assert.Equal(0, matcher.Find(rows, 3, "a", 500));
    }

    [Fact]
    public void UnmatchedExtensionCanStartFromTheCurrentRowAfterWrapping()
    {
        var rows = Rows("Alpha", "Beta");
        var matcher = new FileNameTypeAhead();

        Assert.Equal(0, matcher.Find(rows, -1, "a", 0));
        Assert.Equal(1, matcher.Find(rows, 1, "b", 100));
    }

    [Fact]
    public void OneSecondOfInactivityStartsANewPrefix()
    {
        var rows = Rows("Alpha", "Lime");
        var matcher = new FileNameTypeAhead();

        Assert.Equal(0, matcher.Find(rows, -1, "a", 0));
        Assert.Equal(1, matcher.Find(rows, 0, "l", 1_000));
    }

    [Fact]
    public void ResetDropsPrefixWithoutWaitingForTimeout()
    {
        var rows = Rows("Alpha", "Beta");
        var matcher = new FileNameTypeAhead();

        Assert.Equal(0, matcher.Find(rows, -1, "a", 100));
        matcher.Reset();
        Assert.Equal(1, matcher.Find(rows, 0, "b", 100));
    }

    [Fact]
    public void MatchesOnlyVisibleNamePrefixes()
    {
        FileSystemEntry[] rows = [new() { Name = "Editor.app", IconKey = "app-bundle" }];
        var matcher = new FileNameTypeAhead();

        Assert.Equal(0, matcher.Find(rows, -1, "edITOR", 0));
        Assert.Equal(-1, matcher.Find(rows, 0, "editor.app", 1_000));
        Assert.Equal(-1, matcher.Find(rows, 0, "dit", 2_000));
        Assert.Equal(-1, matcher.Find([], -1, "e", 3_000));
    }
}
