using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

public sealed class InteractionStyleServiceTests
{
    [AvaloniaFact]
    public void ColorsAreConfiguredAndAppliedIndependentlyForEachTheme()
    {
        var settings = new MemorySettingsService();
        var service = new InteractionStyleService(settings);

        service.Initialize();

        Assert.Equal("#ECEEF2", service.GetColor(InteractionStyleToken.Hover, InteractionThemeVariant.Light));
        Assert.Equal("#2B3038", service.GetColor(InteractionStyleToken.Hover, InteractionThemeVariant.Dark));
        Assert.Equal("#E5EEFC", service.GetColor(InteractionStyleToken.Selected, InteractionThemeVariant.Light));
        Assert.Equal("#283E61", service.GetColor(InteractionStyleToken.Selected, InteractionThemeVariant.Dark));
        Assert.Equal("#D8E6FA", service.GetColor(InteractionStyleToken.SelectedHover, InteractionThemeVariant.Light));
        Assert.Equal("#324C74", service.GetColor(InteractionStyleToken.SelectedHover, InteractionThemeVariant.Dark));

        Assert.True(service.TrySetColor(InteractionStyleToken.Hover, InteractionThemeVariant.Light, "#123456"));
        Assert.True(service.TrySetColor(InteractionStyleToken.Hover, InteractionThemeVariant.Dark, "#654321"));
        Assert.True(service.TrySetColor(InteractionStyleToken.Selected, InteractionThemeVariant.Light, "#112233"));
        Assert.True(service.TrySetColor(InteractionStyleToken.Selected, InteractionThemeVariant.Dark, "#445566"));
        Assert.True(service.TrySetColor(InteractionStyleToken.SelectedHover, InteractionThemeVariant.Light, "#223344"));
        Assert.True(service.TrySetColor(InteractionStyleToken.SelectedHover, InteractionThemeVariant.Dark, "#556677"));
        Assert.True(service.TrySetColor(InteractionStyleToken.TextHighlight, InteractionThemeVariant.Light, "#334455"));
        Assert.True(service.TrySetColor(InteractionStyleToken.TextHighlight, InteractionThemeVariant.Dark, "#667788"));
        Assert.Equal("#123456", settings.Get("interaction.hover.light"));
        Assert.Equal("#654321", settings.Get("interaction.hover.dark"));
        Assert.Equal("#112233", settings.Get("interaction.selected.light"));
        Assert.Equal("#445566", settings.Get("interaction.selected.dark"));
        Assert.Equal("#223344", settings.Get("interaction.selected_hover.light"));
        Assert.Equal("#556677", settings.Get("interaction.selected_hover.dark"));
        Assert.Equal("#334455", settings.Get("interaction.text_highlight.light"));
        Assert.Equal("#667788", settings.Get("interaction.text_highlight.dark"));

        var application = Assert.IsAssignableFrom<Application>(Application.Current);
        application.RequestedThemeVariant = ThemeVariant.Light;
        Dispatcher.UIThread.RunJobs();
        AssertColorResource("InteractionHoverBrush", "#FF123456");
        AssertColorResource("ButtonBackgroundPointerOver", "#FF123456");
        AssertColorResource("TextControlBackgroundPointerOver", "#FF123456");
        AssertColorResource("InteractionSelectedBrush", "#FF112233");
        AssertColorResource("FocusRingBrush", "#FF2463D4");
        AssertColorResource("TextControlBorderBrushFocused", "#FF2463D4");
        AssertColorResource("ComboBoxBackgroundBorderBrushFocused", "#FF2463D4");
        AssertColorResource("InteractionSelectedHoverBrush", "#FF223344");
        AssertColorResource("ListBoxItemBackgroundSelectedPointerOver", "#FF223344");
        AssertColorResource("InteractionTextHighlightBrush", "#FF334455");
        AssertColorResource("TextControlSelectionHighlightColor", "#FF334455");

        application.RequestedThemeVariant = ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();
        AssertColorResource("InteractionHoverBrush", "#FF654321");
        AssertColorResource("ButtonBackgroundPointerOver", "#FF654321");
        AssertColorResource("TextControlBackgroundPointerOver", "#FF654321");
        AssertColorResource("InteractionSelectedBrush", "#FF445566");
        AssertColorResource("FocusRingBrush", "#FF8AB4FF");
        AssertColorResource("TextControlBorderBrushFocused", "#FF8AB4FF");
        AssertColorResource("ComboBoxBackgroundBorderBrushFocused", "#FF8AB4FF");
        AssertColorResource("InteractionSelectedHoverBrush", "#FF556677");
        AssertColorResource("ListBoxItemBackgroundSelectedPointerOver", "#FF556677");
        AssertColorResource("InteractionTextHighlightBrush", "#FF667788");
        AssertColorResource("TextControlSelectionHighlightColor", "#FF667788");

        service.ResetColors(InteractionThemeVariant.Dark);
        Assert.Equal("#123456", service.GetColor(InteractionStyleToken.Hover, InteractionThemeVariant.Light));
        Assert.Equal("#2B3038", service.GetColor(InteractionStyleToken.Hover, InteractionThemeVariant.Dark));
        AssertColorResource("InteractionHoverBrush", "#FF2B3038");

        application.RequestedThemeVariant = ThemeVariant.Light;
        Dispatcher.UIThread.RunJobs();
        service.ResetColors(InteractionThemeVariant.Light);
        service.ApplyCurrentTheme();
    }

    [AvaloniaFact]
    public void InvalidColorDoesNotReplaceTheThemeSpecificToken()
    {
        var service = new InteractionStyleService(new MemorySettingsService());
        service.Initialize();

        Assert.False(service.TrySetColor(InteractionStyleToken.Pressed, InteractionThemeVariant.Dark, "not-a-color"));
        Assert.Equal("#363D48", service.GetColor(InteractionStyleToken.Pressed, InteractionThemeVariant.Dark));
    }

    [AvaloniaFact]
    public void FormerFocusSettingsAreMigratedToTheSelectedToken()
    {
        var settings = new MemorySettingsService();
        settings.Set("interaction.focus.light", "#80112233");
        settings.Set("interaction.focus.dark", "#80445566");

        var service = new InteractionStyleService(settings);
        service.Initialize();

        Assert.Equal("#80112233", service.GetColor(InteractionStyleToken.Selected, InteractionThemeVariant.Light));
        Assert.Equal("#80445566", service.GetColor(InteractionStyleToken.Selected, InteractionThemeVariant.Dark));
        Assert.Equal(string.Empty, settings.Get("interaction.focus"));
        Assert.Equal(string.Empty, settings.Get("interaction.focus.light"));
        Assert.Equal(string.Empty, settings.Get("interaction.focus.dark"));
    }

    private static void AssertColorResource(string key, string expected)
    {
        var application = Assert.IsAssignableFrom<Application>(Application.Current);
        Assert.True(application.TryGetResource(key, application.ActualThemeVariant, out var resource));
        Assert.Equal(Color.Parse(expected), Assert.IsAssignableFrom<ISolidColorBrush>(resource).Color);
    }

    private sealed class MemorySettingsService : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public T Get<T>(string key, T defaultValue) => Get(key) is { } value && typeof(T) == typeof(string)
            ? (T)(object)value
            : defaultValue;

        public void Set(string key, string value) => _values[key] = value;

        public void Set<T>(string key, T value) => _values[key] = value?.ToString() ?? string.Empty;

        public Dictionary<string, string> GetAll() => new(_values, StringComparer.OrdinalIgnoreCase);
    }
}
