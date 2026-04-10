namespace FKFinder.Models;

public class BreadcrumbSegment
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool HasDropdown { get; init; }
    public IReadOnlyList<BreadcrumbSegment>? Siblings { get; set; }
}
