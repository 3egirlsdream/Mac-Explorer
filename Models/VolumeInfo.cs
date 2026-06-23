namespace MacExplorer.Models;

public class VolumeInfo
{
    public string Path { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsExternal { get; init; }
    public bool IsRemovable { get; init; }
}
