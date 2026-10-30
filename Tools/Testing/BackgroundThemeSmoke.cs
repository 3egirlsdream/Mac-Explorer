#if DEBUG
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
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
            var clearColor = Sample(service.Image!, 0.5, 0.5);
            var frostedColor = Sample(service.FrostedImage!, 0.5, 0.5);
            if (Math.Abs(clearColor.Red - frostedColor.Red) > 14 || Math.Abs(clearColor.Blue - frostedColor.Blue) > 14)
                throw new InvalidOperationException("Frosted texture changed the image's color channels.");
            checks.Add("Frosted texture preserves image colors");
            service.Update(true, BackgroundImageLayout.HeightTile, 50);
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
            var bitmap = service.Image;
            owner.Width = 1500; owner.Height = 900;
            await CaptureAsync(owner, "resized");
            if (!ReferenceEquals(bitmap, service.Image)) throw new InvalidOperationException("Resize decoded a new image.");
            var list = owner.GetVisualDescendants().OfType<ScrollViewer>().First(v => v.Name == "FastListHost" && v.IsEffectivelyVisible);
            list.Offset = new Vector(0, 600);
            await CaptureAsync(owner, "scrolled");
            if (!ReferenceEquals(bitmap, service.Image)) throw new InvalidOperationException("Scrolling decoded a new image.");
            checks.Add("Resize and scrolling retain the shared image");
            service.Update(true, BackgroundImageLayout.Cover, 50);
            await CaptureAsync(owner, "cover");
            second = App.OpenNewWindow(files);
            await Task.Delay(250);
            service.Update(false, BackgroundImageLayout.Cover, 50);
            if (owner.Resources.ContainsKey("WorkspaceContentBrush") || second.Resources.ContainsKey("WorkspaceContentBrush"))
                throw new InvalidOperationException("Disabling the theme did not restore both windows.");
            await CaptureAsync(owner, "disabled-default");
            service.Update(true, BackgroundImageLayout.HeightTile, 50);
            if (!ReferenceEquals(WindowBackgroundTheme.GetService(second), service)) throw new InvalidOperationException("New window uses another theme.");
            second.Close(); second = null;
            checks.Add("New window shares theme; disabling restores both windows");

            owner.Width = 1280; owner.Height = 800;
            settings = new SettingsDialog();
            settings.Show(owner);
            var tabs = settings.FindControl<TabControl>("SettingsTabs")!;
            tabs.SelectedItem = tabs.Items.OfType<LazyTabItem>().Single(p => p.PageKey == "appearance");
            var language = App.Services.GetRequiredService<ILocalizationService>();
            foreach (var id in new[] { "zh-CN", "zh-Hant", "en", "fr" })
            {
                language.SetLanguage(id);
                settings.RequestedThemeVariant = id is "zh-Hant" or "en" ? ThemeVariant.Dark : ThemeVariant.Light;
                await CaptureAsync(settings, "settings-" + id);
                if (WindowBackgroundTheme.GetService(settings) != null) throw new InvalidOperationException("Dialog inherited the main-window theme.");
            }
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
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { success = error == null, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Background theme smoke: {output}; success={error == null}");
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime) lifetime.Shutdown(error == null ? 0 : 1);
        }

        async Task CaptureAsync(Control control, string name)
        {
            control.UpdateLayout();
            await Task.Delay(180);
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
            }
            finally { backdrop.Frosted = false; }

            void AssertOriginalAt(Point location, string label)
                => AssertMatches(background, location, label);

            void AssertMatches(Avalonia.Media.Imaging.Bitmap reference, Point location, string label)
            {
                var actual = Sample(window, location.X / window.Size.Width, location.Y / window.Size.Height);
                var expected = Sample(reference, location.X / reference.Size.Width, location.Y / reference.Size.Height);
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
