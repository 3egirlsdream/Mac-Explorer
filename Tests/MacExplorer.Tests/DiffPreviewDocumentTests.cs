using System.Text;
using MacExplorer.Services;
using MacExplorer.Services.Preview;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Area", "Preview")]
[Trait("Kind", "Unit")]
public sealed class DiffPreviewDocumentTests
{
    [Theory]
    [InlineData("a.diff")]
    [InlineData("a.PATCH")]
    [InlineData("a.DiFf")]
    public void DiffRecognitionDoesNotChangePluginTextMatching(string path)
    {
        Assert.True(DiffPreviewDocument.IsDiff(path));
        Assert.False(TextFileTypes.IsText(path));
        Assert.False(DiffPreviewDocument.IsDiff(path + ".txt"));
    }

    [Fact]
    public void GitPatchKeepsRawTextAndDisambiguatesHeaderLikeContentAcrossFiles()
    {
        const string text = "diff --git a/a b/a\r\n--- a/a\r\n+++ b/a\r\n@@ -1,2 +1,2 @@ function\r\n--- 删除的内容\r\n+++ 新增的内容\r\n 中文\t  \r\n@@ -10 +10 @@\r\n-old\r\n+new\r\n\\ No newline at end of file\r\ndiff --git a/b b/b\r\n--- /dev/null\r\n+++ b/b\r\n@@ -0,0 +1 @@\r\n+新文件\r\ndiff --git a/c b/c\r\n--- a/c\r\n+++ /dev/null\r\n@@ -1 +0,0 @@\r\n-删除文件\r\n";
        var document = DiffPreviewDocument.Parse(text);
        Assert.Equal(text, document.Text);
        Assert.Equal(new[]
        {
            DiffLineKind.Header, DiffLineKind.Header, DiffLineKind.Header, DiffLineKind.Hunk,
            DiffLineKind.Removed, DiffLineKind.Added, DiffLineKind.Unchanged, DiffLineKind.Hunk,
            DiffLineKind.Removed, DiffLineKind.Added, DiffLineKind.Context,
            DiffLineKind.Header, DiffLineKind.Header, DiffLineKind.Header, DiffLineKind.Hunk, DiffLineKind.Added,
            DiffLineKind.Header, DiffLineKind.Header, DiffLineKind.Header, DiffLineKind.Hunk, DiffLineKind.Removed
        }, document.Lines);
    }

    [Fact]
    public void UnifiedPatchHeadersAfterCompletedHunkAreHeaders()
    {
        var document = DiffPreviewDocument.Parse("--- old\n+++ new\n@@ -1 +1 @@\n-old\n+new\n--- old2\n+++ new2\n");
        Assert.Equal(DiffLineKind.Removed, document.Lines[3]);
        Assert.Equal(DiffLineKind.Added, document.Lines[4]);
        Assert.Equal(DiffLineKind.Header, document.Lines[5]);
        Assert.Equal(DiffLineKind.Header, document.Lines[6]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Unknown patch format\n+plain\n-plain\n")]
    [InlineData("@@ -1,99999999999999 +1 @@\n+unknown\n")]
    public void EmptyOrUnsupportedContentRemainsReadable(string text)
    {
        var document = DiffPreviewDocument.Parse(text);
        Assert.Equal(text, document.Text);
        Assert.Null(document.Comparison);
    }

    [Fact]
    public void ComparisonAlignsUnequalChangeRunsAndOriginalNumbersAcrossHunks()
    {
        var document = DiffPreviewDocument.Parse("--- a/old\n+++ b/new\n@@ -10,4 +20,3 @@\n same\n-one\n-two\n+new\n end\n@@ -100,0 +200,2 @@\n+added\n+\n");
        var comparison = Assert.IsType<DiffComparison>(document.Comparison);
        Assert.Equal(comparison.Before.Length, comparison.After.Length);
        Assert.Equal(new long?[] { null, null, 10, 11, 12, 13, null, null, null }, comparison.Before.Select(line => line?.Number));
        Assert.Equal(new long?[] { null, null, 20, 21, null, 22, null, 200, 201 }, comparison.After.Select(line => line?.Number));
        Assert.Equal("a/old", comparison.Before[0]!.Text);
        Assert.Equal("b/new", comparison.After[0]!.Text);
        Assert.Equal("one", comparison.Before[3]!.Text);
        Assert.Equal("new", comparison.After[3]!.Text);
        Assert.Null(comparison.After[4]);
        Assert.Null(comparison.Before[8]);
        Assert.Equal("", comparison.After[8]!.Text);
        Assert.Equal("one\ntwo\nend\n@@ -100,0 +200,2 @@\n", DiffComparison.Copy(comparison.Before, 3, 0, 9, 0));
        Assert.Equal("new\nend\n@@ -100,0 +200,2 @@\nadded\n\n", DiffComparison.Copy(comparison.After, 3, 0, 9, 0));
        Assert.Equal("ne", DiffComparison.Copy(comparison.Before, 3, 1, 3, 3));
    }

    [Fact]
    public void NoNewlineAnnotationsKeepChangedRowsPairedAndCopyWithoutSyntheticNewlines()
    {
        var comparison = DiffPreviewDocument.Parse("@@ -5 +8 @@\r\n-before\r\n\\ No newline at end of file\r\n+after\r\n\\ No newline at end of file\r\n").Comparison!;
        Assert.Equal(2, comparison.Before.Length);
        Assert.Equal("before", DiffComparison.Copy(comparison.Before, 1, 0, 2, 0));
        Assert.Equal("after", DiffComparison.Copy(comparison.After, 1, 0, 2, 0));
        Assert.Equal(5, comparison.Before[1]!.Number);
        Assert.Equal(8, comparison.After[1]!.Number);
    }

    [Fact]
    public void ComparisonSeparatesFilesAndDisplaysWholeFileAdditionsAndDeletions()
    {
        const string patch = "--- /dev/null\n+++ b/new\n@@ -0,0 +1,2 @@\n+first\n+second\n--- a/deleted\n+++ /dev/null\n@@ -40 +0,0 @@\n-old\n";
        var comparison = DiffPreviewDocument.Parse(patch).Comparison!;
        Assert.Equal(new long?[] { null, null, null, null, null, null, 40 }, comparison.Before.Select(line => line?.Number));
        Assert.Equal(new long?[] { null, null, 1, 2, null, null, null }, comparison.After.Select(line => line?.Number));
        Assert.Equal("first", comparison.After[2]!.Text);
        Assert.Equal("old", comparison.Before[6]!.Text);
    }

    [Theory]
    [InlineData("@@ -1,2 +1 @@\n-one\n+new\n")]
    [InlineData("@@ -1 +1 @@\n-old\n@@ -5 +5 @@\n-a\n+b\n")]
    [InlineData("@@@ -1,1 -1,1 +1,1 @@@\n-combined\n")]
    public void IncompleteOrCombinedPatchesFallBackToTheirOriginalText(string patch)
    {
        var document = DiffPreviewDocument.Parse(patch);
        Assert.Null(document.Comparison);
        Assert.Equal(patch, document.Text);
    }

    [Fact]
    public void LargePatchKeepsRawPreviewWithoutExpandingComparisonDocuments()
    {
        var patch = "@@ -0,0 +1,100001 @@\n" + string.Concat(Enumerable.Repeat("+x\n", 100001));
        var document = DiffPreviewDocument.Parse(patch);
        Assert.Null(document.Comparison);
        Assert.Equal(patch, document.Text);
    }

    [Theory]
    [InlineData("utf-8", true)]
    [InlineData("utf-8", false)]
    [InlineData("utf-16", true)]
    [InlineData("utf-16BE", true)]
    public async Task ReaderKeepsEncodingWhitespaceAndNewlines(string encodingName, bool bom)
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_DiffReader_");
        const string text = "--- 旧\r\n+++ 新\r\n@@ -1 +1 @@\r\n-旧\t  \r\n+新\t  \r\n";
        try
        {
            var encoding = Encoding.GetEncoding(encodingName);
            var path = Path.Combine(root.FullName, "test.patch");
            await File.WriteAllBytesAsync(path, (bom ? encoding.GetPreamble() : []).Concat(encoding.GetBytes(text)).ToArray());
            Assert.Equal(text, (await DiffPreviewDocument.ReadAsync(path, CancellationToken.None)).Text);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task ReaderAcceptsLimitRejectsOversizeMissingAndCancelledReads()
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_DiffLimit_");
        var path = Path.Combine(root.FullName, "test.diff");
        try
        {
            await File.WriteAllTextAsync(path, new string('x', TextPreviewReader.MaxBytes));
            Assert.Equal(TextPreviewReader.MaxBytes, (await TextPreviewReader.ReadAsync(path, CancellationToken.None)).Length);
            await File.AppendAllTextAsync(path, "x");
            await Assert.ThrowsAsync<TextPreviewTooLargeException>(() => DiffPreviewDocument.ReadAsync(path, CancellationToken.None));
            await Assert.ThrowsAsync<FileNotFoundException>(() => DiffPreviewDocument.ReadAsync(path + ".missing", CancellationToken.None));
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiffPreviewDocument.ReadAsync(path, cts.Token));
            Assert.ThrowsAny<OperationCanceledException>(() => DiffPreviewDocument.Parse("@@ -0,0 +1 @@\n+x", cts.Token));
        }
        finally { root.Delete(true); }
    }
}
