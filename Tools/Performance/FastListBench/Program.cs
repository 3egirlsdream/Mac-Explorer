using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Performance;
using SkiaSharp;

internal static class Program
{
    public static bool CompareStyles => Environment.GetEnvironmentVariable("FKFINDER_COMPARE_LIST_STYLES") == "1";
    public static string OutputDirectory { get; private set; } = "";
    [STAThread]
    public static void Main(string[] args)
    {
        OutputDirectory = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "results"));
        Directory.CreateDirectory(OutputDirectory);
        AppBuilder.Configure<BenchApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
    }
}

public sealed class BenchApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Resources.MergedDictionaries.Add((ResourceDictionary)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/TypographyTokens.axaml")));
        Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ThemeTokens.axaml")));
        if (Program.CompareStyles)
        {
            Resources["BoolNotConverter"] = new MacExplorer.Views.BoolNotConverter();
            Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/Styles.axaml")));
            Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var list = new FastFileList
            {
                Background = Brushes.White, Foreground = Brushes.Black, Secondary = Brushes.Gray,
                Selected = Brushes.LightBlue, SelectedHover = Brushes.LightBlue,
                FontFamily = new FontFamily("System Font, PingFang SC, sans-serif")
            };
            var window = new Window
            {
                Title = "FKFinder — 100,000 rows performance check", Width = 1000, Height = 720,
                Content = new ScrollViewer { Content = list }
            };
            desktop.MainWindow = window;
            window.Opened += async (_, _) =>
            {
                try
                {
                    if (Program.CompareStyles) await StyleComparison.RunAsync(window, Program.OutputDirectory);
                    else await RunAsync(list);
                    desktop.Shutdown();
                }
                catch (Exception ex)
                {
                    await File.WriteAllTextAsync(Path.Combine(Program.OutputDirectory, "error.txt"), ex.ToString());
                    Console.Error.WriteLine(ex);
                    desktop.Shutdown(1);
                }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task RunAsync(FastFileList list)
    {
        foreach (var grid in new[] { false, true })
        foreach (var grouped in new[] { false, true })
        {
            var mode = (grid ? "grid" : "details") + (grouped ? "-grouped" : "");
            await RunModeAsync(list, grid, grouped, mode);
        }
        var metricCosts = new List<object>();
        list.IsGrid = true;
        foreach (var count in new[] { 1000, 10_000, 100_000 })
        {
            list.SetRows([]);
            var unique = Enumerable.Range(0, count).Select(i => new FileSystemEntry
            {
                FullPath = $"/unique/{i:D6}.txt", Name = $"{i:D6}.txt", Extension = ".txt"
            }).ToArray();
            var started = Stopwatch.GetTimestamp();
            list.SetRows(unique);
            var firstMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            started = Stopwatch.GetTimestamp();
            list.SetRows(unique.ToArray());
            metricCosts.Add(new { count, firstMs, refreshMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
        }
        await File.WriteAllTextAsync(Path.Combine(Program.OutputDirectory, "unique-grid-layout.json"),
            JsonSerializer.Serialize(metricCosts, new JsonSerializerOptions { WriteIndented = true }));
        list.IsGrid = true;
        using var swatch = new SKBitmap(32, 32);
        swatch.Erase(SKColors.Blue);
        using var png = swatch.Encode(SKEncodedImageFormat.Png, 100);
        list.SetRows([new FileSystemEntry
        {
            FullPath = "__ai_face__/1", Name = "人物封面", IsVirtual = true, IsDirectory = true,
            IconKey = "ai-people", VirtualItemCount = 123,
            ThumbnailUrl = "data:image/png;base64," + Convert.ToBase64String(png.ToArray())
        }]);
        await Task.Delay(500);
        using var cover = new RenderTargetBitmap(new PixelSize((int)list.Bounds.Width, (int)list.Bounds.Height));
        cover.Render(list);
        cover.Save(Path.Combine(Program.OutputDirectory, "virtual-cover.png"));
        using var coverPixels = SKBitmap.Decode(Path.Combine(Program.OutputDirectory, "virtual-cover.png"));
        var color = coverPixels.GetPixel(68, 42);
        if (color.Blue < 240 || color.Red > 20 || color.Green > 20)
            throw new InvalidOperationException("The virtual folder cover did not replace the fallback icon.");
    }

    private static async Task RunModeAsync(FastFileList list, bool grid, bool grouped, string mode)
    {
        list.IsGrid = grid;
        var output = Path.Combine(Program.OutputDirectory, mode);
        Directory.CreateDirectory(output);
        list.IsLoading = true;
        await Task.Delay(200);
        using (var skeleton = new RenderTargetBitmap(new PixelSize((int)list.Bounds.Width, (int)list.Bounds.Height), new Vector(96, 96)))
        {
            skeleton.Render(list);
            skeleton.Save(Path.Combine(output, "skeleton.png"));
        }
        list.IsLoading = false;
        var rows = Enumerable.Range(0, 100_000).Select(i => new FileSystemEntry
        {
            FullPath = $"/synthetic/{i:D6}.txt", Name = $"文件清单性能测试-{i:D6}.txt",
            Extension = ".txt", Size = i * 100, LastModified = new DateTime(2026, 9, 8),
            IsSelected = i % 7 == 0
        }).ToArray();
        var groups = grouped ? Enumerable.Range(0, 100).Select(i => new FastFileListGroup($"分组 {i + 1:D3}", 1000)).ToArray() : [];
        var start = Stopwatch.GetTimestamp();
        list.SetRows(rows, groups);
        var setRowsMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        await Task.Delay(500);
        var durations = new List<double>();
        var rowCounts = new List<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == FileListPerformanceMetrics.MeterName) meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            if (instrument.Name == "filelist.fast.render.duration_ms") durations.Add(value);
        });
        listener.SetMeasurementEventCallback<int>((instrument, value, _, _) =>
        {
            if (instrument.Name == "filelist.fast.render.rows") rowCounts.Add(value);
        });
        listener.Start();
        // Drive native Avalonia/Skia rendering. These are Render costs, not GPU presentation FPS.
        var refreshCosts = new List<double>();
        for (var frame = 0; frame < 360; frame++)
        {
            var index = frame < 180 ? frame * 120 : (frame * 7919) % rows.Length;
            list.ScrollToEntry(rows[index]);
            if (frame % 60 == 0)
            {
                start = Stopwatch.GetTimestamp();
                list.SetRows(rows.ToArray(), groups);
                refreshCosts.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            await Task.Delay(16);
        }
        var sampleDurations = durations.ToArray();
        var sampleRows = rowCounts.ToArray();
        list.ScrollToOffset(0);
        await Task.Delay(100);
        durations.Clear();
        for (var frame = 1; frame <= 180; frame++)
        {
            list.ScrollToOffset(frame * 3.5);
            await Task.Delay(16);
        }
        var continuousDurations = durations.Order().ToArray();
        listener.Dispose();
        var checkedRows = 0;
        var blankRows = 0;
        // Separate image checks from timing; every complete visible name row must contain ink.
        for (var frame = 0; frame < 24; frame++)
        {
            list.ScrollToEntry(rows[frame == 23 ? 99_999 : frame * 4001]);
            await Task.Delay(40);
            var expectedIndex = frame == 23 ? 99_999 : frame * 4001;
            if (list.VisibleRange.First > expectedIndex || list.VisibleRange.End <= expectedIndex)
                throw new InvalidOperationException("Scroll position did not follow the requested row.");
            var size = new PixelSize((int)Math.Ceiling(list.Bounds.Width), (int)Math.Ceiling(list.Bounds.Height));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
            bitmap.Render(list);
            using var png = new MemoryStream();
            bitmap.Save(png);
            using var pixels = SKBitmap.Decode(png.ToArray());
            var (first, end) = list.VisibleRange;
            for (var index = first; index < end; index++)
            {
                var bounds = list.RowBounds(index);
                var y = bounds.Y;
                var nameBounds = list.NameBounds(index);
                if (y < 0 || bounds.Bottom > size.Height) continue;
                checkedRows++;
                var hasInk = false;
                for (var py = (int)nameBounds.Top; py < (int)nameBounds.Bottom && !hasInk; py++)
                for (var x = (int)nameBounds.Left; x < (int)nameBounds.Right; x++)
                {
                    var color = pixels.GetPixel(x, py);
                    if (color.Red < 90 && color.Green < 90 && color.Blue < 90) { hasInk = true; break; }
                }
                if (!hasInk) blankRows++;
            }
            if (frame is 0 or 12 or 23) bitmap.Save(Path.Combine(output, $"rows-{frame:D2}.png"));
        }
        Array.Sort(sampleDurations);
        var report = new
        {
            mode, rows = rows.Length, viewport = list.Viewport.ToString(), setRowsMs,
            renderSamples = sampleDurations.Length,
            renderP50Ms = Percentile(sampleDurations, .50), renderP95Ms = Percentile(sampleDurations, .95),
            renderMaxMs = sampleDurations.LastOrDefault(),
            continuousRenderSamples = continuousDurations.Length,
            continuousRenderP50Ms = Percentile(continuousDurations, .50),
            continuousRenderP95Ms = Percentile(continuousDurations, .95),
            continuousRenderMaxMs = continuousDurations.LastOrDefault(),
            minVisibleRows = sampleRows.DefaultIfEmpty().Min(), maxVisibleRows = sampleRows.DefaultIfEmpty().Max(),
            refreshCostsMs = refreshCosts, checkedRows, blankRows,
            scope = "Native Avalonia/Skia, synthetic 100k rows and type icons. Render CPU cost and sampled raster checks; excludes disk enumeration, Quick Look and presentation FPS."
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(output, "results.json"), json);
        Console.WriteLine(json);
        if (blankRows > 0 || sampleDurations.Length == 0) throw new InvalidOperationException("Native rendering verification failed.");
    }

    private static double Percentile(double[] samples, double fraction)
        => samples.Length == 0 ? 0 : samples[(int)((samples.Length - 1) * fraction)];
}
