using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace MacExplorer.Controls;

/// <summary>Fits file names to their actual text layout, keeping the extension intact.</summary>
public sealed class FileNameText : TextBlock
{
    public FileNameText() => TextTrimming = TextTrimming.CharacterEllipsis;

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override TextLayout CreateTextLayout(string? text)
    {
        using var original = base.CreateTextLayout(text);
        // TextBlock's run cache belongs to the original text, so abbreviated
        // candidates need their own shaped runs while sharing its constraints.
        return CreateLayout(text ?? "", value => new TextLayout(value,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground,
            textAlignment: TextAlignment, textWrapping: TextWrapping,
            textDecorations: TextDecorations, flowDirection: FlowDirection,
            lineHeight: LineHeight, letterSpacing: LetterSpacing,
            maxWidth: original.MaxWidth, maxHeight: original.MaxHeight, maxLines: MaxLines,
            fontFeatures: FontFeatures));
    }

    internal static TextLayout CreateLayout(string name, Func<string, TextLayout> format)
    {
        var full = format(name);
        if (Fits(full, name)) return full;
        full.Dispose();

        var shortened = Abbreviate(name, value =>
        {
            using var layout = format(value);
            return Fits(layout, value);
        });
        return format(shortened);
    }

    internal static string Abbreviate(string name, Func<string, bool> fits)
    {
        var boundaries = StringInfo.ParseCombiningCharacters(name);
        var extensionStart = name.LastIndexOf('.');
        var extensionLength = extensionStart > 0
            ? boundaries.Count(index => index >= extensionStart) : 0;
        var low = extensionLength;
        var high = boundaries.Length - 1;
        var result = Candidate(low);
        while (low <= high)
        {
            var retained = (low + high) / 2;
            var candidate = Candidate(retained);
            if (fits(candidate))
            {
                result = candidate;
                low = retained + 1;
            }
            else high = retained - 1;
        }
        return result;

        string Candidate(int retained)
        {
            var prefix = Math.Min((retained + 1) / 2, retained - extensionLength);
            var suffix = retained - prefix;
            return name[..(prefix == 0 ? 0 : boundaries[prefix])] + "…"
                + (suffix == 0 ? "" : name[boundaries[^suffix]..]);
        }
    }

    private static bool Fits(TextLayout layout, string value)
        => layout.WidthIncludingTrailingWhitespace <= layout.MaxWidth + 0.01
            && layout.TextLines.All(line => !line.HasCollapsed)
            && layout.TextLines.Sum(line => line.Length) >= value.Length;
}
