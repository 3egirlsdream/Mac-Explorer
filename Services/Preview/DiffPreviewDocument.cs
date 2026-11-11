using System.Globalization;
using System.Text.RegularExpressions;

namespace MacExplorer.Services.Preview;

internal enum DiffLineKind : byte { Context, Header, Hunk, Added, Removed, Unchanged }

internal sealed record DiffPreviewDocument(string Text, DiffLineKind[] Lines)
{
    internal DiffComparison? Comparison { get; init; }
    private static readonly Regex HunkHeader = new(
        @"^@@ -(?<old>[0-9]+)(?:,(?<oldCount>[0-9]+))? \+(?<new>[0-9]+)(?:,(?<newCount>[0-9]+))? @@",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static bool IsDiff(string path) => Path.GetExtension(path).ToLowerInvariant() is ".diff" or ".patch";

    internal static async Task<DiffPreviewDocument> ReadAsync(string path, CancellationToken token)
    {
        var text = await TextPreviewReader.ReadAsync(path, token).ConfigureAwait(false);
        return await Task.Run(() => Parse(text, token), token).ConfigureAwait(false);
    }

    internal static DiffPreviewDocument Parse(string text, CancellationToken token = default)
    {
        var kinds = new List<DiffLineKind>();
        using var reader = new StringReader(text);
        int oldRemaining = 0, newRemaining = 0;
        var canCompare = true;
        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            var kind = DiffLineKind.Context;
            if (line.StartsWith("diff ", StringComparison.Ordinal) || line.StartsWith("Index: ", StringComparison.Ordinal))
            {
                if (oldRemaining != 0 || newRemaining != 0) canCompare = false;
                oldRemaining = newRemaining = 0;
                kind = DiffLineKind.Header;
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (oldRemaining != 0 || newRemaining != 0) canCompare = false;
                oldRemaining = newRemaining = 0;
                var match = HunkHeader.Match(line);
                if (match.Success && TryCount(match.Groups["oldCount"], out var oldCount) && TryCount(match.Groups["newCount"], out var newCount))
                {
                    oldRemaining = oldCount;
                    newRemaining = newCount;
                }
                else canCompare = false;
                kind = DiffLineKind.Hunk;
            }
            else if (oldRemaining > 0 || newRemaining > 0)
            {
                // A hunk's content can itself start with --- or +++; counts disambiguate file headers.
                if (line.StartsWith('-') && oldRemaining > 0) { oldRemaining--; kind = DiffLineKind.Removed; }
                else if (line.StartsWith('+') && newRemaining > 0) { newRemaining--; kind = DiffLineKind.Added; }
                else if (line.StartsWith(' ') && oldRemaining > 0 && newRemaining > 0) { oldRemaining--; newRemaining--; kind = DiffLineKind.Unchanged; }
                else if (!line.StartsWith('\\')) { oldRemaining = newRemaining = 0; canCompare = false; }
            }
            else if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal)
                     || line.StartsWith("index ", StringComparison.Ordinal) || line.StartsWith("new file mode ", StringComparison.Ordinal)
                     || line.StartsWith("deleted file mode ", StringComparison.Ordinal) || line.StartsWith("rename from ", StringComparison.Ordinal)
                     || line.StartsWith("rename to ", StringComparison.Ordinal) || line.StartsWith("Binary files ", StringComparison.Ordinal))
                kind = DiffLineKind.Header;
            kinds.Add(kind);
        }
        var lines = kinds.ToArray();
        return new DiffPreviewDocument(text, lines) { Comparison = canCompare && oldRemaining == 0 && newRemaining == 0 ? DiffComparison.Create(text, lines, token) : null };
    }

    internal static bool TryHunkStart(string line, out long before, out long after)
    {
        var match = HunkHeader.Match(line);
        before = after = 0;
        if (!match.Success || !TryCount(match.Groups["oldCount"], out _) || !TryCount(match.Groups["newCount"], out _)
            || !int.TryParse(match.Groups["old"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var oldStart)
            || !int.TryParse(match.Groups["new"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var newStart)) return false;
        before = oldStart;
        after = newStart;
        return true;
    }

    private static bool TryCount(Group group, out int count)
    {
        count = 1;
        return !group.Success || int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out count);
    }
}
