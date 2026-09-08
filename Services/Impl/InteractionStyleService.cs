using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace MacExplorer.Services.Impl;

/// <summary>
/// Applies per-theme user-selected interaction resources above the theme dictionaries.
/// Geometry tokens remain global implementation details and are intentionally not user-editable.
/// </summary>
public sealed class InteractionStyleService : IInteractionStyleService
{
    private const string CornerRadiusSettingKey = "interaction.corner_radius";
    private const string DisabledOpacitySettingKey = "interaction.disabled_opacity";
    private const string FormerFocusSettingKey = "interaction.focus";

    private static readonly IReadOnlyDictionary<InteractionStyleToken, TokenDefinition> TokenDefinitions =
        new Dictionary<InteractionStyleToken, TokenDefinition>
        {
            [InteractionStyleToken.Hover] = new(
                "interaction.hover",
                "InteractionHoverBrush",
                ["ButtonBackgroundPointerOver", "ComboBoxBackgroundPointerOver", "ComboBoxItemBackgroundPointerOver",
                    "TextControlBackgroundPointerOver", "ButtonBackgroundPressed", "ComboBoxBackgroundPressed",
                    "ComboBoxItemBackgroundPressed", "ListBoxItemBackgroundPointerOver", "ListBoxItemBackgroundPressed",
                    "ToggleButtonBackgroundPointerOver", "ToggleButtonBackgroundPressed"]),
            [InteractionStyleToken.Selected] = new(
                "interaction.selected",
                "InteractionSelectedBrush",
                ["SelectionBrush", "ColorBgSelected", "ColorAccentLight", "ListBoxItemBackgroundSelected", "ComboBoxItemBackgroundSelected"]),
            [InteractionStyleToken.SelectedHover] = new(
                "interaction.selected_hover",
                "InteractionSelectedHoverBrush",
                ["ListBoxItemBackgroundSelectedPointerOver", "ListBoxItemBackgroundSelectedPressed",
                    "ComboBoxItemBackgroundSelectedPointerOver", "ComboBoxItemBackgroundSelectedPressed",
                    "ToggleButtonBackgroundCheckedPointerOver", "ToggleButtonBackgroundCheckedPressed"]),
            [InteractionStyleToken.TextHighlight] = new(
                "interaction.text_highlight",
                "InteractionTextHighlightBrush",
                ["TextControlSelectionHighlightColor"]),
        };

    private readonly ISettingsService _settingsService;
    private readonly Dictionary<(InteractionStyleToken Token, InteractionThemeVariant Theme), Color> _defaultColors = [];
    private bool _initialized;

    public InteractionStyleService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public InteractionThemeVariant CurrentTheme => ResolveCurrentTheme();

    public void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;
        GetApplication().ActualThemeVariantChanged += (_, _) => ApplyCurrentTheme();
        CaptureDefaultColors();
        MigrateLegacyColorSettings();
        MigrateFormerFocusSettings();
        ApplyCurrentTheme();

        if (TryGetStoredDouble(CornerRadiusSettingKey, out var cornerRadius))
            ApplyCornerRadius(cornerRadius);
        if (TryGetStoredDouble(DisabledOpacitySettingKey, out var disabledOpacity))
            ApplyDisabledOpacity(disabledOpacity);
    }

    public string GetColor(InteractionStyleToken token, InteractionThemeVariant theme)
    {
        if (TryGetStoredColor(token, theme, out var configured))
            return FormatColor(configured);

        return _defaultColors.TryGetValue((token, theme), out var defaultColor)
            ? FormatColor(defaultColor)
            : throw new InvalidOperationException($"Missing interaction resource '{GetDefinition(token).ResourceKey}'.");
    }

    public bool TrySetColor(InteractionStyleToken token, InteractionThemeVariant theme, string value)
    {
        if (!Color.TryParse(value?.Trim(), out var color))
            return false;

        EnsureInitialized();
        _settingsService.Set(GetSettingKey(token, theme), FormatColor(color));
        if (theme == CurrentTheme)
            ApplyCurrentTheme();
        return true;
    }

    public void ResetColors(InteractionThemeVariant theme)
    {
        EnsureInitialized();
        foreach (var token in TokenDefinitions.Keys)
            _settingsService.Set(GetSettingKey(token, theme), string.Empty);

        if (theme == CurrentTheme)
            ApplyCurrentTheme();
    }

    public void ApplyCurrentTheme()
    {
        EnsureInitialized();
        var theme = CurrentTheme;
        var resources = GetApplication().Resources;
        foreach (var (token, definition) in TokenDefinitions)
        {
            resources.Remove(definition.ResourceKey);
            foreach (var bridgeKey in definition.FluentBridgeKeys)
                resources.Remove(bridgeKey);

            if (TryGetStoredColor(token, theme, out var color))
                ApplyColorOverride(definition, color);
        }
    }

    public double GetCornerRadius()
    {
        if (TryGetStoredDouble(CornerRadiusSettingKey, out var configured))
            return configured;

        var application = GetApplication();
        if (application.TryGetResource("InteractionCornerRadius", application.ActualThemeVariant, out var value)
            && value is CornerRadius radius)
            return radius.TopLeft;

        throw new InvalidOperationException("Missing interaction resource 'InteractionCornerRadius'.");
    }

    public void SetCornerRadius(double value)
    {
        EnsureInitialized();
        var normalized = Math.Round(Math.Clamp(value, 0, 24), MidpointRounding.AwayFromZero);
        _settingsService.Set(CornerRadiusSettingKey, normalized.ToString(CultureInfo.InvariantCulture));
        ApplyCornerRadius(normalized);
    }

    public double GetDisabledOpacity()
    {
        if (TryGetStoredDouble(DisabledOpacitySettingKey, out var configured))
            return configured;

        var application = GetApplication();
        if (application.TryGetResource("InteractionDisabledOpacity", application.ActualThemeVariant, out var value)
            && value is double opacity)
            return opacity;

        throw new InvalidOperationException("Missing interaction resource 'InteractionDisabledOpacity'.");
    }

    public void SetDisabledOpacity(double value)
    {
        EnsureInitialized();
        var normalized = Math.Round(Math.Clamp(value, 0.15, 1), 2, MidpointRounding.AwayFromZero);
        _settingsService.Set(DisabledOpacitySettingKey, normalized.ToString(CultureInfo.InvariantCulture));
        ApplyDisabledOpacity(normalized);
    }

    public void ResetAll()
    {
        ResetColors(InteractionThemeVariant.Light);
        ResetColors(InteractionThemeVariant.Dark);

        var resources = GetApplication().Resources;
        _settingsService.Set(CornerRadiusSettingKey, string.Empty);
        _settingsService.Set(DisabledOpacitySettingKey, string.Empty);
        resources.Remove("InteractionCornerRadius");
        resources.Remove("InteractionDisabledOpacity");
    }

    private void MigrateLegacyColorSettings()
    {
        foreach (var (token, definition) in TokenDefinitions)
        {
            var legacyValue = _settingsService.Get(definition.LegacySettingKey);
            if (!Color.TryParse(legacyValue, out var color))
                continue;

            foreach (var theme in Enum.GetValues<InteractionThemeVariant>())
            {
                var settingKey = GetSettingKey(token, theme);
                if (string.IsNullOrWhiteSpace(_settingsService.Get(settingKey)))
                    _settingsService.Set(settingKey, FormatColor(color));
            }

            _settingsService.Set(definition.LegacySettingKey, string.Empty);
        }
    }

    private void MigrateFormerFocusSettings()
    {
        foreach (var theme in Enum.GetValues<InteractionThemeVariant>())
        {
            var selectedKey = GetSettingKey(InteractionStyleToken.Selected, theme);
            if (string.IsNullOrWhiteSpace(_settingsService.Get(selectedKey)))
            {
                var formerFocusKey = $"{FormerFocusSettingKey}.{theme.ToString().ToLowerInvariant()}";
                var formerFocusValue = _settingsService.Get(formerFocusKey);
                if (string.IsNullOrWhiteSpace(formerFocusValue))
                    formerFocusValue = _settingsService.Get(FormerFocusSettingKey);
                if (Color.TryParse(formerFocusValue, out var color))
                    _settingsService.Set(selectedKey, FormatColor(color));
            }

            _settingsService.Set($"{FormerFocusSettingKey}.{theme.ToString().ToLowerInvariant()}", string.Empty);
        }

        _settingsService.Set(FormerFocusSettingKey, string.Empty);
    }

    private void CaptureDefaultColors()
    {
        var application = GetApplication();
        foreach (var (token, definition) in TokenDefinitions)
        {
            foreach (var theme in Enum.GetValues<InteractionThemeVariant>())
            {
                if (application.TryGetResource(definition.ResourceKey, ToAvaloniaTheme(theme), out var value)
                    && value is ISolidColorBrush brush)
                    _defaultColors[(token, theme)] = brush.Color;
            }
        }
    }

    private void ApplyColorOverride(TokenDefinition definition, Color color)
    {
        var brush = new SolidColorBrush(color);
        var resources = GetApplication().Resources;
        resources[definition.ResourceKey] = brush;
        foreach (var bridgeKey in definition.FluentBridgeKeys)
            resources[bridgeKey] = brush;
    }

    private void ApplyCornerRadius(double value) =>
        GetApplication().Resources["InteractionCornerRadius"] = new CornerRadius(value);

    private void ApplyDisabledOpacity(double value) =>
        GetApplication().Resources["InteractionDisabledOpacity"] = value;

    private bool TryGetStoredColor(InteractionStyleToken token, InteractionThemeVariant theme, out Color color)
    {
        color = default;
        var value = _settingsService.Get(GetSettingKey(token, theme));
        return !string.IsNullOrWhiteSpace(value) && Color.TryParse(value, out color);
    }

    private static string GetSettingKey(InteractionStyleToken token, InteractionThemeVariant theme) =>
        $"{GetDefinition(token).LegacySettingKey}.{theme.ToString().ToLowerInvariant()}";

    private bool TryGetStoredDouble(string key, out double value)
    {
        var raw = _settingsService.Get(key);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            Initialize();
    }

    private static InteractionThemeVariant ResolveCurrentTheme() => GetApplication().ActualThemeVariant == ThemeVariant.Dark
        ? InteractionThemeVariant.Dark
        : InteractionThemeVariant.Light;

    private static ThemeVariant ToAvaloniaTheme(InteractionThemeVariant theme) => theme == InteractionThemeVariant.Dark
        ? ThemeVariant.Dark
        : ThemeVariant.Light;

    private static Application GetApplication() => Application.Current
        ?? throw new InvalidOperationException("Interaction styles cannot be used before application resources are loaded.");

    private static TokenDefinition GetDefinition(InteractionStyleToken token) => TokenDefinitions.TryGetValue(token, out var definition)
        ? definition
        : throw new ArgumentOutOfRangeException(nameof(token), token, "Unknown interaction style token.");

    private static string FormatColor(Color color) => color.A == byte.MaxValue
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private sealed record TokenDefinition(string LegacySettingKey, string ResourceKey, IReadOnlyList<string> FluentBridgeKeys);
}
