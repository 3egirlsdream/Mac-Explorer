using System.Globalization;
using SkiaSharp;

namespace MacExplorer.Models;

public sealed record BackgroundThemeColorSelection(string PaletteId = "", string CustomColor = "#8A9ED6")
{
    public BackgroundThemeColorSelection Normalize() => this with
    {
        PaletteId = PaletteId is "" or "custom" ? PaletteId : "",
        CustomColor = CustomColor is { Length: 7 } && CustomColor[0] == '#' &&
            uint.TryParse(CustomColor.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
                ? CustomColor.ToUpperInvariant() : "#8A9ED6"
    };

    public string[] GetColors(BackgroundThemePreset preset, bool dark)
    {
        if (PaletteId.Length == 0) return dark ? preset.DarkColors : preset.LightColors;
        SKColor.Parse(CustomColor).ToHsv(out var hue, out var saturation, out var brightness);
        // Keep every spectrum axis responsive within the light/dark surface range.
        var amount = Math.Min(saturation * (dark ? .85f : .45f), dark ? 58 : 28);
        var offsets = new[] { -12f, 8f, 14f, -6f };
        var value = dark ? 12 + brightness * .05f : 88 + brightness * .09f;
        var values = new[] { value, value + (dark ? 1 : -2), value - 1, value };
        return offsets.Select((offset, i) =>
        {
            var color = SKColor.FromHsv((hue + offset + 360) % 360, amount, values[i]);
            return $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
        }).ToArray();
    }
}
