namespace MacExplorer.Models;

public class VolumeInfo
{
    public string Path { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsExternal { get; init; }
    public bool IsNetwork { get; init; }
    public string? SourceUrl { get; init; }
    public string? MountId { get; init; }
    public string IconData => IsNetwork ? Assets.Icons.Network : Assets.Icons.ExternalDrive;
    public bool IsRemovable { get; init; }
}
