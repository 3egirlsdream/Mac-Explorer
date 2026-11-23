namespace MacExplorer.Services;

/// <summary>Persisted layout preferences for the Edge-style vertical tab bar.</summary>
public static class VerticalTabBarPreferences
{
    public const string EnabledKey = "vertical_tabs_enabled";
    public const string PinnedKey = "vertical_tabs_pinned";
    public const string WidthKey = "vertical_tabs_width";

    public const double CollapsedWidth = 60;
    public const double DefaultExpandedWidth = 228;
    public const double MinExpandedWidth = 180;
    public const double MaxExpandedWidth = 360;
    private const double MinContentWidth = 360;

    public static bool IsEnabled(ISettingsService settings)
        => settings.Get(EnabledKey, false);

    public static void SetEnabled(ISettingsService settings, bool enabled)
        => settings.Set(EnabledKey, enabled);

    public static bool IsPinned(ISettingsService settings)
        => settings.Get(PinnedKey, true);

    public static void SetPinned(ISettingsService settings, bool pinned)
        => settings.Set(PinnedKey, pinned);

    public static double GetWidth(ISettingsService settings)
    {
        var saved = settings.Get(WidthKey, (int)DefaultExpandedWidth);
        return ClampWidth(saved);
    }

    public static void SetWidth(ISettingsService settings, double width)
        => settings.Set(WidthKey, (int)Math.Round(ClampWidth(width)));

    public static double ClampWidth(double width, double windowWidth = double.PositiveInfinity)
    {
        if (!double.IsFinite(width) || width <= 0)
            width = DefaultExpandedWidth;

        var maxAllowed = MaxExpandedWidth;
        if (double.IsFinite(windowWidth) && windowWidth > 0)
            maxAllowed = Math.Min(MaxExpandedWidth, Math.Max(MinExpandedWidth, Math.Floor(windowWidth - MinContentWidth)));

        return Math.Clamp(Math.Round(width), MinExpandedWidth, maxAllowed);
    }

    public static bool IsRelatedSetting(string key)
        => key is EnabledKey or PinnedKey or WidthKey;
}
