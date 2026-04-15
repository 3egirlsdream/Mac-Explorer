namespace MacExplorer.Models;

public class SidebarItem
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string IconKey { get; init; } = "folder";
    public bool IsExpanded { get; set; }
    public IReadOnlyList<SidebarItem>? Children { get; set; }
    public SidebarItemType Type { get; init; }
}

public enum SidebarItemType
{
    QuickAccess,
    Favorite,
    Place,
    Volume,
    Tag
}
