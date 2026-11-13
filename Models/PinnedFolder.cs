namespace MacExplorer.Models;

public class PinnedFolder
{
    public int GroupId { get; init; }
    public int Id { get; init; }
    public string FolderPath { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public int SortOrder { get; init; }
    public DateTime PinnedAt { get; init; }
}
