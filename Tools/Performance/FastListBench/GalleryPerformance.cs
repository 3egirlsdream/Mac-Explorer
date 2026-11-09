using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Performance;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

internal static class GalleryPerformance
{
    private const int FileCount = 512;

    public static async Task RunAsync(string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        try
        {
            var root = Environment.GetEnvironmentVariable("MACEXPLORER_TEST_ROOT")!;
            MacExplorer.App.Services.GetRequiredService<ISettingsService>().Set("ai_analysis_enabled", false);
            var window = desktop.MainWindow!;
            window.Width = 1100;
            window.Height = 760;
            var view = window.GetVisualDescendants().OfType<FileListView>().First();
            var vm = (FileListViewModel)view.DataContext!;
            var list = view.FindControl<FastFileList>("FastList")!;
            var records = new List<object>();
            vm.IsInfoPanelVisible = false;
            vm.SetViewMode(ViewMode.Grid);
            foreach (var (mode, level) in new[] { ("default", 1), ("maximum", 3), ("gallery", 4) })
            {
                var data = Path.Combine(root, "zoom-" + mode);
                await Task.Run(() => CreateFixture(data));
                foreach (var warm in new[] { false, true })
                {
                    vm.SetThumbnailZoomLevel(level == 4 ? 3 : level);
                    await vm.NavigateToAsync(root);
                    var initialFootprint = MemoryDiagnostics.ReadPhysicalFootprint();
                    long? peakFootprint = initialFootprint;
                    var started = Stopwatch.GetTimestamp();
                    await vm.NavigateToAsync(data);
                    await WaitUntilAsync(() => list.Rows.Count == FileCount && !vm.IsDirectoryLoading);
                    list.ScrollToOffset(0);
                    var first = list.Rows[0];
                    ListBox? strip = null;
                    if (level == 4)
                    {
                        vm.SelectEntry(first);
                        vm.SetThumbnailZoomLevel(level);
                        strip = view.GalleryFilmstrip!;
                        await WaitUntilAsync(() => ReferenceEquals(strip.SelectedItem, first));
                        if (!await view.GalleryPreviewReady.WaitAsync(TimeSpan.FromSeconds(10)))
                            throw new InvalidOperationException("Gallery did not produce a full preview.");
                    }
                    else
                    {
                        await WaitUntilAsync(() => list.HasThumbnail(first));
                    }
                    if (MemoryDiagnostics.ReadPhysicalFootprint() is { } readyFootprint)
                        peakFootprint = Math.Max(peakFootprint ?? 0, readyFootprint);
                    var firstReadyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    var frames = new List<double>();
                    var renders = new List<double>();
                    using var listener = new MeterListener();
                    listener.InstrumentPublished = (instrument, owner) =>
                    {
                        if (instrument.Meter.Name == FileListPerformanceMetrics.MeterName) owner.EnableMeasurementEvents(instrument);
                    };
                    listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
                    {
                        if (instrument.Name == "filelist.fast.render.duration_ms") renders.Add(value);
                    });
                    listener.Start();
                    var frameTime = Stopwatch.GetTimestamp();
                    var measuring = true;
                    void OnFrame(TimeSpan _)
                    {
                        if (!measuring) return;
                        frames.Add(Stopwatch.GetElapsedTime(frameTime).TotalMilliseconds);
                        frameTime = Stopwatch.GetTimestamp();
                        window.RequestAnimationFrame(OnFrame);
                    }
                    window.RequestAnimationFrame(OnFrame);
                    var horizontal = strip?.FindDescendantOfType<ScrollViewer>();
                    for (var i = 1; i <= 60; i++)
                    {
                        if (horizontal != null) horizontal.Offset = new Vector(i * 64, 0);
                        else list.ScrollToOffset(i * 64);
                        await Task.Delay(16);
                        if (i % 4 == 0 && MemoryDiagnostics.ReadPhysicalFootprint() is { } footprint)
                            peakFootprint = Math.Max(peakFootprint ?? 0, footprint);
                    }
                    measuring = false;
                    if (level == 4)
                    {
                        if (horizontal == null || horizontal.Offset.X <= 0)
                            throw new InvalidOperationException("The filmstrip did not scroll.");
                        strip!.SelectedIndex = 2;
                        var superseded = view.GalleryPreviewReady;
                        strip.SelectedIndex = 3;
                        if (await superseded.WaitAsync(TimeSpan.FromSeconds(10))
                            || !await view.GalleryPreviewReady.WaitAsync(TimeSpan.FromSeconds(10)))
                            throw new InvalidOperationException("A superseded preview completed or the latest preview failed.");
                        AssertBluePreview(view);
                    }
                    var result = new
                    {
                        mode, warm, firstReadyMs, frameP95Ms = P95(frames), renderP95Ms = P95(renders),
                        initialFootprint, peakFootprint, frameSamples = frames.Count, renderSamples = renders.Count
                    };
                    records.Add(result);
                    Console.WriteLine(JsonSerializer.Serialize(result));
                }
            }
            await File.WriteAllTextAsync(Path.Combine(output, "gallery-results.json"),
                JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            await VerifyDocumentAsync(root, vm, view);
            desktop.Shutdown();
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "gallery-error.txt"), ex.ToString());
            Console.Error.WriteLine(ex);
            desktop.Shutdown(1);
        }
    }

    private static async Task VerifyDocumentAsync(string root, FileListViewModel vm, FileListView view)
    {
        var directory = Path.Combine(root, "gallery-document");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "preview.pdf");
        using (var pdf = SKDocument.CreatePdf(path))
        {
            var page = pdf.BeginPage(400, 300);
            page.Clear(SKColors.LightBlue);
            pdf.EndPage();
            pdf.Close();
        }
        await vm.NavigateToAsync(directory);
        await WaitUntilAsync(() => (view.GalleryFilmstrip?.SelectedItem as FileSystemEntry)?.FullPath == path);
        if (!await view.GalleryPreviewReady.WaitAsync(TimeSpan.FromSeconds(10))
            || view.FindDescendantOfType<DocumentPreviewHost>() == null)
            throw new InvalidOperationException("Gallery did not attach the native PDF viewer.");
        view.TryExitGallery();
        await WaitUntilAsync(() => view.FindDescendantOfType<DocumentPreviewHost>() == null);
    }

    private static void CreateFixture(string path)
    {
        Directory.CreateDirectory(path);
        using var image = new SKBitmap(1280, 800);
        image.Erase(SKColors.Red);
        using var red = image.Encode(SKEncodedImageFormat.Png, 95);
        image.Erase(SKColors.Blue);
        using var blue = image.Encode(SKEncodedImageFormat.Png, 95);
        var redBytes = red.ToArray();
        var blueBytes = blue.ToArray();
        for (var i = 0; i < FileCount; i++)
            File.WriteAllBytes(Path.Combine(path, $"{i:D4}.png"), i % 2 == 0 ? redBytes : blueBytes);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(15)) throw new TimeoutException();
            await Task.Delay(16);
        }
    }

    private static void AssertBluePreview(FileListView view)
    {
        var preview = view.GetVisualDescendants().OfType<SuperPreviewView>().Single();
        var bitmap = (Bitmap)preview.FindControl<Image>("PreviewImage")!.Source!;
        using var encoded = new MemoryStream();
        bitmap.Save(encoded);
        encoded.Position = 0;
        using var decoded = SKBitmap.Decode(encoded);
        if (decoded.GetPixel(0, 0).Blue < 240) throw new InvalidOperationException("The previous file overwrote the current preview.");
    }

    private static double? P95(List<double> values)
        => values.Count == 0 ? null : values.Order().ElementAt((int)Math.Ceiling(values.Count * 0.95) - 1);
}
