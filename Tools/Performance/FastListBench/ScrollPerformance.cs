using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Indexing;
using MacExplorer.Performance;
using MacExplorer.Services;
using MacExplorer.Services.Search;
using MacExplorer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using LiquidGlassAvaloniaUI;

internal static class ScrollPerformance
{
    public static async Task RunAsync(string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        try
        {
            var root = Environment.GetEnvironmentVariable("MACEXPLORER_TEST_ROOT")!;
            var data = Path.Combine(root, "scroll-data");
            // Isolate scrolling from the initial OCR of 3,333 newly created images.
            MacExplorer.App.Services.GetRequiredService<ISettingsService>().Set("ai_analysis_enabled", false);
            var fontOverride = Environment.GetEnvironmentVariable("FASTLIST_BENCH_FONT_FAMILY");
            if (!string.IsNullOrWhiteSpace(fontOverride))
                Application.Current.Resources["FontFamilyUi"] = new FontFamily(fontOverride);
            MacExplorer.App.Services.GetRequiredService<IndexConfiguration>().ExcludedPaths.Add(Path.Combine(root, ".macexplorer"));
            await Task.Run(() =>
            {
                Directory.CreateDirectory(data);
                using var bitmap = new SKBitmap(240, 160);
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(SKColors.CornflowerBlue);
                using var paint = new SKPaint { Color = SKColors.Orange };
                canvas.DrawCircle(120, 80, 60, paint);
                using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                var bytes = png.ToArray();
                for (var i = 0; i < 10_000; i++)
                {
                    var name = $"{i:D6}-年度项目-性能测试-éà-😀-文件名称需要自动换行";
                    var path = Path.Combine(data, name);
                    if (i % 3 == 0) Directory.CreateDirectory(path);
                    else if (i % 3 == 1) File.WriteAllBytes(path + ".png", bytes);
                    else File.WriteAllText(path + ".txt", "Scroll performance check\n" + name);
                }
            });
            var window = desktop.MainWindow!;
            window.Width = 1100; window.Height = 760;
            var vm = ((MainWindowViewModel)window.DataContext!).FileList;
            var list = window.GetVisualDescendants().OfType<FastFileList>().Single();
            var indexer = MacExplorer.App.Services.GetRequiredService<SearchIndexer>();
            indexer.EnsureRoot(root);
            while (true)
            {
                var observation = indexer.Observe(root);
                if (observation.Status.Phase == SearchIndexPhase.Ready) break;
                await observation.Changed.WaitAsync(TimeSpan.FromMinutes(2));
            }
            // The primary family must resolve directly, avoiding an uncached alias lookup
            // for every shaped run. Keep the same PingFang/Apple emoji glyphs as before.
            var typeface = new Typeface(list.FontFamily, weight: list.FontWeight);
            if (string.IsNullOrWhiteSpace(fontOverride) && typeface.GlyphTypeface.FamilyName != list.FontFamily.Name)
                throw new InvalidOperationException($"Primary font alias did not resolve directly: {list.FontFamily.Name} -> {typeface.GlyphTypeface.FamilyName}");
            var provider = list.ThumbnailProvider;
            var records = new List<object>();
            var renders = new List<double>();
            var ticks = new List<double>();
            var frames = new List<double>();
            var collecting = false;
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, meter) =>
            {
                if (instrument.Meter.Name == FileListPerformanceMetrics.MeterName) meter.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
            {
                if (collecting && instrument.Name == "filelist.fast.render.duration_ms") renders.Add(value);
            });
            listener.Start();
            var previousTick = Stopwatch.GetTimestamp();
            var previousFrame = previousTick;
            var measuringFrames = true;
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(8), DispatcherPriority.Normal, (_, _) =>
            {
                var now = Stopwatch.GetTimestamp();
                if (collecting) ticks.Add(Stopwatch.GetElapsedTime(previousTick, now).TotalMilliseconds);
                previousTick = now;
            });
            timer.Start();
            window.RequestAnimationFrame(Frame);
            void Frame(TimeSpan _)
            {
                var now = Stopwatch.GetTimestamp();
                if (collecting) frames.Add(Stopwatch.GetElapsedTime(previousFrame, now).TotalMilliseconds);
                previousFrame = now;
                if (measuringFrames) window.RequestAnimationFrame(Frame);
            }
            foreach (var mode in new[] { ViewMode.List, ViewMode.Grid, ViewMode.Tree })
            foreach (var thumbnails in new[] { false, true })
            foreach (var pixels in new[] { 3.5, 120, 600 })
            {
                await vm.NavigateToAsync(root);
                vm.SetViewMode(mode);
                list.ThumbnailProvider = thumbnails ? provider : null;
                await vm.NavigateToAsync(data);
                await Task.Delay(300);
                for (var pass = 0; pass < 2; pass++)
                {
                    list.ScrollToOffset(0);
                    await Task.Delay(100);
                    renders.Clear(); ticks.Clear(); frames.Clear();
                    previousTick = previousFrame = Stopwatch.GetTimestamp();
                    var glassBefore = LiquidGlassDiagnostics.Snapshot;
                    collecting = true;
                    for (var step = 1; step <= 120; step++)
                    {
                        list.ScrollToOffset(step * pixels);
                        await Task.Delay(8);
                    }
                    collecting = false;
                    var glassAfter = LiquidGlassDiagnostics.Snapshot;
                    var record = new
                    {
                        mode = mode.ToString(), thumbnails, pixels, pass, samples = renders.Count,
                        fontFamily = list.FontFamily.FamilyNames.ToString(), resolvedFont = typeface.GlyphTypeface.FamilyName,
                        renderP50 = Percentile(renders, .5), renderP95 = Percentile(renders, .95),
                        renderMax = renders.DefaultIfEmpty().Max(), uiP95 = Percentile(ticks, .95), uiMax = ticks.DefaultIfEmpty().Max(),
                        frameP95 = Percentile(frames, .95), frameMax = frames.DefaultIfEmpty().Max(),
                        framesOver25ms = frames.Count(x => x > 25), framesOver50ms = frames.Count(x => x > 50),
                        entries = vm.Entries.Count, offset = list.Offset.Y, visible = list.LastRenderedRowCount,
                        cachedTexts = list.CachedTextCount,
                        glassCaptures = glassAfter.CapturesStarted - glassBefore.CapturesStarted,
                        glassPublishes = glassAfter.CapturesPublished - glassBefore.CapturesPublished,
                        glassCopyBytes = glassAfter.FullBitmapCopyBytes - glassBefore.FullBitmapCopyBytes,
                        glassFilterMisses = glassAfter.FilterCacheMisses - glassBefore.FilterCacheMisses
                    };
                    if (vm.Entries.Count != 10_000 || list.Offset.Y <= 0) throw new InvalidOperationException("Scroll/data check failed");
                    records.Add(record);
                    Console.WriteLine(JsonSerializer.Serialize(record));
                    await File.WriteAllTextAsync(Path.Combine(output, "scroll-results.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            timer.Stop(); measuringFrames = false;
            if (Environment.GetEnvironmentVariable("FASTLIST_BENCH_KEEP_OPEN") != "1") desktop.Shutdown();
            else
            {
                window.Width = 1000;
                window.Height = 680;
            }
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "scroll-error.txt"), ex.ToString());
            Console.Error.WriteLine(ex); desktop.Shutdown(1);
        }
    }
    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];
    }
}
