using System.Collections.Immutable;

namespace MacExplorer.Models;

public sealed record BackgroundThemeOptions
{
    public bool Enabled { get; init; }
    public string PresetId { get; set; } = "";
    public string ImageFile { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Strength { get; set; } = 40;
    public int TextureStrength { get; set; } = 20;
    public ImmutableDictionary<string, BackgroundThemeColorSelection> Colors { get; set; } = ImmutableDictionary<string, BackgroundThemeColorSelection>.Empty;

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
            Strength = Math.Clamp(Strength, 0, 100),
            TextureStrength = Math.Clamp(id != PresetId && BackgroundThemePresets.Find(id) is { } preset
                ? preset.TextureStrength : TextureStrength, 0, 100)
        };
    }
}
