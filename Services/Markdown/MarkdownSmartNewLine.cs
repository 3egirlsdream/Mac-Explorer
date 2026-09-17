// Adapted from Vex (MIT), MarkdownSmartNewLine.cs. See ThirdParty/Vex/LICENSE.
using System.Globalization;
using System.Text.RegularExpressions;
using AvaloniaEdit.Document;
using AvaloniaEdit.Indentation;

namespace MacExplorer.Services.Markdown;

internal static class MarkdownSmartNewLine
{
    private static readonly Regex Prefix = new(
        @"^(?<indent>[ \t]*)(?:(?<task>[-*+][ \t]+\[[ xX]\][ \t]+)|(?<list>[-*+][ \t]+)|(?<number>[0-9]+)(?<delimiter>[.)])(?<space>[ \t]+)|(?<quote>>[ \t]*))(?<rest>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static SmartNewLineChange CreateChange(string text, int selectionStart, int selectionLength,
        string newLine = "\n", bool allowExit = true)
    {
        var start = Math.Clamp(selectionStart, 0, text.Length);
        var length = Math.Clamp(selectionLength, 0, text.Length - start);
        // Unlike the upstream implementation, this also works for empty documents and offset zero.
        var lineStart = start == 0 ? 0 : text.LastIndexOfAny(['\r', '\n'], start - 1) + 1;
        var line = text[lineStart..start];
        var match = Prefix.Match(line);
        if (!match.Success)
            return new(start, length, newLine + new string(line.TakeWhile(c => c is ' ' or '\t').ToArray()));

        var indentation = match.Groups["indent"].Value;
        if (allowExit && string.IsNullOrWhiteSpace(match.Groups["rest"].Value))
            return new(lineStart, start + length - lineStart, indentation + newLine);

        string prefix;
        if (match.Groups["task"].Success)
            prefix = Regex.Replace(match.Groups["task"].Value, @"\[[ xX]\]", "[ ]");
        else if (match.Groups["list"].Success)
            prefix = match.Groups["list"].Value;
        else if (match.Groups["quote"].Success)
            prefix = match.Groups["quote"].Value;
        else if (long.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                 && number < long.MaxValue)
            prefix = (number + 1).ToString(CultureInfo.InvariantCulture)
                + match.Groups["delimiter"].Value + match.Groups["space"].Value;
        else
            prefix = string.Empty; // Do not wrap an overflowing list number to a negative value.
        return new(start, length, newLine + indentation + prefix);
    }
}

internal readonly record struct SmartNewLineChange(int Start, int Length, string Text);

/// <summary>Runs after the editor inserts a newline, rather than stealing IME Enter/candidate confirmation.</summary>
internal sealed class MarkdownIndentationStrategy : IIndentationStrategy
{
    private readonly DefaultIndentationStrategy _fallback = new();

    public void IndentLine(TextDocument document, DocumentLine line)
    {
        if (line.PreviousLine is not { } previous) return;
        if (InsideFence(document, previous))
        {
            _fallback.IndentLine(document, line);
            return;
        }
        var before = document.GetText(previous.Offset, previous.Length);
        var change = MarkdownSmartNewLine.CreateChange(before, before.Length, 0, allowExit: line.Length == 0);
        if (change.Start < before.Length)
        {
            // The delimiter already exists. Only remove the empty list/quote marker.
            document.Replace(previous.Offset + change.Start, change.Length, change.Text[..^1]);
        }
        else
        {
            document.Insert(line.Offset, change.Text[1..]);
        }
    }

    public void IndentLines(TextDocument document, int beginLine, int endLine) =>
        _fallback.IndentLines(document, beginLine, endLine);

    private static bool InsideFence(TextDocument document, DocumentLine last)
    {
        var marker = '\0';
        var openingLength = 0;
        for (var line = document.GetLineByNumber(1); line != null && line.LineNumber <= last.LineNumber; line = line.NextLine)
        {
            var text = document.GetText(line.Offset, line.Length).TrimStart();
            if (text.Length < 3 || text[0] is not ('`' or '~')) continue;
            var count = 1;
            while (count < text.Length && text[count] == text[0]) count++;
            if (count < 3) continue;
            if (marker == '\0') { marker = text[0]; openingLength = count; }
            else if (text[0] == marker && count >= openingLength && string.IsNullOrWhiteSpace(text[count..])) marker = '\0';
        }
        return marker != '\0';
    }
}
