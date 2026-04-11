namespace FKFinder.Models;

public class Collection
{
    public int Id { get; init; }
    public string Name { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; init; }
}
