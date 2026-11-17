namespace MacExplorer.Models;

public sealed record DiskInfo
{
    public string Identity { get; init; } = "";
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Format { get; init; }
    public string? Connection { get; init; }
    public string? DeviceIdentifier { get; init; }
    public string? VolumeUuid { get; init; }
    public string? ContainerIdentifier { get; init; }
    public string? DeviceName { get; init; }
    public string? MountId { get; init; }
    public long? TotalBytes { get; init; }
    public long? FreeBytes { get; init; }
    public long? AvailableBytes { get; init; }
    public bool? IsReadOnly { get; init; }
    public bool IsExternal { get; init; }
    public bool IsDiskImage { get; init; }
    public bool CanEject { get; init; }
    public bool IsApfs => string.Equals(Format, "apfs", StringComparison.OrdinalIgnoreCase);
    public double UsedPercent => TotalBytes is > 0 && FreeBytes.HasValue
        ? Math.Clamp(100d * (TotalBytes.Value - FreeBytes.Value) / TotalBytes.Value, 0, 100) : 0;
}
