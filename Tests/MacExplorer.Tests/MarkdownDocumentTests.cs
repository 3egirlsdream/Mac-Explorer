using System.Text;
using MacExplorer.Services.Markdown;
using Xunit;

namespace MacExplorer.Tests;

public sealed class MarkdownDocumentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MacExplorer-Markdown-Tests-" + Guid.NewGuid().ToString("N"));
    public MarkdownDocumentTests() => Directory.CreateDirectory(_root);
    private string FilePath(string name = "test.md") => Path.Combine(_root, name);

    [Theory]
    [InlineData("readme.md", true)]
    [InlineData("README.MD", true)]
    [InlineData("notes.markdown", false)]
    [InlineData("notes.mdx", false)]
    [InlineData("notes.txt", false)]
    public void ExtensionScopeIsExplicit(string name, bool expected) => Assert.Equal(expected, MarkdownDocument.IsMarkdown(name));

    [Theory]
    [InlineData(8, false, false)]
    [InlineData(8, false, true)]
    [InlineData(16, false, true)]
    [InlineData(16, true, true)]
    [InlineData(32, false, true)]
    [InlineData(32, true, true)]
    public async Task SavePreservesEncodingBomAndLineEndings(int width, bool bigEndian, bool bom)
    {
        Encoding encoding = width switch
        {
            16 => new UnicodeEncoding(bigEndian, bom, true),
            32 => new UTF32Encoding(bigEndian, bom, true),
            _ => new UTF8Encoding(bom, true)
        };
        const string original = "# 中文\r\n\r\n原文\r\n";
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(original)];
        await File.WriteAllBytesAsync(FilePath(), bytes);
        var document = await MarkdownDocument.OpenAsync(FilePath());
        Assert.Equal(original, document.Text);
        Assert.Equal("\r\n", document.NewLine);
        var expected = original + "- 新内容\r\n";
        var saved = await document.SaveAsync(expected);
        Assert.Equal(expected, saved.Text);
        Assert.Equal(original, document.Text); // Sessions represent immutable disk revisions.
        byte[] expectedBytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(expected)];
        Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(FilePath()));
        Assert.Empty(Directory.EnumerateFiles(_root, ".mac-explorer-md-*.tmp"));
    }

    [Fact]
    public async Task ExternalModificationWithSameLengthAndTimestampIsNotOverwritten()
    {
        await File.WriteAllTextAsync(FilePath(), "original");
        var document = await MarkdownDocument.OpenAsync(FilePath());
        var stamp = File.GetLastWriteTimeUtc(FilePath());
        await File.WriteAllTextAsync(FilePath(), "external");
        File.SetLastWriteTimeUtc(FilePath(), stamp);
        await Assert.ThrowsAsync<MarkdownFileChangedException>(() => document.SaveAsync("my edit"));
        Assert.Equal("external", await File.ReadAllTextAsync(FilePath()));
        Assert.Empty(Directory.EnumerateFiles(_root, ".mac-explorer-md-*.tmp"));
    }

    [Fact]
    public async Task ExternallyDeletedFileIsNotRecreated()
    {
        await File.WriteAllTextAsync(FilePath(), "original");
        var document = await MarkdownDocument.OpenAsync(FilePath());
        File.Delete(FilePath());
        await Assert.ThrowsAsync<MarkdownFileChangedException>(() => document.SaveAsync("my edit"));
        Assert.False(File.Exists(FilePath()));
    }

    [Fact]
    public async Task SaveCopyDoesNotChangeTheOriginal()
    {
        await File.WriteAllTextAsync(FilePath(), "original");
        var document = await MarkdownDocument.OpenAsync(FilePath());
        var copy = await document.SaveCopyAsync(FilePath("copy.md"), "copy");
        Assert.Equal("original", await File.ReadAllTextAsync(FilePath()));
        Assert.Equal("copy", await File.ReadAllTextAsync(copy.FilePath));
        var updated = await copy.SaveAsync("updated copy");
        Assert.Equal("updated copy", updated.Text);
    }

    [Fact]
    public async Task CancellationDoesNotChangeTheFile()
    {
        await File.WriteAllTextAsync(FilePath(), "original");
        var document = await MarkdownDocument.OpenAsync(FilePath());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => document.SaveAsync("new", cancellation.Token));
        Assert.Equal("original", await File.ReadAllTextAsync(FilePath()));
        Assert.Empty(Directory.EnumerateFiles(_root, ".mac-explorer-md-*.tmp"));
    }

    [Fact]
    public async Task EmptyMarkdownCanBeSaved()
    {
        await File.WriteAllTextAsync(FilePath(), string.Empty);
        var document = await MarkdownDocument.OpenAsync(FilePath());
        Assert.Equal(string.Empty, document.Text);
        await document.SaveAsync(string.Empty);
        Assert.Empty(await File.ReadAllBytesAsync(FilePath()));
    }

    [Fact]
    public async Task InvalidEncodingIsRejectedWithoutChangingTheBytes()
    {
        byte[] bytes = [0xff, 0x61];
        await File.WriteAllBytesAsync(FilePath(), bytes);
        await Assert.ThrowsAsync<IOException>(() => MarkdownDocument.OpenAsync(FilePath()));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(FilePath()));
    }

    [Fact]
    public async Task LargeFilesAreRejectedBeforeRendering()
    {
        await File.WriteAllBytesAsync(FilePath(), new byte[MarkdownDocument.MaxReadBytes + 1]);
        await Assert.ThrowsAsync<IOException>(() => MarkdownDocument.OpenAsync(FilePath()));
        await Assert.ThrowsAsync<IOException>(() => MarkdownDocument.ReadPreviewAsync(FilePath(), CancellationToken.None));
    }

    [Fact]
    public async Task SymbolicLinkIsPreservedOnSave()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = FilePath("target.md");
        await File.WriteAllTextAsync(target, "original");
        var link = FilePath("link.md");
        File.CreateSymbolicLink(link, target);
        var document = await MarkdownDocument.OpenAsync(link);
        await document.SaveAsync("edited");
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal("edited", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task RetargetedSymbolicLinkIsNotOverwritten()
    {
        if (OperatingSystem.IsWindows()) return;
        var first = FilePath("first.md");
        var second = FilePath("second.md");
        await File.WriteAllTextAsync(first, "same");
        await File.WriteAllTextAsync(second, "same");
        File.CreateSymbolicLink(FilePath(), first);
        var document = await MarkdownDocument.OpenAsync(FilePath());
        File.Delete(FilePath());
        File.CreateSymbolicLink(FilePath(), second);
        await Assert.ThrowsAsync<MarkdownFileChangedException>(() => document.SaveAsync("edited"));
        Assert.Equal("same", await File.ReadAllTextAsync(second));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
