using System.Collections.Immutable;

namespace MacExplorer.Models;

public enum BackgroundImageLayout { HeightTile, Cover }

public sealed record BackgroundThemeOptions
{
    public bool Enabled { get; init; }
    public string PresetId { get; init; } = "";
    public string ImageFile { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public BackgroundImageLayout Layout { get; init; }
    public int Strength { get; init; } = 40;
    public int TextureStrength { get; init; } = 20;
    public ImmutableDictionary<string, BackgroundThemeColorSelection> Colors { get; init; } = ImmutableDictionary<string, BackgroundThemeColorSelection>.Empty;

    public BackgroundThemeColorSelection GetColors(string presetId) => Colors.GetValueOrDefault(presetId) ?? new();

    public BackgroundThemeOptions Normalize()
    {
        var id = BackgroundThemePresets.MigrateId(PresetId ?? "");
        var colors = Colors ?? ImmutableDictionary<string, BackgroundThemeColorSelection>.Empty;
        foreach (var entry in colors)
        {
            if (BackgroundThemePresets.Find(entry.Key) == null || entry.Value == null) colors = colors.Remove(entry.Key);
            else if (entry.Value.Normalize() is var normalized && normalized != entry.Value) colors = colors.SetItem(entry.Key, normalized);
        }
        return this with
        {
            ImageFile = Path.GetFileName(ImageFile ?? ""),
            DisplayName = Path.GetFileName(DisplayName ?? ""),
            PresetId = id,
            Colors = colors,
            Layout = Enum.IsDefined(Layout) ? Layout : BackgroundImageLayout.HeightTile,
            Strength = Math.Clamp(Strength, 0, 100),
            TextureStrength = Math.Clamp(id != PresetId && BackgroundThemePresets.Find(id) is { } preset
                ? preset.TextureStrength : TextureStrength, 0, 100)
        };
    }
}
