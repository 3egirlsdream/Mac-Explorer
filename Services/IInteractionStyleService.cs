namespace MacExplorer.Services;

/// <summary>
/// Semantic visual states shared by interactive controls. The names describe
/// how a value is used instead of tying it to a specific control template.
/// </summary>
public enum InteractionStyleToken
{
    Hover,
    Selected,
    SelectedHover,
    TextHighlight
}

public enum InteractionThemeVariant
{
    Light,
    Dark
}

public interface IInteractionStyleService
{
    void Initialize();

    InteractionThemeVariant CurrentTheme { get; }

    string GetColor(InteractionStyleToken token, InteractionThemeVariant theme);
    bool TrySetColor(InteractionStyleToken token, InteractionThemeVariant theme, string value);
    void ResetColors(InteractionThemeVariant theme);
    void ApplyCurrentTheme();

    double GetCornerRadius();
    void SetCornerRadius(double value);
    double GetDisabledOpacity();
    void SetDisabledOpacity(double value);
    void ResetAll();
}
