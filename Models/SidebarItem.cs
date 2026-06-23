namespace MacExplorer.Models;

public class SidebarItem
{
    /// <summary>
    /// Display name shown in sidebar.
    /// </summary>
    public string DisplayName => Name;

    /// <summary>
    /// SVG path data for the PathIcon.
    /// </summary>
    public string IconData { get; init; } = "";

    /// <summary>
    /// Foreground color for the icon (hex string).
    /// </summary>
    public string IconColor { get; init; } = "#54A3F7";

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
