#if DEBUG
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using SkiaSharp;

namespace MacExplorer.Tools.Testing;

internal static class GalleryPreviewSmoke
{
    internal static async Task RunAsync(MainWindow owner)
    {
        var root = RuntimePaths.TestRoot ?? throw new InvalidOperationException("An isolated test profile is required.");
        var output = Environment.GetEnvironmentVariable("MACEXPLORER_GALLERY_SMOKE_OUT")
            ?? Path.Combine(root, "gallery-preview");
        var fixtureDir = Path.Combine(root, "gallery-fixture");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(fixtureDir);

        string? error = null;
        var shots = new List<string>();
        try
        {
            CreateFixtures(fixtureDir);
            owner.Width = 1180;
            owner.Height = 760;
            owner.RequestedThemeVariant = ThemeVariant.Light;
            owner.ShowAndActivate();

            var vm = owner.DataContext as MainWindowViewModel
                ?? throw new InvalidOperationException("MainWindowViewModel not found.");
            var fileList = vm.FileList;
            await fileList.NavigateToAsync(fixtureDir);
            await WaitAsync(() => !fileList.IsDirectoryLoading && fileList.Entries.Count >= 8);

            // Apply two Finder tags to the hero icon so tag dots appear in the floating bar.
            var heroEntry = fileList.Entries.First(e => e.Name == "appicon.png");
            if (fileList.FileTagService is { } tagService)
            {
                await tagService.SetTagAsync([heroEntry.FullPath], FileTagCatalog.FinderColors[0], true);
                await tagService.SetTagAsync([heroEntry.FullPath], FileTagCatalog.FinderColors[4], true);
            }

            fileList.SetViewMode(ViewMode.Grid);
            fileList.SelectEntry(heroEntry);
            fileList.SetThumbnailZoomLevel(FileThumbnailSizing.GalleryLevel);
            await Task.Delay(500);

            var fileListView = owner.GetVisualDescendants().OfType<FileListView>().First(v => v.IsGalleryVisible);
            await fileListView.GalleryPreviewReady;
            await Task.Delay(350);

            // 1. Light mode — transparent icon with checkerboard + floating bar + filmstrip
            await CaptureAsync("gallery-light-icon");

            // 1b. Light mode with BOTH expanded Sidebar AND Inline InfoPanel open (exact 3-column layout!)
            owner.Width = 1360;
            fileList.IsInfoPanelVisible = true;
            await Task.Delay(450);
            await CaptureAsync("gallery-light-with-inspector");
            fileList.IsInfoPanelVisible = false;
            owner.Width = 1180;
            await Task.Delay(250);

            // 2. Dark mode — transparent icon
            owner.RequestedThemeVariant = ThemeVariant.Dark;
            await Task.Delay(350);
            await CaptureAsync("gallery-dark-icon");

            // 3. Light mode — full bleed photo (auto checkerboard off)
            owner.RequestedThemeVariant = ThemeVariant.Light;
            var photoEntry = fileList.Entries.First(e => e.Name == "sunset-landscape.jpg");
            fileList.SelectEntry(photoEntry);
            await Task.Delay(450);
            await fileListView.GalleryPreviewReady;
            await Task.Delay(300);
            await CaptureAsync("gallery-light-photo");

            // 4. Light mode — Folder Collage Card
            var folderEntry = fileList.Entries.First(e => e.Name == "UI Design Kit");
            fileList.SelectEntry(folderEntry);
            await WaitAsync(() => fileListView.GalleryView?.GalleryFolderCollageGrid.Children.Count >= 4);
            await Task.Delay(450);
            await CaptureAsync("gallery-light-folder");

            // 5. Light mode — Unified Spacebar SuperPreview Overlay on transparent icon
            fileList.SelectEntry(heroEntry);
            await Task.Delay(350);
            await fileListView.GalleryPreviewReady;
            await owner.OpenSuperPreviewForEntryAsync(heroEntry);
            await Task.Delay(450);
            await CaptureAsync("superpreview-light-icon");
            owner.FindControl<SuperPreviewView>("SuperPreviewControl")?.Close();
            await Task.Delay(200);

            // 6. Light mode — Expanded filmstrip + 90° rotated artwork
            fileListView.GalleryPreview?.RotateGalleryImage(90);
            if (fileListView.GalleryView is { } gv)
            {
                typeof(FileListView)
                    .GetMethod("ApplyGalleryStripHeight", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(fileListView, [148.0, false]);
            }
            await Task.Delay(350);
            await CaptureAsync("gallery-light-resized");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"),
                JsonSerializer.Serialize(new { success = error == null, shots, error }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Gallery preview smoke: {output}; success={error == null}");
            if (error != null) Console.Error.WriteLine(error);
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                lifetime.Shutdown(error == null ? 0 : 1);
        }

        async Task CaptureAsync(string name)
        {
            owner.UpdateLayout();
            await Task.Delay(250);
            var target = Path.Combine(output, name + ".png");
            var size = owner.Bounds.Size;
            var pixelSize = new PixelSize(Math.Max(1, (int)Math.Round(size.Width)), Math.Max(1, (int)Math.Round(size.Height)));
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(pixelSize, new Vector(96, 96));
            bitmap.Render(owner);
            bitmap.Save(target);
            shots.Add(target);
        }
    }

    private static void CreateFixtures(string dir)
    {
        // Transparent macOS-style app icon (matches user's screenshot subject!)
        WriteRoundedIconPng(Path.Combine(dir, "appicon.png"), 512, 512);
        WritePhotoJpg(Path.Combine(dir, "sunset-landscape.jpg"), 1280, 820, new SKColor(0xFF, 0x7A, 0x59), new SKColor(0x3B, 0x2F, 0x8F));
        WritePhotoJpg(Path.Combine(dir, "ocean-breeze.jpg"), 1080, 720, new SKColor(0x1E, 0xA8, 0xD6), new SKColor(0x0B, 0x4F, 0x71));
        WritePhotoJpg(Path.Combine(dir, "forest-mist.jpg"), 960, 720, new SKColor(0x3E, 0xA6, 0x6B), new SKColor(0x1B, 0x43, 0x32));
        WriteRoundedIconPng(Path.Combine(dir, "badge-vector.png"), 384, 384);
        File.WriteAllText(Path.Combine(dir, "screen-recording-2026.mov"), "stub");
        File.WriteAllText(Path.Combine(dir, "release-notes-v2.md"), "# Gallery Preview\n\nClean stage, glass controls, and refined filmstrip.\n");
        File.WriteAllText(Path.Combine(dir, "design-spec-2026.pdf"), "stub");
        File.WriteAllText(Path.Combine(dir, "assets-backup.zip"), "stub");
        File.WriteAllText(Path.Combine(dir, "ambient-track.mp3"), "stub");
        File.WriteAllText(Path.Combine(dir, "extra-long-filename-for-edge-fade-check-01.png"), "stub");
        File.WriteAllText(Path.Combine(dir, "extra-long-filename-for-edge-fade-check-02.jpg"), "stub");

        var sub = Path.Combine(dir, "UI Design Kit");
        Directory.CreateDirectory(sub);
        WritePhotoJpg(Path.Combine(sub, "01-hero.jpg"), 640, 480, new SKColor(0x63, 0x66, 0xF1), new SKColor(0xEC, 0x48, 0x99));
        WritePhotoJpg(Path.Combine(sub, "02-cards.jpg"), 640, 480, new SKColor(0x10, 0xB9, 0x81), new SKColor(0x05, 0x96, 0x69));
        WritePhotoJpg(Path.Combine(sub, "03-palette.jpg"), 640, 480, new SKColor(0xF5, 0x9E, 0x0B), new SKColor(0xEF, 0x44, 0x44));
        WriteRoundedIconPng(Path.Combine(sub, "04-mark.png"), 320, 320);
        File.WriteAllText(Path.Combine(sub, "tokens.json"), "{ \"radius\": 10 }");
        File.WriteAllText(Path.Combine(sub, "README.md"), "# UI Design Kit");
    }

    private static void WriteRoundedIconPng(string path, int w, int h)
    {
        using var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        var pad = w * 0.08f;
        var rect = new SKRect(pad, pad, w - pad, h - pad);
        using var grad = SKShader.CreateLinearGradient(
            new SKPoint(rect.Left, rect.Bottom),
            new SKPoint(rect.Right, rect.Top),
            [new SKColor(0x8B, 0x5C, 0xF6), new SKColor(0xEC, 0x48, 0x99), new SKColor(0xFB, 0x71, 0x85)],
            SKShaderTileMode.Clamp);
        using var bgPaint = new SKPaint { Shader = grad, IsAntialias = true };
        canvas.DrawRoundRect(new SKRoundRect(rect, w * 0.22f, h * 0.22f), bgPaint);

        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        var folderRect = new SKRect(w * 0.26f, h * 0.36f, w * 0.74f, h * 0.68f);
        canvas.DrawRoundRect(new SKRoundRect(folderRect, w * 0.05f, h * 0.05f), white);
        var tabRect = new SKRect(w * 0.26f, h * 0.31f, w * 0.46f, h * 0.41f);
        canvas.DrawRoundRect(new SKRoundRect(tabRect, w * 0.03f, h * 0.03f), white);

        using var ring = new SKPaint { Color = new SKColor(0xEC, 0x48, 0x99), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = w * 0.045f };
        canvas.DrawCircle(w * 0.64f, h * 0.63f, w * 0.11f, white);
        canvas.DrawCircle(w * 0.64f, h * 0.63f, w * 0.085f, ring);

        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static void WritePhotoJpg(string path, int w, int h, SKColor top, SKColor bottom)
    {
        using var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bmp);
        using var grad = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, h), [top, bottom], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = grad, IsAntialias = true };
        canvas.DrawRect(0, 0, w, h, paint);
        using var glow = new SKPaint { Color = new SKColor(255, 255, 255, 58), IsAntialias = true };
        canvas.DrawCircle(w * 0.72f, h * 0.32f, h * 0.22f, glow);
        using var hill = new SKPaint { Color = new SKColor(15, 23, 42, 95), IsAntialias = true };
        using var pathObj = new SKPath();
        pathObj.MoveTo(0, h);
        pathObj.LineTo(0, h * 0.68f);
        pathObj.QuadTo(w * 0.30f, h * 0.45f, w * 0.56f, h * 0.66f);
        pathObj.QuadTo(w * 0.80f, h * 0.52f, w, h * 0.64f);
        pathObj.LineTo(w, h);
        pathObj.Close();
        canvas.DrawPath(pathObj, hill);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 92);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static async Task WaitAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Timed out waiting for gallery smoke condition.");
    }

    private static async Task RunProcessAsync(string file, string[] args)
    {
        using var proc = Process.Start(new ProcessStartInfo(file)
        {
            RedirectStandardError = true,
            UseShellExecute = false
        }.ApplyArgs(args))!;
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{file} failed: {await proc.StandardError.ReadToEndAsync()}");
    }

    private static ProcessStartInfo ApplyArgs(this ProcessStartInfo psi, string[] args)
    {
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return psi;
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr Selector(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern int SendInt(IntPtr receiver, IntPtr selector);
}
#endif
