using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using MacExplorer.Services.Markdown;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "Markdown")]
public sealed class ReviewMarkdownCodeSpanTests
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData("alpha", "`alpha`")]
    [InlineData("a`b", "``a`b``")]
    [InlineData("`alpha`", "`` `alpha` ``")]
    [InlineData("a``b", "```a``b```")]
    [InlineData("`", "`` ` ``")]
    [InlineData(" leading ", "`  leading  `")]
    [InlineData(" ", "` `")]
    [InlineData("alpha ", "`alpha `")]
    [InlineData("\talpha\t", "`\talpha\t`")]
    public void InlineCodePreservesTheSelectedContentAndOneStepUndo(string selected, string expected)
    {
        const string before = "before ";
        const string after = " after";
        var original = before + selected + after;
        var editor = new TextEditor { Document = new TextDocument(original) };
        editor.SelectionStart = before.Length;
        editor.SelectionLength = selected.Length;

        MarkdownEditing.Apply(editor, "Code", "\n");

        Assert.Equal(before + expected + after, editor.Text);
        Assert.Equal(selected, editor.SelectedText);
        editor.Undo();
        Assert.Equal(original, editor.Text);
        editor.Redo();
        Assert.Equal(before + expected + after, editor.Text);
    }

    [AvaloniaFact(Explicit = true)]
    public void InlineCodeAtAnEmptySelectionKeepsTheEditablePlaceholder()
    {
        var editor = new TextEditor { Document = new TextDocument() };
        MarkdownEditing.Apply(editor, "Code", "\n");
        Assert.Equal("`code`", editor.Text);
        Assert.Equal("code", editor.SelectedText);
        editor.Undo();
        Assert.Equal(string.Empty, editor.Text);
    }

    [AvaloniaFact(Explicit = true)]
    public void ReadOnlyCodeIsNotMutated()
    {
        var editor = new TextEditor { Document = new TextDocument("`keep`"), IsReadOnly = true };
        editor.SelectAll();
        MarkdownEditing.Apply(editor, "Code", "\n");
        Assert.Equal("`keep`", editor.Text);
    }

    [AvaloniaFact(Explicit = true)]
    public void FencedCodeStillUsesAtLeastThreeBackticksAndPreservesCrLf()
    {
        var editor = new TextEditor { Document = new TextDocument("a```b") };
        editor.SelectAll();
        MarkdownEditing.Apply(editor, "CodeBlock", "\r\n");
        Assert.Equal("````\r\na```b\r\n````", editor.Text);
        editor.Undo();
        Assert.Equal("a```b", editor.Text);
    }

}
