namespace MacExplorer.Models;

public enum BackgroundTextureBlend { Normal, Overlay }

public sealed record BackgroundThemePreset(string Id, string FamilyKey, string NameKey, string? TextureAsset,
    int TextureStrength, BackgroundTextureBlend Blend, string[] LightColors, string[] DarkColors)
{
    public required string DefaultColor { get; init; }
    public required string[] ColorOptions { get; init; }
}

public static class BackgroundThemePresets
{
    // Wide, horizontally periodic SVG textures over a window-wide color palette.
    public static IReadOnlyList<BackgroundThemePreset> All { get; } = Array.AsReadOnly<BackgroundThemePreset>([
        new("cyber-circuit", "background.family.cyber", "background.preset.circuit", "cyber-circuit.svg", 35, BackgroundTextureBlend.Normal,
            ["#E7E6F6", "#DDEFF4", "#DFEAF7", "#DFEFEB"], ["#161423", "#101D29", "#111D2B", "#122325"]) { DefaultColor = "#829FCF", ColorOptions = ["#829FCF", "#A58BD1", "#7DA9AC", "#D59FB4"] },
        new("cyber-hud", "background.family.cyber", "background.preset.hud", "cyber-hud.svg", 35, BackgroundTextureBlend.Normal,
            ["#E1EAF5", "#E1F2F1", "#E4E5F3", "#DEEDF5"], ["#111C2A", "#102124", "#1C192C", "#14202C"]) { DefaultColor = "#7CA4BD", ColorOptions = ["#7CA4BD", "#84AFB7", "#A7A0D1", "#BD9E8D"] },
        new("cyber-city", "background.family.cyber", "background.preset.city", "cyber-city.svg", 35, BackgroundTextureBlend.Normal,
            ["#EDE1F0", "#E3E5F7", "#EADFF0", "#DFECF3"], ["#221529", "#18182A", "#21172B", "#11202A"]) { DefaultColor = "#BC91C6", ColorOptions = ["#BC91C6", "#AD8ECA", "#8EA9D3", "#CE97B6"] },
        new("nature-willow", "background.family.nature", "background.preset.willow", "nature-willow.svg", 35, BackgroundTextureBlend.Normal,
            ["#E6EEDD", "#DDF0E7", "#DFEFE6", "#E8EFD9"], ["#192017", "#11241F", "#10231F", "#1B2014"]) { DefaultColor = "#88B28E", ColorOptions = ["#88B28E", "#A8BA8D", "#7AADA1", "#B8AD86"] },
        new("nature-marigold", "background.family.nature", "background.preset.marigold", "nature-marigold.svg", 35, BackgroundTextureBlend.Normal,
            ["#F4E9DC", "#EAE0EF", "#ECE5DC", "#E6E3F1"], ["#221D19", "#21192A", "#1D211B", "#191A2B"]) { DefaultColor = "#D6A88F", ColorOptions = ["#D6A88F", "#C7A3C6", "#9EBB9F", "#B4AED0"] },
        new("nature-trellis", "background.family.nature", "background.preset.trellis", "nature-trellis.svg", 35, BackgroundTextureBlend.Normal,
            ["#E8E4F2", "#E2EDF0", "#E4EBDD", "#EEE3ED"], ["#1D1929", "#131F29", "#19231B", "#261B27"]) { DefaultColor = "#AAA9C5", ColorOptions = ["#AAA9C5", "#A9BCA1", "#A5B8C5", "#CEACBD"] },
        new("animal-dragon", "background.family.animal", "background.preset.dragon", "animal-dragon.svg", 35, BackgroundTextureBlend.Normal,
            ["#F7E6DA", "#F2E5DD", "#F1DFD9", "#F2E4EB"], ["#261C24", "#261920", "#201B2A", "#2A1D23"]) { DefaultColor = "#CBA088", ColorOptions = ["#CBA088", "#D6B383", "#B79CAA", "#BCB89A"] },
        new("animal-rabbit", "background.family.animal", "background.preset.rabbit", "animal-rabbit.svg", 35, BackgroundTextureBlend.Normal,
            ["#E9E6F2", "#E3EDF4", "#E9E8F1", "#E7EFEB"], ["#1D1B2B", "#151F2C", "#211B2A", "#16231F"]) { DefaultColor = "#A3A4CB", ColorOptions = ["#A3A4CB", "#B79EC8", "#9AB7C7", "#CBABB8"] },
        new("animal-panda", "background.family.animal", "background.preset.panda", "animal-panda.svg", 35, BackgroundTextureBlend.Normal,
            ["#E7EDDE", "#DFECE5", "#ECECDA", "#DDEBE5"], ["#1D2217", "#15251F", "#222218", "#14221D"]) { DefaultColor = "#96B49D", ColorOptions = ["#96B49D", "#ABBA97", "#9CB7AD", "#B9AC91"] }
    ]);

    public static BackgroundThemePreset? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    internal static string MigrateId(string id) => id switch
    {
        "qq-circuit-pink" => "cyber-circuit",
        "qq-circuit-neon" => "cyber-hud",
        "qq-confetti-taro" => "nature-marigold",
        "qq-dunhuang-red" => "nature-trellis",
        "qq-ink-blue" => "animal-panda",
        _ => id
    };
}
