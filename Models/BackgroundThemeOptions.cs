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

    public BackgroundThemeOptions Normalize()
    {
        var id = BackgroundThemePresets.MigrateId(PresetId ?? "");
        return this with
        {
            ImageFile = Path.GetFileName(ImageFile ?? ""),
            DisplayName = Path.GetFileName(DisplayName ?? ""),
            PresetId = id,
            Layout = Enum.IsDefined(Layout) ? Layout : BackgroundImageLayout.HeightTile,
            Strength = Math.Clamp(Strength, 0, 100),
            TextureStrength = Math.Clamp(id != PresetId && BackgroundThemePresets.Find(id) is { } preset
                ? preset.TextureStrength : TextureStrength, 0, 100)
        };
    }
}
