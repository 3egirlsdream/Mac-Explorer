using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using SkiaSharp;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Smoke", "UI")]
[Trait("Area", "BackgroundTheme")]
public sealed class BackgroundThemeTests
{
    [AvaloniaFact]
    public void HeightTilingKeepsItsScaleWhileWideningAndCoverPreservesAspectRatio()
    {
        var image = new Size(600, 800);
        var tile = BackgroundThemeSurface.CalculateImageRect(image, new Size(1000, 800), BackgroundImageLayout.HeightTile);
        Assert.Equal(new Rect(0, 0, 600, 800), tile);
        Assert.Equal(tile, BackgroundThemeSurface.CalculateImageRect(image, new Size(1800, 800), BackgroundImageLayout.HeightTile));
        Assert.Equal(new Rect(0, 0, 300, 400), BackgroundThemeSurface.CalculateImageRect(image, new Size(1000, 400), BackgroundImageLayout.HeightTile));
        Assert.Equal(new Rect(0, -400, 1200, 1600), BackgroundThemeSurface.CalculateImageRect(image, new Size(1200, 800), BackgroundImageLayout.Cover));
    }

    [AvaloniaFact]
    public async Task ImportIsBoundedAndDurableAndFailedReplacementKeepsThePreviousImage()
    {
        using var fixture = new Fixture();
        await fixture.ImportAsync(2400, 1200);
        Assert.True(fixture.Service.IsActive);
        var original = fixture.Service.Image;
        var managedFile = Path.Combine(fixture.Root, fixture.Service.Options.ImageFile);
        Assert.True(File.Exists(managedFile));
        using (var normalized = SKBitmap.Decode(managedFile))
            Assert.Equal(new SKSizeI(2048, 1024), normalized.Info.Size);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.ImportAsync(new MemoryStream([1, 2, 3]), "broken.png"));
        Assert.Same(original, fixture.Service.Image);
        fixture.Service.Update(true, BackgroundImageLayout.Cover, 63);
        using var restored = new BackgroundThemeService(fixture.Settings, fixture.Root);
        await restored.InitializeAsync();
        Assert.Equal(fixture.Service.Options, restored.Options);
        Assert.True(restored.IsActive);
        fixture.Service.Reset();
        Assert.False(fixture.Service.IsActive);
        Assert.False(File.Exists(managedFile));
    }

    [AvaloniaFact]
    public async Task MainWindowsUpdateTogetherAndDialogSurfacesStayOpaque()
    {
        using var fixture = new Fixture();
        var first = new AppWindow { Width = 600, Height = 400 };
        var second = new AppWindow { Width = 800, Height = 400 };
        var dialog = new DialogWindow { Width = 400, Height = 300 };
        using var firstTheme = new WindowBackgroundTheme(first, fixture.Service);
        using var secondTheme = new WindowBackgroundTheme(second, fixture.Service);
        var material = new BackgroundThemeSurface { Frosted = true };
        first.Content = material;
        first.Show(); second.Show(); dialog.Show();
        try
        {
            await fixture.ImportAsync();
            Assert.Same(fixture.Service, WindowBackgroundTheme.GetService(material));
            Assert.Null(WindowBackgroundTheme.GetService(dialog));
            foreach (var window in new[] { first, second })
                Assert.Equal(Colors.Transparent, BrushColor(window, "WorkspaceContentBrush"));
            Assert.Equal(Colors.White, BrushColor(dialog, "WorkspaceContentBrush"));
            first.RequestedThemeVariant = ThemeVariant.Dark;
            fixture.Service.Update(false, BackgroundImageLayout.HeightTile, 40);
            Assert.Equal(Color.Parse("#1B1D21"), BrushColor(first, "WorkspaceContentBrush"));
            Assert.Equal(Colors.White, BrushColor(second, "WorkspaceContentBrush"));
        }
        finally { first.Close(); second.Close(); dialog.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("zh-CN", false)]
    [InlineData("zh-Hant", true)]
    [InlineData("en", true)]
    [InlineData("fr", false)]
    public async Task SettingsEditAndResetTheSharedThemeWithoutClipping(string language, bool dark)
    {
        using var fixture = new Fixture();
        using var localization = new LocalizationService(fixture.Settings);
        localization.SetLanguage(language);
        await fixture.ImportAsync();
        var fluent = new Avalonia.Themes.Fluent.FluentTheme();
        Application.Current!.Styles.Insert(0, fluent);
        var view = new BackgroundThemeSettingsView(fixture.Service);
        var window = new Window { Width = 530, Height = 350, Content = view,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        foreach (var uri in new[] { "Assets/ComponentStyles.axaml", "Views/Dialogs/SettingsStyles.axaml" })
            window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/" + uri)));
        window.Show();
        try
        {
            view.FindControl<ComboBox>("LayoutCombo")!.SelectedIndex = 1;
            view.FindControl<Slider>("StrengthSlider")!.Value = 72;
            Assert.Equal(BackgroundImageLayout.Cover, fixture.Service.Options.Layout);
            Assert.Equal(72, fixture.Service.Options.Strength);
            localization.SetLanguage(language == "en" ? "fr" : "en");
            Assert.Equal(localization["background.cover"], view.FindControl<ComboBox>("LayoutCombo")!.SelectionBoxItem);
            Assert.Equal(72, fixture.Service.Options.Strength);
            localization.SetLanguage(language);
            view.FindControl<ToggleSwitch>("EnabledToggle")!.IsChecked = false;
            Assert.False(fixture.Service.IsActive);
            view.FindControl<ToggleSwitch>("EnabledToggle")!.IsChecked = true;
            Assert.True(fixture.Service.IsActive);
            window.UpdateLayout();
            foreach (var name in new[] { "ChooseButton", "ResetButton", "LayoutCombo", "StrengthSlider", "StrengthValue" })
            {
                var control = view.FindControl<Control>(name)!;
                var point = control.TranslatePoint(default, window)!.Value;
                Assert.True(point.X >= 0 && point.X + control.Bounds.Width <= window.ClientSize.Width, name);
            }
            view.FindControl<Button>("ResetButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(fixture.Service.IsActive);
            Assert.Null(view.FindControl<Image>("PreviewImage")!.Source);
        }
        finally { window.Close(); Application.Current!.Styles.Remove(fluent); }
    }

    [AvaloniaFact]
    public async Task MissingImageFallsBackAndCanBeReplaced()
    {
        using var fixture = new Fixture();
        fixture.Settings.Set(BackgroundThemeService.SettingKey, "{\"Enabled\":true,\"ImageFile\":\"missing.png\",\"Strength\":200}");
        using var service = new BackgroundThemeService(fixture.Settings, fixture.Root);
        await service.InitializeAsync();
        Assert.False(service.IsActive);
        Assert.Equal("background.unavailable", service.ErrorKey);
        Assert.Equal(100, service.Options.Strength);
        await service.ImportAsync(new MemoryStream(Fixture.Png(80, 60)), "new.png");
        Assert.Null(service.ErrorKey);
        Assert.True(service.IsActive);
    }

    private static Color BrushColor(Control control, string key)
    {
        Assert.True(control.TryFindResource(key, control.ActualThemeVariant, out var brush));
        return Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "background-theme-" + Guid.NewGuid().ToString("N"));
        public ShortcutMemorySettings Settings { get; } = new();
        public BackgroundThemeService Service { get; }
        public Fixture() => Service = new BackgroundThemeService(Settings, Root);
        public Task ImportAsync(int width = 120, int height = 80) => Service.ImportAsync(new MemoryStream(Png(width, height)), "主题 Theme très long filename.png");
        public static byte[] Png(int width, int height)
        {
            using var bitmap = new SKBitmap(width, height);
            bitmap.Erase(SKColors.CornflowerBlue);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 90);
            return data.ToArray();
        }
        public void Dispose() { Service.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
