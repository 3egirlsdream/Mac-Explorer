using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using MacExplorer.Services.Markdown;
using Xunit;

namespace MacExplorer.Tests;

public sealed class MarkdownEditingTests
{
    [Theory]
    [InlineData("", 0, "\n")]
    [InlineData("- item", 6, "\n- ")]
    [InlineData("- [x] done", 10, "\n- [ ] ")]
    [InlineData("8. item", 7, "\n9. ")]
    [InlineData("8) item", 7, "\n9) ")]
    [InlineData("> quote", 7, "\n> ")]
    [InlineData("9223372036854775807. item", 25, "\n")]
    public void SmartNewLineContinuesCommonMarkdown(string text, int caret, string expected)
    {
        var change = MarkdownSmartNewLine.CreateChange(text, caret, 0);
        Assert.Equal(expected, change.Text);
    }

    [Fact]
    public void SmartNewLineAtOffsetZeroDoesNotReadBeforeTheDocument()
    {
        var change = MarkdownSmartNewLine.CreateChange("\ntext", 0, 0);
        Assert.Equal(0, change.Start);
        Assert.Equal("\n", change.Text);
    }

    [Theory]
    [InlineData("- ")]
    [InlineData("- [ ] ")]
    [InlineData("2. ")]
    [InlineData("> ")]
    public void EmptyListOrQuoteExitsThePrefix(string text)
    {
        var change = MarkdownSmartNewLine.CreateChange(text, text.Length, 0, "\r\n");
        Assert.Equal(0, change.Start);
        Assert.Equal(text.Length, change.Length);
        Assert.Equal("\r\n", change.Text);
    }

    [AvaloniaFact]
    public void BoldIsOneUndoableDocumentEdit()
    {
        var editor = new TextEditor { Document = new TextDocument("Hello world") };
        editor.SelectionStart = 6;
        editor.SelectionLength = 5;
        MarkdownEditing.Apply(editor, "Bold", "\n");
        Assert.Equal("Hello **world**", editor.Text);
        editor.Undo();
        Assert.Equal("Hello world", editor.Text);
        editor.Redo();
        Assert.Equal("Hello **world**", editor.Text);
    }

    [AvaloniaFact]
    public void MultilinePrefixPreservesCrLfAndDoesNotIncludeTheNextUnselectedLine()
    {
        const string original = "alpha\r\nbeta\r\ngamma";
        var editor = new TextEditor { Document = new TextDocument(original) };
        editor.SelectionStart = 0;
        editor.SelectionLength = "alpha\r\nbeta\r\n".Length;
        MarkdownEditing.Apply(editor, "Bullet", "\r\n");
        Assert.Equal("- alpha\r\n- beta\r\ngamma", editor.Text);
        editor.Undo();
        Assert.Equal(original, editor.Text);
    }

    [AvaloniaFact]
    public void EmptyDocumentCanBeFormatted()
    {
        var editor = new TextEditor { Document = new TextDocument() };
        MarkdownEditing.Apply(editor, "H1", "\n");
        Assert.Equal("# ", editor.Text);
        editor.Undo();
        Assert.Equal(string.Empty, editor.Text);
    }

    [AvaloniaFact]
    public void CodeFenceGrowsPastBackticksInsideTheSelection()
    {
        var editor = new TextEditor { Document = new TextDocument("```inside```") };
        editor.SelectAll();
        MarkdownEditing.Apply(editor, "CodeBlock", "\n");
        Assert.Equal("````\n```inside```\n````", editor.Text);
    }

    [Fact]
    public void IndentationDoesNotContinueMarkdownListsInsideCodeFences()
    {
        var document = new TextDocument("```\n- code\n");
        new MarkdownIndentationStrategy().IndentLine(document, document.GetLineByNumber(3));
        Assert.Equal("```\n- code\n", document.Text);
    }

    [AvaloniaFact]
    public void HighlightingDefinitionsLoadInBothThemes()
    {
        Assert.NotNull(MarkdownHighlighting.Create(false).GetNamedColor("Heading"));
        Assert.NotNull(MarkdownHighlighting.Create(true).GetNamedColor("Heading"));
    }
}
