namespace MacExplorer.Models;

public enum BackgroundTextureBlend { Normal, Overlay }

public sealed record BackgroundThemePreset(string Id, string NameKey, string? TextureAsset,
    int TextureStrength, BackgroundTextureBlend Blend, string[] LightColors, string[] DarkColors);

public static class BackgroundThemePresets
{
    // Palette adaptations and source provenance are documented beside the original textures.
    public static IReadOnlyList<BackgroundThemePreset> All { get; } = Array.AsReadOnly<BackgroundThemePreset>([
        new("qq-circuit-pink", "background.preset.pink_city", "circuit.svg", 20, BackgroundTextureBlend.Overlay,
            ["#E8BAC3", "#F1C39F", "#D7ACF8", "#D7ACF8"], ["#644054", "#705039", "#57387B", "#57387B"]),
        new("qq-circuit-neon", "background.preset.neon", "circuit.svg", 20, BackgroundTextureBlend.Overlay,
            ["#E4B6F7", "#F7B2F5", "#B9BDFA", "#C2BBFA"], ["#603A77", "#713774", "#3B417A", "#444079"]),
        new("qq-confetti-taro", "background.preset.taro", "confetti.svg", 20, BackgroundTextureBlend.Normal,
            ["#FFE1DB", "#FFE0D4", "#CBC5FE", "#E2DFFE"], ["#6D5558", "#6D554F", "#494574", "#5A5873"]),
        new("qq-dunhuang-red", "background.preset.dunhuang", "dunhuang.png", 100, BackgroundTextureBlend.Normal,
            ["#CDBCA5", "#CBB79F", "#EAECDD", "#CFC9BF"], ["#99A4865B", "#99A07B55", "#99D8DFBD", "#99A89D8A"]),
        new("qq-ink-blue", "background.preset.ink_blue", null, 0, BackgroundTextureBlend.Normal,
            ["#DCEBF0", "#C9E0EA", "#EDF4F6", "#D7E9EF"], ["#293E47", "#294550", "#25373F", "#304A54"])
    ]);

    public static BackgroundThemePreset? Find(string id) => All.FirstOrDefault(p => p.Id == id);
}
