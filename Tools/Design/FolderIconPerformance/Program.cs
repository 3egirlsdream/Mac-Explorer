using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using FolderIconDemo;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using SkiaSharp;

internal static class Program
{
    public static string Output = "";
    public static string Assets = "";
    public static bool Reverse;
    [STAThread]
    public static void Main(string[] args)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MACEXPLORER_TEST_ROOT")))
            throw new InvalidOperationException("Launch through Tools/Testing/run-isolated.sh.");
        Output = Path.GetFullPath(args[0]);
        Assets = Path.GetFullPath(args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "Tools/Design/FolderIconDemo/Assets");
        Reverse = args.Contains("--reverse", StringComparer.Ordinal);
        Directory.CreateDirectory(Output);
        if (args.Contains("--binary-only", StringComparer.Ordinal))
        {
            AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
            var assembly = Assembly.LoadFrom(Path.GetFullPath("Tools/Design/FolderIconDemo/bin/Debug/net10.0/osx-arm64/FolderIconDemo.dll"));
            var render = assembly.GetType("MacExplorer.Services.Impl.FrostedFolderRenderer")!.GetMethod("Render")!
                .CreateDelegate<Func<SKBitmap, SKBitmap?, int, float, float, bool, CancellationToken, Bitmap>>();
            var results = new Dictionary<string, object>();
            foreach (var pixels in new[] { 112, 256 })
            {
                using var first = FolderCoverRenderService.DecodePhoto(File.ReadAllBytes(Path.Combine(Assets, "reference-cover.png")), pixels, default);
                using var second = FolderCoverRenderService.DecodePhoto(File.ReadAllBytes(Path.Combine(Assets, "coast.jpg")), pixels, default);
                void Draw() { using var bitmap = render(first, second, pixels, FrostedFolderRenderer.DefaultFrost, 10, false, default); }
                for (var i = 0; i < 4; i++) Draw();
                var samples = new double[24];
                for (var i = 0; i < samples.Length; i++)
                { var start = Stopwatch.GetTimestamp(); Draw(); samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
                Array.Sort(samples);
                results[pixels.ToString()] = new { p50Ms = samples[11], p95Ms = samples[21], decodedBytes = pixels * pixels * 4 };
            }
            File.WriteAllText(Path.Combine(Output, "actual-demo-binary.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        AppBuilder.Configure<BenchApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
    }
}

public sealed class BenchApp : Application
{
    public override void Initialize()
    {
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://MacExplorer/"))
            { Source = new Uri("avares://MacExplorer/Assets/TypographyTokens.axaml") });
        Styles.Add(new FluentTheme());
        foreach (var asset in new[] { "Assets/ThemeTokens.axaml", "Assets/Styles.axaml", "Controls/AppWindow.axaml", "Assets/ComponentStyles.axaml" })
            Styles.Add(new StyleInclude(new Uri("avares://MacExplorer/")) { Source = new Uri("avares://MacExplorer/" + asset) });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var list = new FastFileList
            {
                IsGrid = true, Background = Brushes.White, Foreground = Brushes.Black, Secondary = Brushes.Gray,
                Selected = Brushes.LightBlue, SelectedHover = Brushes.LightBlue,
                FontFamily = new FontFamily("PingFang SC, Helvetica Neue")
            };
            var window = new AppWindow { Title = "FKFinder", Width = 1000, Height = 720, Content = new ScrollViewer { Content = list } };
            desktop.MainWindow = window;
            window.Opened += async (_, _) =>
            {
                try { await Assessment.RunAsync(list); desktop.Shutdown(); }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(Program.Output, "error.txt"), ex.ToString());
                    Console.Error.WriteLine(ex); desktop.Shutdown(1);
                }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal static class Assessment
{
    private static readonly Func<IReadOnlyList<SKBitmap>, int, byte[]> Existing = typeof(FolderPhotoCoverService)
        .GetMethod("Compose", BindingFlags.NonPublic | BindingFlags.Static)!
        .CreateDelegate<Func<IReadOnlyList<SKBitmap>, int, byte[]>>();

    public static async Task RunAsync(FastFileList list)
    {
        var first = File.ReadAllBytes(Path.Combine(Program.Assets, "reference-cover.png"));
        var second = File.ReadAllBytes(Path.Combine(Program.Assets, "coast.jpg"));
        var material = new FolderCoverMaterial(FrostedFolderRenderer.DefaultFrost, 10, false, true);
        var report = new Dictionary<string, object>
        {
            ["scope"] = "Optimized compositor source; 2 photos. Synthesis excludes source reads/decodes. Native 10k-row list uses the unmodified viewport scheduler, cancellation and 16 MiB image cache; both providers decode the same in-memory source bytes. CPU/offscreen raster timing, not GPU presentation FPS.",
            ["rendering"] = await Task.Run(() => Rendering(first, second)),
            ["cache"] = await Cache(first, second, material)
        };
        foreach (var size in new[] { 112, 256 })
        {
            using var a = FolderCoverRenderService.DecodePhoto(first, size, default);
            using var b = FolderCoverRenderService.DecodePhoto(second, size, default);
            report[$"cached_existing_{size}"] = CachedDraw(Existing([a, b], size));
            report[$"cached_glass_{size}"] = CachedDraw(FrostedFolderRenderer.RenderPng(a, b, size));
        }
        var rows = Enumerable.Range(0, 10_000).Select(i => new FileSystemEntry
            { FullPath = $"/synthetic-albums/{i:D5}", Name = $"Album {i:D5}", IsDirectory = true, IconKey = "folder" }).ToArray();
        list.SetRows(rows);
        await Task.Delay(200);
        using var oldGate = new SemaphoreSlim(2);
        var glass = new FolderCoverRenderService();
        var oldGenerated = 0;
        Task<ThumbnailResult?> Old(FileSystemEntry _, int pixels, CancellationToken ct) => Task.Run(async () =>
        {
            await oldGate.WaitAsync(ct);
            try
            {
                using var a = FolderCoverRenderService.DecodePhoto(first, pixels, ct);
                using var b = FolderCoverRenderService.DecodePhoto(second, pixels, ct);
                var bytes = Existing([a, b], pixels); ct.ThrowIfCancellationRequested();
                Interlocked.Increment(ref oldGenerated);
                return (ThumbnailResult?)new ThumbnailResult(bytes, "");
            }
            finally { oldGate.Release(); }
        }, ct);
        Task<ThumbnailResult?> New(FileSystemEntry _, int pixels, CancellationToken ct)
            => glass.CreateAsync(first, second, pixels, material, ct);
        if (Program.Reverse)
        {
            await Scroll(list, rows, New);
            report["scroll_glass"] = await Scroll(list, rows, New);
            report["scroll_existing"] = await Scroll(list, rows, Old);
            report["scroll_glass_repeat"] = await Scroll(list, rows, New);
        }
        else
        {
            await Scroll(list, rows, Old);
            report["scroll_existing"] = await Scroll(list, rows, Old);
            report["scroll_glass"] = await Scroll(list, rows, New);
            report["scroll_existing_repeat"] = await Scroll(list, rows, Old);
        }
        report["glass_maximum_concurrent"] = glass.MaximumActive;
        report["glass_generated"] = glass.Generated;
        report["existing_generated"] = oldGenerated;
        if (glass.MaximumActive > 2 || glass.Generated == 0) throw new InvalidOperationException("Unexpected glass work scheduling.");
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(Program.Output, "performance.json"), json);
        Console.WriteLine(json);
    }

    private static object Rendering(byte[] first, byte[] second)
    {
        var result = new Dictionary<string, object>();
        foreach (var size in new[] { 112, 256 })
        {
            using var a = FolderCoverRenderService.DecodePhoto(first, size, default);
            using var b = FolderCoverRenderService.DecodePhoto(second, size, default);
            void Old() { using var s = new MemoryStream(Existing([a, b], size)); using var image = new Bitmap(s); }
            void New() { using var image = FrostedFolderRenderer.Render(a, b, size); }
            for (var i = 0; i < 5; i++) { Old(); New(); }
            result[$"existing_{size}"] = Measure(Old, 40);
            result[$"glass_{size}"] = Measure(New, 40);
            result[$"decoded_bytes_{size}"] = size * size * 4;
        }
        return result;
    }

    private static async Task<object> Cache(byte[] first, byte[] second, FolderCoverMaterial material)
    {
        using var cache = new HomeThumbnailCache();
        var service = new FolderCoverRenderService();
        var key = new FileSystemEntry { FullPath = "/synthetic/shared", ThumbnailUrl = "v1:18:10" };
        var start = Stopwatch.GetTimestamp();
        var consumers = Enumerable.Range(0, 12).Select(_ => cache.AcquireAsync(key, 112,
            (_, pixels, ct) => service.CreateAsync(first, second, pixels, material, ct), default)).ToArray();
        var leases = await Task.WhenAll(consumers);
        var cold = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (service.Generated != 1 || leases.Any(lease => !ReferenceEquals(lease.Bitmap, leases[0].Bitmap)))
            throw new InvalidOperationException("Cache consumers did not share a single cover.");
        var before = service.Generated;
        var hits = Measure(() => { using var hit = cache.TryAcquire(key, 112); if (hit?.Bitmap == null) throw new InvalidOperationException(); }, 1000);
        if (service.Generated != before) throw new InvalidOperationException("A cache hit generated a new cover.");
        foreach (var lease in leases) lease.Dispose();
        var snapshot = ((IMemoryDiagnosticSource)cache).GetMemoryState();
        return new { concurrentConsumers = 12, generated = service.Generated, hits,
            initialWaitIncluding80MsDebounce = cold, cacheState = snapshot };
    }

    private static object CachedDraw(byte[] png)
    {
        var owned = new List<Bitmap>();
        var grid = new UniformGrid { Columns = 8, Rows = 6, Width = 640, Height = 480 };
        for (var i = 0; i < 48; i++)
        {
            using var stream = new MemoryStream(png); var bitmap = new Bitmap(stream); owned.Add(bitmap);
            grid.Children.Add(new Image { Source = bitmap, Width = 64, Height = 64 });
        }
        grid.Measure(new Size(640, 480)); grid.Arrange(new Rect(0, 0, 640, 480));
        using var target = new RenderTargetBitmap(new PixelSize(1280, 960), new Vector(192, 192));
        for (var i = 0; i < 5; i++) target.Render(grid);
        var result = Measure(() => target.Render(grid), 60);
        foreach (var bitmap in owned) bitmap.Dispose();
        return result;
    }

    private static async Task<object> Scroll(FastFileList list, FileSystemEntry[] rows,
        Func<FileSystemEntry, int, CancellationToken, Task<ThumbnailResult?>> provider)
    {
        list.FolderCoverProvider = provider; list.FolderCoversEnabled = true;
        list.ScrollToOffset(0); await Task.Delay(350);
        var scale = TopLevel.GetTopLevel(list)?.RenderScaling ?? 1;
        using var target = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(list.Bounds.Width * scale),
            (int)Math.Ceiling(list.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        var rapid = new double[90];
        for (var frame = 0; frame < rapid.Length; frame++)
        {
            var start = Stopwatch.GetTimestamp(); list.ScrollToEntry(rows[(frame * 137) % rows.Length]);
            target.Render(list); rapid[frame] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            await Task.Delay(16);
        }
        var dwell = new double[12];
        for (var frame = 0; frame < dwell.Length; frame++)
        {
            list.ScrollToEntry(rows[(frame * 233) % rows.Length]); await Task.Delay(250);
            var start = Stopwatch.GetTimestamp(); target.Render(list);
            dwell[frame] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        list.ScrollToEntry(rows[5000]); await Task.Delay(250);
        var offset = list.Offset.Y;
        var continuous = new double[120];
        for (var frame = 0; frame < continuous.Length; frame++)
        {
            var start = Stopwatch.GetTimestamp(); list.ScrollToOffset(offset + frame * 3.5); target.Render(list);
            continuous[frame] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            await Task.Delay(16);
        }
        var images = (FastFileListImages)typeof(FastFileList).GetField("_images", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(list)!;
        return new { rapid = Stats(rapid), dwell = Stats(dwell), continuous = Stats(continuous), scale,
            cacheBytes = images.CachedBytes, pending = images.PendingCount };
    }

    private static object Measure(Action action, int count)
    {
        var samples = new double[count];
        var allocation = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < count; i++)
        { var start = Stopwatch.GetTimestamp(); action(); samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
        return new { latency = Stats(samples), managedBytesPerCall = (GC.GetAllocatedBytesForCurrentThread() - allocation) / count };
    }

    private static object Stats(double[] samples)
    {
        Array.Sort(samples);
        return new { count = samples.Length, p50Ms = samples[(samples.Length - 1) / 2], p95Ms = samples[(int)Math.Floor((samples.Length - 1) * .95)] };
    }
}
