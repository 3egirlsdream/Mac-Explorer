// Core selection/formatting operations adapted from Vex's MarkdownEditorMutationService (MIT).
// Use TextDocument.Replace instead of assigning TextEditor.Text: formatting remains one undoable edit.
using System.Text.RegularExpressions;
using AvaloniaEdit;

namespace MacExplorer.Services.Markdown;

internal static class MarkdownEditing
{
    private static readonly Regex LinePrefix = new(
        @"^(?<indent>[ \t]*)(?:#{1,6}[ \t]+|>[ \t]?|[-+*][ \t]+\[[ xX]\][ \t]+|[-+*][ \t]+|[0-9]+[.)][ \t]+)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static void Apply(TextEditor editor, string action, string newLine)
    {
        if (editor.IsReadOnly) return;
        switch (action)
        {
            case "Bold": Wrap(editor, "**", "**", "粗体文字"); break;
            case "Italic": Wrap(editor, "*", "*", "斜体文字"); break;
            case "Strike": Wrap(editor, "~~", "~~", "删除线文字"); break;
            case "Code": InsertInlineCode(editor); break;
            case "Link": InsertLink(editor, false); break;
            case "Image": InsertLink(editor, true); break;
            case "Quote": PrefixLines(editor, "> "); break;
            case "Bullet": PrefixLines(editor, "- "); break;
            case "Numbered": PrefixLines(editor, "1. "); break;
            case "Task": PrefixLines(editor, "- [ ] "); break;
            case "H1": case "H2": case "H3": case "H4": case "H5": case "H6":
                PrefixLines(editor, new string('#', action[1] - '0') + " "); break;
            case "CodeBlock": InsertCodeBlock(editor, newLine); break;
            case "Table": InsertTable(editor, newLine); break;
            case "Rule": ReplaceSelection(editor, newLine + "---" + newLine); break;
        }
        editor.Focus();
    }

    internal static void Wrap(TextEditor editor, string prefix, string suffix, string placeholder)
    {
        var selected = editor.SelectionLength == 0 ? placeholder : editor.SelectedText;
        var start = editor.SelectionStart;
        Replace(editor, start, editor.SelectionLength, prefix + selected + suffix,
            start + prefix.Length, selected.Length);
    }

    internal static void ReplaceSelection(TextEditor editor, string replacement)
    {
        var start = editor.SelectionStart;
        Replace(editor, start, editor.SelectionLength, replacement, start + replacement.Length, 0);
    }

    private static void Replace(TextEditor editor, int start, int length, string replacement, int selectionStart, int selectionLength)
    {
        editor.Document.Replace(start, length, replacement);
        // The replace anchors the old selection onto the new text; drop it before repositioning.
        editor.SelectionLength = 0;
        editor.CaretOffset = selectionStart + selectionLength;
        editor.SelectionStart = selectionStart;
        editor.SelectionLength = selectionLength;
    }

    private static void InsertLink(TextEditor editor, bool image)
    {
        var selected = editor.SelectedText;
        var trimmed = selected.Trim();
        var isTarget = !trimmed.Contains('\n') && !trimmed.Contains('\r') &&
            (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "mailto"
             || image && new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp" }
                 .Contains(Path.GetExtension(trimmed), StringComparer.OrdinalIgnoreCase));
        var label = selected.Length > 0 && !isTarget ? selected : image ? "图片说明" : "链接文字";
        var target = isTarget ? trimmed.Replace('\\', '/').Replace(" ", "%20").Replace("(", "%28").Replace(")", "%29")
            : image ? "image.png" : "https://";
        var prefix = image ? "![" : "[";
        var start = editor.SelectionStart;
        var replacement = $"{prefix}{label}]({target})";
        var selectLabel = selected.Length == 0 || isTarget;
        Replace(editor, start, editor.SelectionLength, replacement,
            start + (selectLabel ? prefix.Length : prefix.Length + label.Length + 2),
            selectLabel ? label.Length : target.Length);
    }

    internal static void PrefixLines(TextEditor editor, string prefix)
    {
        var document = editor.Document;
        var first = document.GetLineByOffset(editor.SelectionStart);
        var end = editor.SelectionStart + editor.SelectionLength;
        var last = document.GetLineByOffset(end);
        if (end > editor.SelectionStart && last.Offset == end) last = last.PreviousLine!;
        var start = first.Offset;
        var oldEnd = last.EndOffset;
        var replacements = new List<(int Offset, int Length, string Text)>();
        var number = 1;
        for (var line = first; line != null && line.LineNumber <= last.LineNumber; line = line.NextLine)
        {
            var text = document.GetText(line.Offset, line.Length);
            var clean = LinePrefix.Replace(text, "${indent}", 1);
            var indentLength = clean.TakeWhile(c => c is ' ' or '\t').Count();
            var marker = prefix == "1. " ? $"{number++}. " : prefix;
            replacements.Add((line.Offset, line.Length, clean[..indentLength] + marker + clean[indentLength..]));
        }
        document.BeginUpdate();
        try
        {
            foreach (var item in replacements.AsEnumerable().Reverse())
                document.Replace(item.Offset, item.Length, item.Text);
        }
        finally { document.EndUpdate(); }
        var newLength = oldEnd - start + replacements.Sum(x => x.Text.Length - x.Length);
        editor.CaretOffset = start + newLength;
        editor.SelectionStart = start;
        editor.SelectionLength = newLength;
    }

    private static void InsertInlineCode(TextEditor editor)
    {
        var selected = editor.SelectionLength > 0 ? editor.SelectedText : "code";
        var delimiter = new string('`', BacktickDelimiterLength(selected, 1));
        // Keep edge backticks separate from the delimiters. CommonMark removes
        // one leading/trailing space pair, except when the content is all spaces.
        var needsPadding = selected.StartsWith('`') || selected.EndsWith('`') ||
            selected.StartsWith(' ') && selected.EndsWith(' ') && selected.Any(c => c != ' ');
        var padding = needsPadding ? " " : string.Empty;
        Wrap(editor, delimiter + padding, padding + delimiter, "code");
    }

    private static int BacktickDelimiterLength(string text, int minimum)
    {
        var length = minimum;
        var run = 0;
        foreach (var character in text)
        {
            run = character == '`' ? run + 1 : 0;
            length = Math.Max(length, run + 1);
        }
        return length;
    }

    private static void InsertCodeBlock(TextEditor editor, string newLine)
    {
        var selected = editor.SelectionLength > 0 ? editor.SelectedText : "代码";
        var fence = new string('`', BacktickDelimiterLength(selected, 3));
        Wrap(editor, fence + newLine, newLine + fence, "代码");
    }

    private static void InsertTable(TextEditor editor, string newLine)
    {
        var selected = editor.SelectedText.ReplaceLineEndings("\n");
        var delimiter = selected.Contains('\t') ? '\t' : selected.Contains('|') ? '|' : '\0';
        var rows = selected.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (delimiter == '\0' || rows.Length < 2)
        {
            ReplaceSelection(editor, $"| 标题 1 | 标题 2 |{newLine}| --- | --- |{newLine}| 内容 | 内容 |{newLine}");
            return;
        }
        var cells = rows.Select(row => (delimiter == '|' ? row.Trim().Trim('|') : row)
            .Split(delimiter).Select(cell => cell.Trim().Replace("|", "\\|")).ToArray()).ToArray();
        var columns = cells.Max(row => row.Length);
        string Format(string[] row) => "| " + string.Join(" | ", Enumerable.Range(0, columns)
            .Select(i => i < row.Length ? row[i] : string.Empty)) + " |";
        var lines = new List<string> { Format(cells[0]), Format(Enumerable.Repeat("---", columns).ToArray()) };
        lines.AddRange(cells.Skip(1).Select(Format));
        ReplaceSelection(editor, string.Join(newLine, lines));
    }
}
