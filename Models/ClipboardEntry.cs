namespace MacExplorer.Models;

public class ClipboardEntry
{
    public List<string> SourcePaths { get; init; } = [];
    public ClipboardOperation Operation { get; init; }
    public bool IsEmpty => SourcePaths.Count == 0;
}

public enum ClipboardOperation
{
    Copy,
    Cut
}
