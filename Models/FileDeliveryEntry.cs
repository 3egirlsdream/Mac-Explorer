using MacExplorer.Services;

namespace MacExplorer.Models;

public sealed record FileDeliveryEntry(string Id, string Name, string Location)
{
    public string? CurrentLocation { get; init; }
    public FileListScrollAnchor? ScrollAnchor { get; init; }
    public double ScrollOffset { get; init; }
}

public sealed record FileDeliveryPreferences
{
    public List<FileDeliveryEntry> Entries { get; init; } = [];
    public string? SelectedId { get; init; }
}
