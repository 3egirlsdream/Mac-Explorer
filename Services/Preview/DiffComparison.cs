using System.Text;

namespace MacExplorer.Services.Preview;

internal sealed class DiffPaneLine(string text, string newLine, long? number, DiffLineKind kind)
{
    internal string Text { get; } = text;
    internal string NewLine { get; set; } = newLine;
    internal long? Number { get; } = number;
    internal DiffLineKind Kind { get; } = kind;
}

internal sealed record DiffComparison(DiffPaneLine?[] Before, DiffPaneLine?[] After)
{
    // Very large patches remain available in the original text view without expanding into two documents.
    private const int MaxComparisonLines = 100_000;

    internal static DiffComparison? Create(string text, DiffLineKind[] kinds, CancellationToken token)
    {
        if (kinds.Length > MaxComparisonLines) return null;
        var before = new List<DiffPaneLine?>();
        var after = new List<DiffPaneLine?>();
        var removed = new List<DiffPaneLine>();
        var added = new List<DiffPaneLine>();
        long oldNumber = 0, newNumber = 0;
        var hasHunk = false;
        var fileHeader = -1;
        DiffPaneLine? lastBefore = null, lastAfter = null;
        var start = 0;
        for (var i = 0; i < kinds.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var length = text.AsSpan(start).IndexOfAny('\r', '\n');
            var end = length < 0 ? text.Length : start + length;
            var delimiterEnd = end;
            if (delimiterEnd < text.Length && text[delimiterEnd++] == '\r' && delimiterEnd < text.Length && text[delimiterEnd] == '\n')
                delimiterEnd++;
            var line = text[start..end];
            var newLine = text[end..delimiterEnd];
            start = delimiterEnd;

            if (kinds[i] == DiffLineKind.Removed)
            {
                lastBefore = new DiffPaneLine(line[1..], newLine, oldNumber++, DiffLineKind.Removed);
                lastAfter = null;
                removed.Add(lastBefore);
            }
            else if (kinds[i] == DiffLineKind.Added)
            {
                lastAfter = new DiffPaneLine(line[1..], newLine, newNumber++, DiffLineKind.Added);
                lastBefore = null;
                added.Add(lastAfter);
            }
            else if (line.StartsWith("\\ No newline at end of file", StringComparison.Ordinal))
            {
                if (lastBefore != null) lastBefore.NewLine = "";
                if (lastAfter != null) lastAfter.NewLine = "";
            }
            else
            {
                FlushChanges();
                if (kinds[i] == DiffLineKind.Hunk)
                {
                    if (!DiffPreviewDocument.TryHunkStart(line, out oldNumber, out newNumber)) return null;
                    hasHunk = true;
                }

                if (kinds[i] == DiffLineKind.Header && line.StartsWith("--- ", StringComparison.Ordinal))
                {
                    fileHeader = before.Count;
                    before.Add(new DiffPaneLine(line[4..], newLine, null, DiffLineKind.Header));
                    after.Add(null);
                }
                else if (kinds[i] == DiffLineKind.Header && line.StartsWith("+++ ", StringComparison.Ordinal)
                         && fileHeader == before.Count - 1 && fileHeader >= 0)
                {
                    after[fileHeader] = new DiffPaneLine(line[4..], newLine, null, DiffLineKind.Header);
                    fileHeader = -1;
                }
                else if (kinds[i] == DiffLineKind.Unchanged)
                {
                    lastBefore = new DiffPaneLine(line[1..], newLine, oldNumber++, DiffLineKind.Context);
                    lastAfter = new DiffPaneLine(line[1..], newLine, newNumber++, DiffLineKind.Context);
                    before.Add(lastBefore);
                    after.Add(lastAfter);
                }
                else
                {
                    var metadata = new DiffPaneLine(line, newLine, null, kinds[i]);
                    before.Add(metadata);
                    after.Add(metadata);
                    lastBefore = lastAfter = null;
                }
            }
        }
        FlushChanges();
        return hasHunk ? new DiffComparison(before.ToArray(), after.ToArray()) : null;

        void FlushChanges()
        {
            for (var i = 0; i < Math.Max(removed.Count, added.Count); i++)
            {
                before.Add(i < removed.Count ? removed[i] : null);
                after.Add(i < added.Count ? added[i] : null);
            }
            removed.Clear();
            added.Clear();
        }
    }

    internal static string Project(DiffPaneLine?[] lines) => string.Join('\n', lines.Select(line => line?.Text ?? "")) + (lines.Length > 0 ? "\n" : "");

    internal static string Copy(DiffPaneLine?[] lines, int firstLine, int startColumn, int lastLine, int endColumn)
    {
        var result = new StringBuilder();
        for (var i = firstLine; i <= lastLine && i < lines.Length; i++)
        {
            if (lines[i] is not { } line) continue;
            var start = i == firstLine ? Math.Min(startColumn, line.Text.Length) : 0;
            var end = i == lastLine ? Math.Min(endColumn, line.Text.Length) : line.Text.Length;
            if (end > start) result.Append(line.Text.AsSpan(start, end - start));
            if (i < lastLine || endColumn > line.Text.Length) result.Append(line.NewLine);
        }
        return result.ToString();
    }
}
