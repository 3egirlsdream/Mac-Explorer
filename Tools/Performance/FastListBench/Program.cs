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
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using SkiaSharp;

internal static class Program
{
    public static string OutputDirectory { get; private set; } = "";
    public static bool TreeOnly { get; private set; }
    public static bool FolderCoverOnly { get; private set; }
    [STAThread]
    public static void Main(string[] args)
    {
        OutputDirectory = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "results"));
        TreeOnly = args.Contains("--tree-only", StringComparer.Ordinal);
        FolderCoverOnly = args.Contains("--folder-cover-only", StringComparer.Ordinal);
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
                    await RunAsync(list);
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
        if (Program.FolderCoverOnly) { await RunFolderCoverComparisonAsync(list); return; }
        if (Program.TreeOnly) { await RunTreeComparisonAsync(list); return; }
        foreach (var grid in new[] { false, true })
        foreach (var grouped in new[] { false, true })
        {
            var mode = (grid ? "grid" : "details") + (grouped ? "-grouped" : "");
            await RunModeAsync(list, grid, grouped, mode);
        }
        await RunTreeComparisonAsync(list);
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

    private static async Task RunFolderCoverComparisonAsync(FastFileList list)
    {
        list.IsGrid = true;
        var rows = Enumerable.Range(0, 10_000).Select(i => new FileSystemEntry
        {
            FullPath = $"/synthetic-folders/{i:D5}", Name = $"相册 {i:D5}",
            IsDirectory = true, IconKey = "folder"
        }).ToArray();
        using var sample = new SKBitmap(112, 112);
        sample.Erase(SKColors.Orange);
        using var encoded = sample.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = encoded.ToArray();
        using var gate = new SemaphoreSlim(2);
        var started = 0;
        var cancelled = 0;
        var active = 0;
        var maxActive = 0;
        list.FolderCoverProvider = async (entry, _, token) =>
        {
            await gate.WaitAsync(token);
            var current = Interlocked.Increment(ref active);
            Interlocked.Increment(ref started);
            InterlockedExtensions.Max(ref maxActive, current);
            try
            {
                await Task.Delay(8, token);
                return new ThumbnailResult(bytes, entry.FullPath);
            }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
            finally { Interlocked.Decrement(ref active); gate.Release(); }
        };
        list.SetRows(rows);
        await Task.Delay(150);

        await SampleFolderScrollAsync(list, rows, false); // Warm JIT, type icons and text caches before comparison.
        var disabled = await SampleFolderScrollAsync(list, rows, false);
        var disabledRequests = started;
        var enabled = await SampleFolderScrollAsync(list, rows, true);
        var enabledRequests = started - disabledRequests;
        var disabledAgain = await SampleFolderScrollAsync(list, rows, false);

        var viewport = rows.Take(48).ToArray();
        var scheduler = new FastFileListImages
        {
            FolderCoverProvider = (_, _, _) => Task.FromResult<ThumbnailResult?>(null)
        };
        scheduler.UpdateVisible(viewport, 112, 128, false);
        await Task.Delay(100);
        var schedulerOff = SampleViewportUpdate(scheduler, viewport, false);
        var schedulerOn = SampleViewportUpdate(scheduler, viewport, true);
        scheduler.Clear();

        var fixture = Path.Combine(Program.OutputDirectory, "folder-cover-fixture");
        Directory.CreateDirectory(fixture);
        for (var i = 0; i < 997; i++) File.WriteAllBytes(Path.Combine(fixture, $"file-{i:D4}.txt"), []);
        for (var i = 0; i < 3; i++) File.WriteAllBytes(Path.Combine(fixture, $"photo-{i:D4}.png"), bytes);
        var thumbnails = new BenchThumbnails(bytes);
        var covers = new FolderPhotoCoverService(thumbnails);
        var scanMs = new double[30];
        var composeMs = new double[30];
        for (var i = 0; i < scanMs.Length; i++)
        {
            var start = Stopwatch.GetTimestamp();
            FolderPhotoCoverService.SelectPhotoPaths(fixture, thumbnails.IsImageFile, CancellationToken.None);
            scanMs[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            start = Stopwatch.GetTimestamp();
            await covers.CreateAsync(fixture, 112, CancellationToken.None);
            composeMs[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Directory.Delete(fixture, recursive: true);
        Array.Sort(scanMs);
        Array.Sort(composeMs);

        var report = new
        {
            rows = rows.Length, disabled, enabled, disabledAgain,
            schedulerOffP95Microseconds = schedulerOff,
            schedulerOnP95Microseconds = schedulerOn,
            disabledRequests, enabledRequests, cancelled, maxActive,
            directFilesPerFolder = 1000,
            scanP50Ms = Percentile(scanMs, .5), scanP95Ms = Percentile(scanMs, .95),
            scanAndComposeP50Ms = Percentile(composeMs, .5), scanAndComposeP95Ms = Percentile(composeMs, .95),
            scope = "Native Avalonia/Skia 10k synthetic folders; timed grid scrolling and UI rendering with a bounded 8ms thumbnail stub. Viewport scheduling times 48 visible folders. Scan/compose uses 1000 local files and cached bitmap bytes. Excludes native Quick Look generation and GPU presentation FPS."
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(Program.OutputDirectory, "folder-cover-comparison.json"), json);
        Console.WriteLine(json);
        if (disabledRequests != 0 || enabledRequests == 0 || maxActive > 2)
            throw new InvalidOperationException("Folder cover request scheduling did not match the expected bounds.");
    }

    private static double SampleViewportUpdate(FastFileListImages scheduler, FileSystemEntry[] viewport, bool enabled)
    {
        var samples = new double[200];
        for (var i = 0; i < samples.Length; i++)
        {
            var start = Stopwatch.GetTimestamp();
            scheduler.UpdateVisible(viewport, 112, 128, enabled);
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        }
        Array.Sort(samples);
        return Percentile(samples, .95);
    }

    private static async Task<object> SampleFolderScrollAsync(FastFileList list, FileSystemEntry[] rows, bool enabled)
    {
        list.FolderCoversEnabled = enabled;
        list.ScrollToOffset(0);
        await Task.Delay(200);
        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)list.Bounds.Width, (int)list.Bounds.Height), new Vector(96, 96));
        var rapid = new double[120];
        for (var frame = 0; frame < rapid.Length; frame++)
        {
            var start = Stopwatch.GetTimestamp();
            list.ScrollToEntry(rows[(frame * 137) % rows.Length]);
            bitmap.Render(list);
            rapid[frame] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            await Task.Delay(16);
        }
        var dwell = new double[24];
        for (var frame = 0; frame < dwell.Length; frame++)
        {
            list.ScrollToEntry(rows[(frame * 233) % rows.Length]);
            await Task.Delay(250);
            var start = Stopwatch.GetTimestamp();
            bitmap.Render(list);
            dwell[frame] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        list.ScrollToEntry(rows[5_000]);
        var continuousStart = list.Offset.Y;
        var continuous = new double[180];
        for (var frame = 0; frame < continuous.Length; frame++)
        {
            var start = Stopwatch.GetTimestamp();
            list.ScrollToOffset(continuousStart + frame * 3.5);
            bitmap.Render(list);
            continuous[frame] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            await Task.Delay(16);
        }
        Array.Sort(rapid);
        Array.Sort(dwell);
        Array.Sort(continuous);
        return new
        {
            enabled,
            rapidP50Ms = Percentile(rapid, .5), rapidP95Ms = Percentile(rapid, .95),
            dwellP50Ms = Percentile(dwell, .5), dwellP95Ms = Percentile(dwell, .95),
            continuousP50Ms = Percentile(continuous, .5), continuousP95Ms = Percentile(continuous, .95)
        };
    }

    private sealed class BenchThumbnails(byte[] bytes) : IThumbnailService
    {
        public Task<ThumbnailResult?> GetThumbnailResultAsync(string filePath, int maxPixelSize, CancellationToken ct = default)
            => Task.FromResult<ThumbnailResult?>(new ThumbnailResult(bytes, filePath));
        public Task<byte[]?> GetThumbnailAsync(string filePath, int maxPixelSize, CancellationToken ct = default)
            => Task.FromResult<byte[]?>(bytes);
        public Task<byte[]?> GetFaceCropAsync(string filePath, float bx, float by, float bw, float bh,
            int maxPixelSize = 128, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public bool IsImageFile(string extension) => extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
        public void EvictFromCache(string filePath) { }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            int current;
            do
            {
                current = Volatile.Read(ref target);
                if (current >= value) return;
            } while (Interlocked.CompareExchange(ref target, value, current) != current);
        }
    }

    private static async Task RunTreeComparisonAsync(FastFileList list)
    {
        list.IsGrid = false;
        var root = Enumerable.Range(0, 100_000).Select(i => new FileSystemEntry
        {
            FullPath = $"/tree/{i:D6}", Name = $"文件清单性能测试-{i:D6}",
            IsDirectory = i == 0, IconKey = i == 0 ? "folder" : "file-generic",
            Extension = i == 0 ? "" : ".txt", Size = i * 100
        }).ToArray();
        var tree = root.Select(entry => new FileTreeRow(entry, 0, entry.IsFolder, false, false, false)).ToArray();
        var children = Enumerable.Range(0, 10_000).Select(i => new FileSystemEntry
        {
            FullPath = $"/tree/000000/{i:D5}.txt", Name = $"子目录文件-{i:D5}.txt",
            Extension = ".txt", Size = i * 100
        }).ToArray();
        var expanded = new FileTreeRow[root.Length + children.Length];
        expanded[0] = new FileTreeRow(root[0], 0, true, true, false, false);
        for (var i = 0; i < children.Length; i++)
            expanded[i + 1] = new FileTreeRow(children[i], 1, false, false, false, false);
        for (var i = 1; i < root.Length; i++) expanded[i + children.Length] = tree[i];

        var plainSamples = new double[3];
        var treeSamples = new double[3];
        for (var pass = 0; pass < plainSamples.Length; pass++)
        {
            list.SetRows(root);
            await Task.Delay(100);
            plainSamples[pass] = SampleRenderP95(root.Length);
            list.SetTreeRows(tree, []);
            await Task.Delay(100);
            treeSamples[pass] = SampleRenderP95(root.Length);
        }
        var plainP95 = plainSamples.Order().ElementAt(1);
        var treeP95 = treeSamples.Order().ElementAt(1);
        var started = Stopwatch.GetTimestamp();
        list.SetTreeRows(expanded, []);
        var expandMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        await Task.Delay(300);
        var expandedP95 = SampleRenderP95(expanded.Length);
        started = Stopwatch.GetTimestamp();
        list.SetTreeRows(tree, []);
        var collapseMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var report = new
        {
            rootRows = root.Length, childRows = children.Length,
            plainP95Ms = plainP95, unopenedTreeP95Ms = treeP95,
            plainPassesP95Ms = plainSamples, treePassesP95Ms = treeSamples,
            unopenedOverheadPercent = plainP95 == 0 ? 0 : (treeP95 / plainP95 - 1) * 100,
            withinTwentyPercent = treeP95 <= plainP95 * 1.2,
            expandMs, collapseMs, expandedP95Ms = expandedP95,
            scope = "Native Avalonia/Skia synthetic rows; includes UI row replacement and visible-range rendering, excludes directory enumeration and GPU presentation."
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(Program.OutputDirectory, "tree-comparison.json"), json);
        Console.WriteLine(json);

        double SampleRenderP95(int count)
        {
            var size = new PixelSize(Math.Max(1, (int)list.Bounds.Width), Math.Max(1, (int)list.Bounds.Height));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
            var durations = new double[180];
            for (var frame = 0; frame < durations.Length; frame++)
            {
                list.ScrollToOffset((frame * 7919 % count) * FastFileList.RowHeight);
                var start = Stopwatch.GetTimestamp();
                bitmap.Render(list);
                durations[frame] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            Array.Sort(durations);
            return Percentile(durations, .95);
        }
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
