using Avalonia;

namespace MacExplorer.Services.Impl;

public sealed class TypographyService : ITypographyService
{
    internal const string SettingKey = "font_size_preset";

    private static readonly string[] FontSizeTokens =
    [
        "FontSizeMeta",
        "FontSizeCaption",
        "FontSizeLabel",
        "FontSizeBody",
        "FontSizeBodyLarge",
        "FontSizeHeading",
        "FontSizeTitle",
        "FontSizeDisplay"
    ];

    private static readonly string[] LineHeightTokens =
    [
        "LineHeightMeta",
        "LineHeightCaption",
        "LineHeightLabel",
        "LineHeightBody",
        "LineHeightBodyLarge",
        "LineHeightHeading",
        "LineHeightTitle",
        "LineHeightDisplay"
    ];

    private static readonly string[] LayoutMetricTokens =
    [
        "TypographyListRowMinHeight",
        "TypographyTextControlMinHeight",
        "TypographyInlineEditorHeight",
        "TypographySettingsRowMinHeight",
        "TypographySettingsCompactRowMinHeight"
    ];

    private readonly ISettingsService _settingsService;
    private readonly Dictionary<string, double> _standardValues = new(StringComparer.Ordinal);
    private bool _initialized;

    public TypographyService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        CurrentPreset = ParsePreset(settingsService.Get(SettingKey));
    }

    public FontSizePreset CurrentPreset { get; private set; }

    public event EventHandler<TypographyChangedEventArgs>? TypographyChanged;

    public void Initialize()
    {
        if (_initialized)
            return;

        var application = Application.Current
            ?? throw new InvalidOperationException("Typography cannot initialize before the application resources are loaded.");

        CaptureStandardValues(application, FontSizeTokens);
        CaptureStandardValues(application, LineHeightTokens);
        CaptureStandardValues(application, LayoutMetricTokens);
        _initialized = true;
        ApplyPreset(CurrentPreset);
    }

    public void SetPreset(FontSizePreset preset)
    {
        if (!Enum.IsDefined(preset))
            preset = FontSizePreset.Standard;

        if (!_initialized)
            Initialize();

        if (CurrentPreset == preset)
            return;

        CurrentPreset = preset;
        _settingsService.SetDeferred(SettingKey, ToSettingValue(preset));
        ApplyPreset(preset);
        TypographyChanged?.Invoke(this, new TypographyChangedEventArgs { Preset = preset });
    }

    private void CaptureStandardValues(Application application, IEnumerable<string> tokens)
    {
        foreach (var token in tokens)
        {
            if (!application.TryGetResource(token, application.ActualThemeVariant, out var value) || value is not double number)
                throw new InvalidOperationException($"Missing typography resource '{token}'.");

            _standardValues[token] = number;
        }
    }

    private void ApplyPreset(FontSizePreset preset)
    {
        var application = Application.Current
            ?? throw new InvalidOperationException("Typography resources are unavailable.");
        var scale = GetScale(preset);
        var layoutScale = Math.Max(1d, scale);

        foreach (var token in FontSizeTokens)
            application.Resources[token] = RoundFont(_standardValues[token] * scale);

        foreach (var token in LineHeightTokens)
            application.Resources[token] = RoundMetric(_standardValues[token] * scale);

        foreach (var token in LayoutMetricTokens)
            application.Resources[token] = RoundMetric(_standardValues[token] * layoutScale);
    }

    private static FontSizePreset ParsePreset(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "small" => FontSizePreset.Small,
        "large" => FontSizePreset.Large,
        _ => FontSizePreset.Standard
    };

    private static string ToSettingValue(FontSizePreset preset) => preset switch
    {
        FontSizePreset.Small => "small",
        FontSizePreset.Large => "large",
        _ => "standard"
    };

    private static double GetScale(FontSizePreset preset) => preset switch
    {
        FontSizePreset.Small => 0.9,
        FontSizePreset.Large => 1.15,
        _ => 1d
    };

    private static double RoundFont(double value) =>
        Math.Round(value * 2, MidpointRounding.AwayFromZero) / 2;

    private static double RoundMetric(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero);
}
