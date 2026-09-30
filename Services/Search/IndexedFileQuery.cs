namespace MacExplorer.Services.Search;

/// <summary>Literal database filters. Date upper bounds are exclusive.</summary>
public sealed record IndexedFileQuery
{
    public string Query { get; init; } = "";
    public string Source { get; init; } = "all";
    public string[] Extensions { get; init; } = [];
    public IndexedTagFilter[] Tags { get; init; } = [];
    public string? FileTag { get; init; }
    public string? Person { get; init; }
    public int? MinRating { get; init; }
    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }
    public DateTimeOffset? CreatedFrom { get; init; }
    public DateTimeOffset? CreatedTo { get; init; }
    public DateTimeOffset? ModifiedFrom { get; init; }
    public DateTimeOffset? ModifiedTo { get; init; }
    public DateOnly? TakenFrom { get; init; }
    public DateOnly? TakenTo { get; init; }
    public int Offset { get; init; }
}

public sealed record IndexedTagFilter(string Type, string Value);
public sealed record IndexedFileMatch(string FullPath, string Name, string Extension, long Size,
    bool IsDirectory, DateTimeOffset Created, DateTimeOffset LastModified, bool HasCurrentAnalysis,
    int Rating, IReadOnlyList<string> MatchedSources);
public sealed record IndexedFileSearchResult(IReadOnlyList<IndexedFileMatch> Items,
    int Offset, int? NextOffset, long AnalyzedFiles, IReadOnlyList<string> IndexStatus)
{
    public bool HasMore => NextOffset.HasValue;
}
