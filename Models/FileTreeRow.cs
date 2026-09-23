namespace MacExplorer.Models;

/// <summary>Presentation state for one visible row in the optional tree view.</summary>
public readonly record struct FileTreeRow(
    FileSystemEntry Entry,
    int Depth,
    bool CanExpand,
    bool IsExpanded,
    bool IsLoading,
    bool HasError);

public readonly record struct FileTreeGroup(string Name, int DirectCount, int VisibleCount);
