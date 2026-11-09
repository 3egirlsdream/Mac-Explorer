using Avalonia;
using Avalonia.Harfbuzz;
using Avalonia.Media;
using Avalonia.Platform;

namespace MacExplorer.TextShaping;

public static class PohTextShaping
{
    public static bool IsActive => AvaloniaLocator.Current.GetService<ITextShaperImpl>() is PohHarfBuzzTextShaper;

    public static bool TryInstall()
    {
        if (IsActive) return true;
        if (typeof(GlyphTypeface).Assembly.GetName().Version != new Version(12, 0, 4, 0)
            || typeof(HarfBuzzTextShaper).Assembly.GetName().Version != new Version(12, 0, 4, 0)
            || AvaloniaLocator.Current.GetService<ITextShaperImpl>()?.GetType() != typeof(HarfBuzzTextShaper))
            return false;
        AvaloniaLocator.CurrentMutable.Bind<ITextShaperImpl>().ToConstant(new PohHarfBuzzTextShaper());
        return true;
    }
}
