using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using SkiaSharp;
using System.Xml.Linq;
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

    [AvaloniaFact]
    public async Task PresetsPersistWithoutRemovingTheUsersImageAndLateSelectionsCannotReplaceTheLatest()
    {
        using var fixture = new Fixture();
        await fixture.ImportAsync();
        var managed = fixture.Service.Options.ImageFile;
        var first = fixture.Service.SelectPresetAsync("cyber-circuit");
        var last = fixture.Service.SelectPresetAsync("nature-trellis");
        await Task.WhenAll(first, last);
        Assert.Equal("nature-trellis", fixture.Service.Options.PresetId);
        Assert.True(File.Exists(Path.Combine(fixture.Root, managed)));
        fixture.Service.Update(true, BackgroundImageLayout.Cover, 73);
        Assert.Equal(73, fixture.Service.Options.TextureStrength);
        Assert.Equal(40, fixture.Service.Options.Strength);
        using var restored = new BackgroundThemeService(fixture.Settings, fixture.Root);
        await restored.InitializeAsync();
        Assert.Equal(fixture.Service.Options, restored.Options);
        Assert.True(restored.IsActive);
        await fixture.Service.SelectPresetAsync("");
        Assert.Equal(managed, fixture.Service.Options.ImageFile);
        Assert.Equal("", fixture.Service.Options.PresetId);
        fixture.Service.Reset();
        Assert.False(File.Exists(Path.Combine(fixture.Root, managed)));
    }

    [AvaloniaFact]
    public async Task NineWideVectorPresetsHaveContinuousEdgesAndReadableLightAndDarkPalettes()
    {
        using var fixture = new Fixture();
        Assert.Equal(9, BackgroundThemePresets.All.Count);
        Assert.Equal(3, BackgroundThemePresets.All.Select(p => p.FamilyKey).Distinct().Count());
        Assert.All(BackgroundThemePresets.All.GroupBy(p => p.FamilyKey), family => Assert.Equal(3, family.Count()));
        foreach (var preset in BackgroundThemePresets.All)
        {
            Assert.Equal(BackgroundTextureBlend.Normal, preset.Blend);
            Assert.EndsWith(".svg", preset.TextureAsset);
            using (var stream = AssetLoader.Open(new Uri("avares://MacExplorer/Assets/BackgroundThemes/" + preset.TextureAsset)))
            {
                var svg = XDocument.Load(stream);
                var bounds = svg.Root!.Attribute("viewBox")!.Value.Split(' ')
                    .Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                Assert.Equal(650, bounds[3]);
                Assert.InRange(bounds[2] / bounds[3], 3, 4.1);
                Assert.DoesNotContain(svg.Descendants(), node => node.Name.LocalName == "image");
            }
            await fixture.Service.SelectPresetAsync(preset.Id);
            Assert.True(fixture.Service.IsActive);
            using var artwork = fixture.Service.AcquireArtwork()!;
            Assert.NotNull(artwork.Texture);
            Assert.InRange(artwork.Texture.Width, 1, 4096);
            Assert.InRange(artwork.Texture.Height, 1, 1280);
            Assert.InRange((double)artwork.Texture.Width / artwork.Texture.Height, 3, 4.1);
            // Inspect the SVG's actual decoded boundary pixels on a white base.
            // This also catches alpha discontinuities without allocating two full tiles.
            {
                double seamDifference = 0;
                for (var y = 0; y < artwork.Texture.Height; y++)
                {
                    var a = OverWhite(artwork.Texture.GetPixel(artwork.Texture.Width - 1, y));
                    var b = OverWhite(artwork.Texture.GetPixel(0, y));
                    seamDifference += (Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue)) / 3d;
                }
                Assert.True(seamDifference / artwork.Texture.Height < 5, $"{preset.Id}: visible seam {seamDifference / artwork.Texture.Height:F2}");
            }
            foreach (var dark in new[] { false, true })
            {
                using var frame = BackgroundThemeComposition.Render(artwork.Texture, fixture.Service.Options, 480, 300, dark, CancellationToken.None);
                Assert.Equal(255, frame.GetPixel(100, 100).Alpha);
                var text = Luminance(SKColor.Parse(dark ? "#E5EAF0" : "#303946"));
                for (var y = 0; y < frame.Height; y += 3)
                    for (var x = 0; x < frame.Width; x += 3)
                    {
                        var background = Luminance(frame.GetPixel(x, y));
                        var contrast = (Math.Max(text, background) + .05) / (Math.Min(text, background) + .05);
                        Assert.True(contrast >= 4.5, $"{preset.Id} ({dark}): contrast {contrast:F2} at {x},{y}");
                    }
            }
        }

        static SKColor OverWhite(SKColor color) => new(
            (byte)(255 - color.Alpha + color.Red * color.Alpha / 255),
            (byte)(255 - color.Alpha + color.Green * color.Alpha / 255),
            (byte)(255 - color.Alpha + color.Blue * color.Alpha / 255));

        static double Luminance(SKColor color)
        {
            static double Linear(byte c) => c / 255d <= .04045 ? c / 255d / 12.92 : Math.Pow((c / 255d + .055) / 1.055, 2.4);
            return .2126 * Linear(color.Red) + .7152 * Linear(color.Green) + .0722 * Linear(color.Blue);
        }
    }

    [AvaloniaFact]
    public void FullStrengthPresetsPreserveSourcePixels()
    {
        using var source = new SKBitmap(8, 8);
        source.Erase(new SKColor(36, 125, 213));
        foreach (var preset in BackgroundThemePresets.All)
        {
            using var frame = BackgroundThemeComposition.Render(source,
                new() { PresetId = preset.Id, TextureStrength = 100 }, 8, 8, false, CancellationToken.None);
            Assert.Equal(source.GetPixel(4, 4), frame.GetPixel(4, 4));
        }
    }

    [AvaloniaFact]
    public void PaletteSpansTheWindowAndTextureStrengthDoesNotDimItsColors()
    {
        var options = new BackgroundThemeOptions { PresetId = "cyber-hud", TextureStrength = 0 };
        using var texture = new SKBitmap(8, 8);
        texture.Erase(SKColors.White);
        using var narrow = BackgroundThemeComposition.Render(texture, options, 400, 100, false, CancellationToken.None);
        using var wide = BackgroundThemeComposition.Render(texture, options, 800, 100, false, CancellationToken.None);
        Assert.NotEqual(narrow.GetPixel(10, 50), narrow.GetPixel(110, 50));
        var a = narrow.GetPixel(200, 50); var b = wide.GetPixel(400, 50);
        Assert.InRange(Math.Abs(a.Red - b.Red), 0, 1);
        Assert.InRange(Math.Abs(a.Green - b.Green), 0, 1);
        Assert.InRange(Math.Abs(a.Blue - b.Blue), 0, 1);
        using var withoutTexture = BackgroundThemeComposition.Render(null, options with { TextureStrength = 100 }, 400, 100, false, CancellationToken.None);
        Assert.Equal(narrow.GetPixel(100, 50), withoutTexture.GetPixel(100, 50));
        using var custom = BackgroundThemeComposition.Render(texture, new() { Strength = 0 }, 400, 100, false, CancellationToken.None);
        Assert.Equal(SKColors.White, custom.GetPixel(100, 50));
    }

    [AvaloniaFact]
    public async Task PresetControlsKeepSelectionAndIntensityWhenLanguageChanges()
    {
        using var fixture = new Fixture();
        using var localization = new LocalizationService(fixture.Settings);
        var fluent = new Avalonia.Themes.Fluent.FluentTheme();
        Application.Current!.Styles.Insert(0, fluent);
        var view = new BackgroundThemeSettingsView(fixture.Service);
        var window = new Window { Width = 530, Height = 400, Content = view };
        foreach (var uri in new[] { "Assets/ComponentStyles.axaml", "Views/Dialogs/SettingsStyles.axaml" })
            window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/" + uri)));
        window.Show();
        try
        {
            view.FindControl<ComboBox>("PresetCombo")!.SelectedIndex = 1;
            for (var i = 0; fixture.Service.Options.PresetId.Length == 0 && i < 100; i++) await Task.Delay(10);
            Assert.Equal("cyber-circuit", fixture.Service.Options.PresetId);
            view.FindControl<Slider>("StrengthSlider")!.Value = 35;
            localization.SetLanguage("fr");
            Assert.Equal(35, fixture.Service.Options.TextureStrength);
            Assert.Equal(10, view.FindControl<ComboBox>("PresetCombo")!.ItemCount);
            Assert.Equal(localization["background.family.cyber"] + " · " + localization["background.preset.circuit"],
                view.FindControl<ComboBox>("PresetCombo")!.SelectionBoxItem);
            await fixture.Service.SelectPresetAsync("animal-panda");
            Assert.True(view.FindControl<Border>("StrengthRow")!.IsVisible);
            Assert.True(view.FindControl<Border>("LayoutRow")!.IsVisible);
        }
        finally { window.Close(); Application.Current!.Styles.Remove(fluent); }
    }

    private static Color BrushColor(Control control, string key)
    {
        Assert.True(control.TryFindResource(key, control.ActualThemeVariant, out var brush));
        return Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
    }

    [AvaloniaTheory]
    [InlineData("qq-circuit-pink", "cyber-circuit")]
    [InlineData("qq-circuit-neon", "cyber-hud")]
    [InlineData("qq-confetti-taro", "nature-marigold")]
    [InlineData("qq-dunhuang-red", "nature-trellis")]
    [InlineData("qq-ink-blue", "animal-panda")]
    public async Task DevelopmentPresetsMigrateWhileKeepingCustomImage(string oldId, string newId)
    {
        using var fixture = new Fixture();
        await fixture.ImportAsync();
        var options = fixture.Service.Options with { PresetId = oldId, TextureStrength = 100 };
        fixture.Settings.Set(BackgroundThemeService.SettingKey, System.Text.Json.JsonSerializer.Serialize(options));
        using var restored = new BackgroundThemeService(fixture.Settings, fixture.Root);
        await restored.InitializeAsync();
        Assert.True(restored.IsActive);
        Assert.Equal(newId, restored.Options.PresetId);
        Assert.Equal(BackgroundThemePresets.Find(newId)!.TextureStrength, restored.Options.TextureStrength);
        Assert.Equal(options.ImageFile, restored.Options.ImageFile);
        await restored.SelectPresetAsync("");
        Assert.True(restored.IsActive);
    }

    [AvaloniaFact]
    public void TitlePaletteSamplesBroadArtworkAndPhotoColors()
    {
        using var image = new SKBitmap(96, 48);
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++) image.SetPixel(x, y, x < image.Width / 2 ? SKColors.Red : SKColors.Blue);
        var photo = BackgroundThemeComposition.CreateTitleColors(image, new(), false, CancellationToken.None);
        var preset = BackgroundThemeComposition.CreateTitleColors(image, new() { PresetId = "cyber-circuit" }, false, CancellationToken.None);
        Assert.Equal(photo, preset);
        Assert.Equal(255, photo[0].R);
        Assert.Equal(255, photo[2].B);
        Assert.InRange(photo[1].R, (byte)145, (byte)148);
        Assert.InRange(photo[1].B, (byte)145, (byte)148);
        Assert.All(photo, c => Assert.InRange(c.G, (byte)37, (byte)39));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChromeSurfacesFollowTheWindowThemeAndRestoreWithoutTintingDialogs(bool dark)
    {
        using var fixture = new Fixture();
        var preview = new Border { Classes = { "workspace-preview-surface" }, Height = 100 };
        var chip = new Border { Classes = { "info-tag-chip" }, Height = 30 };
        var toolbar = new Button { Classes = { "ghost", "toolbar-btn" }, Content = "Toolbar", Width = 80 };
        var navigation = new Button { Classes = { "ghost", "workspace-nav-button" }, Content = "Nav" };
        var info = new Button { Classes = { "ghost", "info-panel-icon" }, Content = "Info" };
        var first = new AppWindow { Width = 600, Height = 400, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
            Content = new StackPanel { Children = { preview, chip, toolbar, navigation, info } } };
        var dialog = new DialogWindow { Width = 400, Height = 300, RequestedThemeVariant = first.RequestedThemeVariant };
        first.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        first.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ThemeTokens.axaml")));
        first.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/Styles.axaml")));
        first.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        using var theme = new WindowBackgroundTheme(first, fixture.Service);
        first.Show(); dialog.Show(first);
        try
        {
            var original = BrushColor(first, "TitleBarBackgroundBrush");
            var originalDivider = BrushColor(first, "WorkspaceDividerBrush");
            var originalMuted = BrushColor(first, "TextMutedBrush");
            await fixture.Service.SelectPresetAsync("cyber-hud");
            for (var i = 0; theme.IsRendering && i < 100; i++) await Task.Delay(10);
            Assert.True(theme.IsActive);
            Assert.IsType<LinearGradientBrush>(first.Resources["TitleBarBackgroundBrush"]);
            Assert.Equal(original, BrushColor(dialog, "TitleBarBackgroundBrush"));
            Assert.Equal(originalDivider, BrushColor(dialog, "WorkspaceDividerBrush"));
            Assert.Equal(originalMuted, BrushColor(dialog, "TextMutedBrush"));
            Assert.InRange(BrushColor(first, "WorkspaceDividerBrush").A, (byte)16, (byte)40);
            Assert.InRange(Assert.IsAssignableFrom<ISolidColorBrush>(preview.Background).Color.A, (byte)1, (byte)254);
            Assert.InRange(Assert.IsAssignableFrom<ISolidColorBrush>(chip.Background).Color.A, (byte)1, (byte)254);
            Assert.InRange(BrushColor(first, "ViewModeToggleSelectedBg").A, (byte)1, (byte)254);
            foreach (var button in new[] { toolbar, navigation, info })
            {
                var location = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), first)!.Value;
                first.MouseMove(location);
                Assert.True(button.IsPointerOver);
                var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().Single(v => v.Name == "PART_ContentPresenter");
                Assert.InRange(Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color.A, (byte)1, (byte)254);
                first.MouseDown(location, MouseButton.Left);
                Assert.InRange(Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color.A, (byte)1, (byte)254);
                first.MouseUp(location, MouseButton.Left);
            }
            fixture.Service.Update(false, BackgroundImageLayout.HeightTile, 20);
            Assert.Equal(original, BrushColor(first, "TitleBarBackgroundBrush"));
            Assert.Equal(originalDivider, BrushColor(first, "WorkspaceDividerBrush"));
            Assert.Equal(originalMuted, BrushColor(first, "TextMutedBrush"));
            Assert.Equal(255, Assert.IsAssignableFrom<ISolidColorBrush>(preview.Background).Color.A);
            Assert.Equal(255, Assert.IsAssignableFrom<ISolidColorBrush>(chip.Background).Color.A);
            Assert.Equal(255, BrushColor(first, "ViewModeToggleSelectedBg").A);
        }
        finally { first.Close(); dialog.Close(); }
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
