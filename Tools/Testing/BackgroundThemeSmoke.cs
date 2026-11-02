#if DEBUG
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using MacExplorer.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace MacExplorer.Tools.Testing;

internal static class BackgroundThemeSmoke
{
    public static async Task RunAsync(MainWindow owner)
    {
        var root = RuntimePaths.TestRoot ?? throw new InvalidOperationException("An isolated profile is required.");
        var output = Path.Combine(root, "background-theme");
        Directory.CreateDirectory(output);
        var service = App.Services.GetRequiredService<BackgroundThemeService>();
        var checks = new List<string>();
        var timings = new Dictionary<string, double>();
        string? error = null;
        SettingsDialog? settings = null;
        MainWindow? second = null;
        try
        {
            var files = Path.Combine(root, "Documents", "Theme fixture");
            Directory.CreateDirectory(files);
            for (var i = 0; i < 80; i++)
                await File.WriteAllTextAsync(Path.Combine(files, $"{i + 1:00} — 背景主题 Theme demonstration.txt"), "Background theme test.");
            var source = Path.Combine(output, "theme.png");
            CreateImage(source);
            await using (var stream = File.OpenRead(source)) await service.ImportAsync(stream, "theme.png");
            service.Update(true, 50);
            if (owner.DataContext is not MainWindowViewModel model) throw new InvalidOperationException("Missing main model.");
            owner.MinWidth = 640;
            owner.Width = 1280; owner.Height = 800;
            await model.FileList.NavigateToAsync(files);
            await Task.Delay(500);
            foreach (var dark in new[] { false, true })
            {
                owner.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                var prefix = dark ? "dark" : "light";
                owner.Width = 1280;
                model.FileList.IsInfoPanelVisible = true;
                await CaptureAsync(owner, prefix + "-files");
                await AssertBackgroundBoundariesAsync();
                await CapturePopupStatesAsync(prefix);
                model.FileList.IsInfoPanelVisible = false;
                owner.Width = 800;
                await CaptureAsync(owner, prefix + "-compact");
                owner.Width = 1600;
                await CaptureAsync(owner, prefix + "-wide");
                owner.Width = 1280;
                await model.FileList.NavigateToAsync(VirtualPath.Home);
                await CaptureAsync(owner, prefix + "-home");
                await model.FileList.NavigateToAsync(files);
            }
            checks.Add("Light/dark, file list, information pane, home, compact and wide render snapshots");
            checks.Add("Active tab pixels match the shared window background");
            checks.Add("Info divider keeps the original texture on its left; toolbar corners and status bar reveal the original background");
            checks.Add("Toolbar and info frost use the same window coordinates, scale and opacity as the original image");
            checks.Add("Titlebar uses a texture-free palette; preview cards and toolbar controls reveal the themed surface");
            checks.Add("Toolbar, list header, information pane and status separators use low-opacity theme strokes");
            model.FileList.IsInfoPanelVisible = true;
            foreach (var preset in BackgroundThemePresets.All)
            {
                await service.SelectPresetAsync(preset.Id);
                foreach (var dark in new[] { false, true })
                {
                    owner.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                    await CaptureAsync(owner, preset.Id + (dark ? "-dark" : "-light"));
                    WindowBackgroundTheme.GetController(owner)!.Frame!.Image.Save(
                        Path.Combine(output, preset.Id + (dark ? "-dark-artwork.png" : "-light-artwork.png")));
                    await AssertBackgroundBoundariesAsync();
                    if (preset.Id == "cyber-circuit") await CapturePopupStatesAsync("preset-" + (dark ? "dark" : "light"));
                }
            }
            checks.Add("All nine wide SVG presets render in light/dark with continuous clear/frosted surfaces");
            checks.Add("Custom image and preset light/dark selection, context-menu hover, nested menu and sort dropdown keep their translucent glass material");
            await service.SelectPresetAsync("");
            var bitmap = service.Image;
            owner.Width = 1500; owner.Height = 900;
            await CaptureAsync(owner, "resized");
            if (!ReferenceEquals(bitmap, service.Image)) throw new InvalidOperationException("Resize decoded a new image.");
            var list = owner.GetVisualDescendants().OfType<ScrollViewer>().First(v => v.Name == "FastListHost" && v.IsEffectivelyVisible);
            list.Offset = new Vector(0, 600);
            await CaptureAsync(owner, "scrolled");
            if (!ReferenceEquals(bitmap, service.Image)) throw new InvalidOperationException("Scrolling decoded a new image.");
            checks.Add("Resize and scrolling retain the shared image");
            service.Update(true, 50);
            await CaptureAsync(owner, "height-tile");
            second = App.OpenNewWindow(files);
            await Task.Delay(250);
            service.Update(false, 50);
            if (owner.Resources.ContainsKey("WorkspaceContentBrush") || second.Resources.ContainsKey("WorkspaceContentBrush"))
                throw new InvalidOperationException("Disabling the theme did not restore both windows.");
            await CaptureAsync(owner, "disabled-default");
            service.Update(true, 50);
            if (!ReferenceEquals(WindowBackgroundTheme.GetService(second), service)) throw new InvalidOperationException("New window uses another theme.");
            second.Close(); second = null;
            checks.Add("New window shares theme; disabling restores both windows");

            owner.Width = 1280; owner.Height = 800;
            settings = new SettingsDialog();
            settings.Show(owner);
            var tabs = settings.FindControl<TabControl>("SettingsTabs")!;
            tabs.SelectedItem = tabs.Items.OfType<LazyTabItem>().Single(p => p.PageKey == "appearance");
            settings.UpdateLayout();
            var themeSettings = settings.GetVisualDescendants().OfType<BackgroundThemeSettingsView>().Single();
            for (var attempt = 0; !themeSettings.ArePreviewsReady && attempt < 500; attempt++) await Task.Delay(20);
            if (!themeSettings.ArePreviewsReady) throw new InvalidOperationException("Packaged swatch previews did not load.");
            timings["themePreviewLoadMilliseconds"] = themeSettings.PreviewLoadDuration.TotalMilliseconds;
            themeSettings.GetSwatch("cyber-circuit").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            for (var attempt = 0; (service.Options.PresetId != "cyber-circuit" || themeSettings.OpenPalette == null) && attempt < 100; attempt++) await Task.Delay(20);
            themeSettings.ClosePalette();
            if (service.Options.PresetId != "cyber-circuit") throw new InvalidOperationException("Settings did not select the preset.");
            var language = App.Services.GetRequiredService<ILocalizationService>();
            foreach (var id in new[] { "zh-CN", "zh-Hant", "en", "fr" })
            {
                language.SetLanguage(id);
                settings.RequestedThemeVariant = id is "zh-Hant" or "en" ? ThemeVariant.Dark : ThemeVariant.Light;
                await CaptureAsync(settings, "settings-" + id);
                if (!ReferenceEquals(themeSettings.FindControl<Image>("PreviewImage")!.Source, service.GetPreview(settings.ActualThemeVariant == ThemeVariant.Dark)))
                    throw new InvalidOperationException("Preset preview did not follow the settings appearance.");
                if (WindowBackgroundTheme.GetService(settings) != null) throw new InvalidOperationException("Dialog inherited the main-window theme.");
            }
            var rows = themeSettings.FindControl<Panel>("PatternRows")!;
            if (rows.Children.Count != 9) throw new InvalidOperationException("Settings must offer nine pattern cards.");
            foreach (var preset in BackgroundThemePresets.All)
            {
                themeSettings.GetSwatch(preset.Id).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                for (var attempt = 0; (service.Options.PresetId != preset.Id || !rows.IsEnabled || themeSettings.OpenPalette == null || themeSettings.OpenPalettePattern != preset.Id) && attempt < 250; attempt++) await Task.Delay(20);
                if (service.Options.PresetId != preset.Id || !rows.IsEnabled)
                    throw new InvalidOperationException($"Settings did not finish selecting {preset.Id}.");
            }
            await CaptureAsync(settings, "settings-fr-long-preset");
            language.SetLanguage("zh-CN");
            var customSwatch = themeSettings.GetSwatch("cyber-circuit");
            customSwatch.BringIntoView();
            customSwatch.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            for (var attempt = 0; (!rows.IsEnabled || service.Options.PresetId != "cyber-circuit" || themeSettings.OpenPalette == null) && attempt < 250; attempt++) await Task.Delay(20);
            await CaptureAsync(settings, "settings-color-palette");
            var palettePopup = themeSettings.FindControl<Popup>("ColorPalettePopup")!;
            await CaptureAsync(palettePopup.Child!, "theme-palette-popover");
            var swatchPoint = customSwatch.PointToScreen(default);
            var surfacePoint = palettePopup.Child!.PointToScreen(default);
            if (Math.Abs(surfacePoint.X - swatchPoint.X) > 4)
                throw new InvalidOperationException($"Palette is not aligned to its card: {surfacePoint} / {swatchPoint}.");
            var paletteView = themeSettings.OpenPalette ?? throw new InvalidOperationException("The custom swatch did not open the palette.");
            if (paletteView.GetVisualDescendants().OfType<Button>().Count(item => item.Classes.Contains("color-palette-swatch")) != BackgroundThemePresets.All[0].ColorOptions.Length)
                throw new InvalidOperationException("The configured palette did not render.");
            await CaptureAsync(paletteView, "color-palette-content");
            settings.RequestedThemeVariant = ThemeVariant.Dark;
            await CaptureAsync(settings, "settings-theme-palette-dark");
            settings.Width = 780;
            themeSettings.GetSwatch("cyber-city").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            for (var attempt = 0; (service.Options.PresetId != "cyber-city" || themeSettings.OpenPalettePattern != "cyber-city" || themeSettings.OpenPalette == null) && attempt < 250; attempt++) await Task.Delay(20);
            await CaptureAsync(settings, "settings-theme-palette-compact");
            var popupOrigin = settings.PointToClient(palettePopup.Child!.PointToScreen(default));
            if (popupOrigin.X < 0 || popupOrigin.X + palettePopup.Child.Bounds.Width > settings.ClientSize.Width ||
                popupOrigin.Y < 0 || popupOrigin.Y + palettePopup.Child.Bounds.Height > settings.ClientSize.Height)
                throw new InvalidOperationException("Compact palette extends beyond the settings window.");
            settings.RequestedThemeVariant = ThemeVariant.Light;
            settings.Width = 940;
            customSwatch.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            for (var attempt = 0; (service.Options.PresetId != "cyber-circuit" || themeSettings.OpenPalettePattern != "cyber-circuit" || themeSettings.OpenPalette == null) && attempt < 250; attempt++) await Task.Delay(20);
            await CaptureAsync(settings, "settings-theme-palette-zh-CN");
            checks.Add("Theme palette uses one card-aligned surface, stays inside compact windows and supports light/dark appearance");
            service.UpdateCustomColor("cyber-circuit", "#CE804A");
            await CaptureAsync(owner, "custom-palette-main-window");
            themeSettings.ClosePalette();
            themeSettings.FindControl<Button>("UseImageButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            for (var attempt = 0; service.Options.PresetId.Length != 0 && attempt < 100; attempt++) await Task.Delay(20);
            if (service.Options.PresetId.Length != 0 || service.Options.DisplayName != "theme.png")
                throw new InvalidOperationException("Settings did not restore the custom image.");
            checks.Add("Settings switch through all nine SVG presets and restore the retained custom image");
            language.SetLanguage("zh-CN");
            var interactionPalette = settings.GetVisualDescendants().OfType<ColorPalette>().Single(item => item.Name == "InteractionPalette");
            interactionPalette.BringIntoView();
            var interaction = App.Services.GetRequiredService<IInteractionStyleService>();
            var editingTheme = settings.GetVisualDescendants().OfType<ComboBox>().Single(item => item.Name == "InteractionThemeVariantCombo").SelectedIndex == 0
                ? InteractionThemeVariant.Light : InteractionThemeVariant.Dark;
            var originalHover = interaction.GetColor(InteractionStyleToken.Hover, editingTheme);
            interactionPalette.GetVisualDescendants().OfType<Button>().Single(item => Equals(item.Tag, "#3B82F6"))
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            if (interaction.GetColor(InteractionStyleToken.Hover, editingTheme) != "#3B82F6")
                throw new InvalidOperationException("The shared palette did not update the interaction color.");
            interactionPalette.InputBox.Text = "#AB";
            await Task.Delay(50);
            if (interaction.GetColor(InteractionStyleToken.Hover, editingTheme) != "#3B82F6")
                throw new InvalidOperationException("A partial hex draft changed the saved color.");
            interactionPalette.InputBox.Text = "#22C55E";
            interactionPalette.InputBox.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
            if (interaction.GetColor(InteractionStyleToken.Hover, editingTheme) != "#22C55E")
                throw new InvalidOperationException("The palette did not commit the completed hex color.");
            await CaptureAsync(settings, "settings-shared-color-palette");
            interactionPalette.FindControl<Button>("ResetButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            if (interaction.GetColor(InteractionStyleToken.Hover, editingTheme) != originalHover)
                throw new InvalidOperationException("The interaction palette did not restore its configured defaults.");
            checks.Add("Shared theme/interaction palette shows colored swatches, commits complete hex input and restores defaults");
            settings.Close(); settings = null;
            language.SetLanguage("zh-CN");
            checks.Add("Settings retain default surfaces in four languages");
            service.Reset();
            if (service.Image != null || !File.Exists(source)) throw new InvalidOperationException("Reset did not release the theme or removed the source.");
            checks.Add("Reset releases the imported theme and preserves the source");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            settings?.Close(); second?.Close();
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { success = error == null, checks, timings, error }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Background theme smoke: {output}; success={error == null}");
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime) lifetime.Shutdown(error == null ? 0 : 1);
        }

        async Task CapturePopupStatesAsync(string prefix)
        {
            var model = (MainWindowViewModel)owner.DataContext!;
            model.FileList.SelectEntry(model.FileList.Entries.First());
            await CaptureAsync(owner, prefix + "-selected");
            var fileView = owner.GetVisualDescendants().OfType<FileListView>().Single(v => v.IsEffectivelyVisible);
            var anchor = fileView.FindControl<Button>("NameSortButton")!;
            var show = typeof(FileListView).GetMethod("ShowMenuAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task)show.Invoke(fileView, [anchor, null])!;
            var menu = (ContextMenu)typeof(FileListView).GetField("_openMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(fileView)!;
            menu.Close();
            menu.PlacementTarget = anchor;
            menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
            menu.Open(anchor);
            try
            {
                await Task.Delay(150);
                var parent = menu.Items.OfType<MenuItem>().First(item => item.IsEnabled && item.Items.Count > 0);
                parent.IsSelected = true;
                await CaptureAsync(menu, prefix + "-context-menu");
                AssertPopup(menu, "context-menu");
                parent.IsSubMenuOpen = true;
                await Task.Delay(150);
                var submenu = parent.GetVisualDescendants().OfType<Popup>().Single();
                await CaptureAsync(submenu.Child!, prefix + "-submenu");
                AssertPopup(submenu.Child!, "submenu");
            }
            finally { menu.Close(); fileView.DismissContextMenu(); }
            var toolbar = owner.GetVisualDescendants().OfType<FinderToolbar>().Single(v => v.IsEffectivelyVisible);
            var dropdown = toolbar.FindControl<Popup>("SortDropdown")!;
            dropdown.PlacementTarget = toolbar.FindControl<Button>("SortButton")!;
            dropdown.IsOpen = true;
            try
            {
                await Task.Delay(150);
                await CaptureAsync(dropdown.Child!, prefix + "-sort-dropdown");
                AssertPopup(dropdown.Child!, "sort-dropdown");
            }
            finally { dropdown.IsOpen = false; model.FileList.ClearSelection(); }

            void AssertPopup(Control root, string name)
            {
                var glass = root.GetVisualDescendants().OfType<LiquidGlassAvaloniaUI.LiquidGlassSurface>().Single();
                if (glass.SurfaceColor.A is 0 or > 240 || glass.BlurRadius != 8 || !glass.CaptureForeground)
                    throw new InvalidOperationException("Popup did not keep its translucent foreground glass material.");
                var provider = typeof(LiquidGlassAvaloniaUI.LiquidGlassSurface).Assembly.GetType("LiquidGlassAvaloniaUI.LiquidGlassBackdropProvider")!;
                var snapshot = provider.GetMethod("TryGetSnapshot")!.Invoke(null, [glass])
                    ?? throw new InvalidOperationException($"Popup has no page backdrop: root={provider.GetMethod("GetBackdropRoot", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [glass])}, visible={glass.IsEffectivelyVisible}, bounds={glass.Bounds}, screen={glass.PointToScreen(default)}, owner={owner.PointToScreen(default)}, size={owner.ClientSize}, transformed={glass.GetTransformedBounds()}");
                var type = snapshot.GetType();
                if (!(bool)type.GetMethod("TryAddLease")!.Invoke(snapshot, null)!)
                    throw new InvalidOperationException("Popup page backdrop was already released.");
                try
                {
                    var image = (SKImage)type.GetProperty("Image")!.GetValue(snapshot)!;
                    using var pixels = SKBitmap.FromImage(image);
                    double red = 0, green = 0, blue = 0;
                    var count = 0;
                    for (var y = 0; y < pixels.Height; y += 8)
                        for (var x = 0; x < pixels.Width; x += 8)
                        {
                            var color = pixels.GetPixel(x, y);
                            if (color.Alpha < 230) continue;
                            red += color.Red; green += color.Green; blue += color.Blue; count++;
                        }
                    if (count < pixels.Width * pixels.Height / 256 ||
                        Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)) < count * 4)
                        throw new InvalidOperationException("Popup sampled an empty or neutral region instead of the colored page.");
                    using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
                    File.WriteAllBytes(Path.Combine(output, prefix + "-" + name + "-backdrop.png"), encoded.ToArray());
                }
                finally { type.GetMethod("ReleaseLease")!.Invoke(snapshot, null); }

            }
        }

        async Task CaptureAsync(Control control, string name)
        {
            control.UpdateLayout();
            await Task.Delay(180);
            var controller = WindowBackgroundTheme.GetController(control);
            for (var attempt = 0; controller?.IsRendering == true && attempt < 250; attempt++) await Task.Delay(20);
            if (controller is { IsRendering: true }) throw new TimeoutException("Theme composition did not settle.");
            var visual = ElementComposition.GetElementVisual(control)!;
            await visual.Compositor.RequestCommitAsync();
            using var snapshot = await visual.Compositor.CreateCompositionVisualSnapshot(visual, 1);
            snapshot.Save(Path.Combine(output, name + ".png"));
        }

        async Task AssertBackgroundBoundariesAsync()
        {
            var tabs = owner.FindControl<ListBox>("TabList")!;
            var tab = tabs.ContainerFromIndex(tabs.SelectedIndex)!;
            var point = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, 10), owner)!.Value;
            var windowVisual = ElementComposition.GetElementVisual(owner)!;
            var backgroundVisual = ElementComposition.GetElementVisual(owner.BackgroundContent!)!;
            using var window = await windowVisual.Compositor.CreateCompositionVisualSnapshot(windowVisual, 1);
            using var background = await backgroundVisual.Compositor.CreateCompositionVisualSnapshot(backgroundVisual, 1);
            AssertOriginalAt(point, "Active tab");
            var title = owner.GetVisualDescendants().OfType<WindowTitleBar>().Single().FindControl<Grid>("TitleBarSurface")!;
            var colors = WindowBackgroundTheme.GetController(owner)!.Frame!.TitleColors;
            foreach (var x in new[] { 0.6, 0.85 })
            {
                var location = title.TranslatePoint(new Point(title.Bounds.Width * x, 12), owner)!.Value;
                var amount = (float)((x - 0.5) * 2);
                var left = colors[1]; var right = colors[2];
                AssertColorAt(location, new SKColor((byte)(left.R + (right.R - left.R) * amount),
                    (byte)(left.G + (right.G - left.G) * amount), (byte)(left.B + (right.B - left.B) * amount)), "Titlebar palette");
            }
            if (!owner.TryFindResource("ViewModeToggleSelectedBg", owner.ActualThemeVariant, out var selected)
                || selected is not ISolidColorBrush { Color.A: > 0 and < 255 })
                throw new InvalidOperationException("Toolbar selection is not translucent.");
            var workspace = owner.GetVisualDescendants().OfType<ExplorerWorkspaceView>().Single(v => v.IsEffectivelyVisible);
            var info = workspace.FindControl<BackgroundThemeSurface>("InfoPanelMaterial")!;
            foreach (var y in new[] { 10d, 80d, 180d })
                AssertOriginalAt(info.TranslatePoint(new Point(-3, y), owner)!.Value, "Info divider's left edge");
            var toolbar = workspace.FindControl<Border>("ToolbarMaterialHost")!;
            foreach (var corner in new[] { new Point(1, 1), new Point(toolbar.Bounds.Width - 2, 1),
                new Point(1, toolbar.Bounds.Height - 2), new Point(toolbar.Bounds.Width - 2, toolbar.Bounds.Height - 2) })
                AssertOriginalAt(toolbar.TranslatePoint(corner, owner)!.Value, "Rounded toolbar corner");
            var status = workspace.FindControl<Border>("StatusBar")!;
            AssertOriginalAt(status.TranslatePoint(new Point(status.Bounds.Width * 0.65, status.Bounds.Height / 2), owner)!.Value, "Status bar");
            var infoView = workspace.GetVisualDescendants().OfType<InfoPanelView>().Single(v => v.IsEffectivelyVisible);
            var fileView = workspace.GetVisualDescendants().OfType<FileListView>().Single(v => v.IsEffectivelyVisible);
            AssertDivider(status.BorderBrush, "Status bar");
            AssertDivider(infoView.FindControl<Border>("InfoPanelRoot")!.BorderBrush, "Information pane");
            AssertDivider(fileView.FindControl<Border>("ListHeaderPanel")!.BorderBrush, "File list header");
            foreach (var separator in toolbar.GetVisualDescendants().OfType<Separator>().Where(v => v.Classes.Contains("toolbar-separator")))
                AssertDivider(separator.Background, "Toolbar");

            var backdrop = (BackgroundThemeSurface)owner.BackgroundContent!;
            backdrop.Frosted = true;
            try
            {
                await Task.Delay(60);
                await backgroundVisual.Compositor.RequestCommitAsync();
                using var frosted = await backgroundVisual.Compositor.CreateCompositionVisualSnapshot(backgroundVisual, 1);
                AssertMatches(frosted, toolbar.TranslatePoint(new Point(toolbar.Bounds.Width / 2, 3), owner)!.Value, "Toolbar frost");
                foreach (var y in new[] { 50d, 300d, 500d })
                    AssertMatches(frosted, info.TranslatePoint(new Point(8, y), owner)!.Value, "Info frost");
                var preview = infoView.FindControl<Border>("PreviewImageArea")!;
                var location = preview.TranslatePoint(new Point(preview.Bounds.Width / 2, 20), owner)!.Value;
                var under = Sample(frosted, location.X / frosted.Size.Width, location.Y / frosted.Size.Height);
                if (preview.Background is not ISolidColorBrush { Color.A: > 0 and < 255 } surface)
                    throw new InvalidOperationException("Information preview is not translucent.");
                var alpha = surface.Color.A / 255f;
                AssertColorAt(location, new SKColor((byte)(under.Red * (1 - alpha) + surface.Color.R * alpha),
                    (byte)(under.Green * (1 - alpha) + surface.Color.G * alpha),
                    (byte)(under.Blue * (1 - alpha) + surface.Color.B * alpha)), "Information preview surface");
            }
            finally { backdrop.Frosted = false; }

            void AssertOriginalAt(Point location, string label)
                => AssertMatches(background, location, label);

            void AssertDivider(IBrush? brush, string label)
            {
                if (brush is not ISolidColorBrush { Color.A: > 0 and <= 40 })
                    throw new InvalidOperationException($"{label} divider is not a low-opacity stroke.");
            }

            void AssertMatches(Avalonia.Media.Imaging.Bitmap reference, Point location, string label)
            {
                var expected = Sample(reference, location.X / reference.Size.Width, location.Y / reference.Size.Height);
                AssertColorAt(location, expected, label);
            }

            void AssertColorAt(Point location, SKColor expected, string label)
            {
                var actual = Sample(window, location.X / window.Size.Width, location.Y / window.Size.Height);
                if (Math.Abs(actual.Red - expected.Red) > 3 || Math.Abs(actual.Green - expected.Green) > 3 || Math.Abs(actual.Blue - expected.Blue) > 3)
                    throw new InvalidOperationException($"{label} background is discontinuous: {actual} / {expected}.");
            }
        }
    }

    private static SKColor Sample(Avalonia.Media.Imaging.Bitmap bitmap, double x, double y)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        using var decoded = SKBitmap.Decode(stream.ToArray());
        return decoded.GetPixel((int)(decoded.Width * x), (int)(decoded.Height * y));
    }

    private static void CreateImage(string path)
    {
        using var surface = SKSurface.Create(new SKImageInfo(720, 960));
        using var gradient = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 960),
            [SKColor.Parse("#B8C8EE"), SKColor.Parse("#8DD7CB"), SKColor.Parse("#E4C2D8")], null, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = gradient, IsAntialias = true };
        surface.Canvas.DrawRect(new SKRect(0, 0, 720, 960), paint);
        using var lines = new SKPaint { Color = new SKColor(255, 255, 255, 110), Style = SKPaintStyle.Stroke, StrokeWidth = 8, IsAntialias = true };
        for (var y = -180; y < 1140; y += 240)
            for (var x = 0; x <= 720; x += 240)
                surface.Canvas.DrawCircle(x, y, 90, lines);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(path, data.ToArray());
    }
}
#endif
