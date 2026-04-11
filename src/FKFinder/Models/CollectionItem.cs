namespace FKFinder.Models;

public class CollectionItem
{
    public int Id { get; init; }
    public int CollectionId { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public DateTime AddedAt { get; init; }
}
