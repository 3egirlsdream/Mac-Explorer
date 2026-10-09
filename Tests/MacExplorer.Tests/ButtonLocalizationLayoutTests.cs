using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

public sealed class ButtonLocalizationLayoutTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedActionButtonsCenterLabelsWhenLanguageChanges(bool dark)
    {
        using var language = new LocalizationService(new MemorySettings(), () => AppLanguage.ChineseSimplified);
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
        foreach (var classes in new[] { "primary compact", "secondary compact", "ghost compact", "danger compact", "secondary" })
        {
            var button = new Button { Width = 180, Height = 44 };
            foreach (var name in classes.Split(' ')) button.Classes.Add(name);
            button.Bind(ContentControl.ContentProperty, (Avalonia.Data.Binding)new LocExtension("ui.148").ProvideValue(null!));
            panel.Children.Add(button);
        }
        var autoButton = new Button { HorizontalAlignment = HorizontalAlignment.Left };
        autoButton.Classes.Add("secondary");
        autoButton.Classes.Add("compact");
        autoButton.Bind(ContentControl.ContentProperty, (Avalonia.Data.Binding)new LocExtension("ui.159").ProvideValue(null!));
        panel.Children.Add(autoButton);
        var window = InputAppearanceTests.CreateWindow(panel, dark);
        var chineseWidth = autoButton.Bounds.Width;
        try
        {
            foreach (var selection in new[] { AppLanguage.ChineseSimplified, AppLanguage.English, AppLanguage.ChineseSimplified })
            {
                language.SetLanguage(selection);
                Dispatcher.UIThread.RunJobs();
                foreach (var button in panel.Children.OfType<Button>())
                {
                    var label = Assert.Single(button.GetVisualDescendants().OfType<TextBlock>());
                    Assert.Equal(ReferenceEquals(button, autoButton)
                        ? selection == AppLanguage.English ? "Enable/disable" : "启用/禁用"
                        : selection == AppLanguage.English ? "Save skill" : "保存技能", label.Text);
                    Assert.True(label.Bounds.Width + button.Padding.Left + button.Padding.Right <= button.Bounds.Width);
                    var center = label.TranslatePoint(new Point(label.Bounds.Width / 2, label.Bounds.Height / 2), button)!.Value;
                    Assert.True(Math.Abs(center.X - button.Bounds.Width / 2) <= 1,
                        $"{button.Classes}, {label.Text}: horizontal center {center.X}, button width {button.Bounds.Width}");
                    Assert.True(Math.Abs(center.Y - button.Bounds.Height / 2) <= 1,
                        $"{button.Classes}, {label.Text}: vertical center {center.Y}, button height {button.Bounds.Height}");
                }
                if (selection == AppLanguage.English) Assert.True(autoButton.Bounds.Width > chineseWidth);
                else Assert.Equal(chineseWidth, autoButton.Bounds.Width);
            }
        }
        finally { window.Close(); }
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new();
        public event Action<string>? SettingChanged;
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public T Get<T>(string key, T defaultValue) => defaultValue;
        public void Set(string key, string value) { _values[key] = value; SettingChanged?.Invoke(key); }
        public void Set<T>(string key, T value) => Set(key, value?.ToString() ?? "");
        public Dictionary<string, string> GetAll() => new(_values);
    }
}
