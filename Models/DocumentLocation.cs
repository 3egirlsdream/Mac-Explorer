using MacExplorer.Services.Impl;

namespace MacExplorer.Models;

public sealed record DocumentLocation(string Kind, int Number = 1, string? Sheet = null, string? Cell = null)
{
    public string DisplayName => Kind == "cell" ? LocalizationText.Get("content.location.cell", Sheet ?? "", Cell ?? "")
        : LocalizationText.Get("content.location." + Kind, Number);
}
public sealed record DocumentTextSegment(string Text, DocumentLocation Location);
public sealed record DocumentContent(IReadOnlyList<DocumentTextSegment> Segments, bool IsTruncated = false);
public sealed record DocumentMatch(string Snippet, DocumentLocation Location);
