using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Platform;
using MacExplorer;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using SkiaSharp;

internal static class Program
{
    private static readonly int[] Sizes = [16, 32, 48, 64, 96, 128, 160, 192, 224, 256, 512];
    [STAThread]
    private static void Main(string[] args)
    {
        if (RuntimePaths.TestRoot == null) throw new InvalidOperationException("Use Tools/Testing/run-isolated.sh.");
        if (OperatingSystem.IsMacOS() && MemoryDiagnostics.ReadPhysicalFootprint() == null)
            throw new InvalidOperationException("Place the matching libMacExplorerNativeDrag.dylib beside this executable.");
        var command = args[0];
        var output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        if (command == "scroll")
        {
            ScrollBenchmark.Output = output;
            AppBuilder.Configure<ScrollBenchmark>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
            return;
        }
        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
        if (command is "export" or "check") Export(output, command == "check");
        else if (command == "capture") Capture(output);
        else if (command == "bench") Bench(output);
        else if (command == "compare") Compare(output, Path.GetFullPath(args[2]));
        else throw new ArgumentException("export|check|capture|bench|compare <directory> [baseline]");
    }

    private static SKBitmap Photo(string name)
    {
        using var input = AssetLoader.Open(new Uri("avares://MacExplorer/Assets/" + name));
        using var stream = new MemoryStream(); input.CopyTo(stream);
        return BoundedImageDecoder.DecodeSkia(stream.ToArray(), 256)!;
    }
    private static byte[] Icon(FolderPhotoCoverStyle style, int size) => style == FolderPhotoCoverStyle.Frosted
        ? PlainFolderRenderer.RenderPng(size) : SkeuomorphicFolderRenderer.RenderPng(size, cacheMaterials: false);

    private static void Export(string output, bool check)
    {
        var files = new SortedDictionary<string, byte[]>();
        using var first = Photo("FolderCoverSamples/alpine-lake.jpg");
        using var second = Photo("BackgroundThemes/Previews/nature-trellis.png");
        using var third = Photo("BackgroundThemes/Previews/nature-marigold.png");
        foreach (var style in new[] { FolderPhotoCoverStyle.Frosted, FolderPhotoCoverStyle.Skeuomorphic })
        {
            var name = style.ToString().ToLowerInvariant();
            foreach (var size in Sizes) files[$"{name}-{size}.png"] = Icon(style, size);
            files[$"{name}-preview.png"] = style == FolderPhotoCoverStyle.Skeuomorphic
                ? SkeuomorphicFolderRenderer.RenderPng(256, [first, second, third], cacheMaterials: false)
                : FolderPhotoCoverService.ComposeStyle([first, second, third], 256, style);
        }
        using var grain = (SKImage)typeof(SkeuomorphicFolderRenderer).GetMethod("CreatePaperGrain", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        using var encoded = grain.Encode(SKEncodedImageFormat.Png, 100);
        files["paper-grain.png"] = encoded.ToArray();
        foreach (var (name, bytes) in files)
        {
            var path = Path.Combine(output, name);
            if (check)
            {
                using var expected = SKBitmap.Decode(bytes);
                using var actual = SKBitmap.Decode(path);
                if (actual == null || !expected.Bytes.AsSpan().SequenceEqual(actual.Bytes))
                    throw new InvalidOperationException("Asset differs: " + name);
            }
            else File.WriteAllBytes(path, bytes);
        }
        Console.WriteLine($"{commandName(check)} {files.Count} assets; {files.Values.Sum(b => b.Length)} bytes.");
        static string commandName(bool check) => check ? "Verified" : "Exported";
    }

    private static void Capture(string output)
    {
        using var landscape = Photo("FolderCoverSamples/alpine-lake.jpg");
        using var portrait = new SKBitmap(100, 256);
        using (var c = new SKCanvas(portrait)) { c.Clear(SKColors.Orange); using var p = new SKPaint { Color = SKColors.Purple }; c.DrawCircle(40, 90, 32, p); }
        using var transparent = new SKBitmap(160, 100);
        using (var c = new SKCanvas(transparent)) { c.Clear(SKColors.Transparent); using var p = new SKPaint { Color = new SKColor(30, 150, 220, 100), IsAntialias = true }; c.DrawOval(new SKRect(10, 10, 150, 90), p); }
        var photos = new[] { landscape, portrait, transparent };
        foreach (var style in Enum.GetValues<FolderPhotoCoverStyle>())
        {
            foreach (var size in Sizes)
                File.WriteAllBytes(Path.Combine(output, $"icon-{style}-{size}.png"), SvgIconCache.RenderFolderPng(size, style));
            foreach (var size in new[] { 32, 48, 96, 112, 128, 192, 256 })
            foreach (var count in new[] { 1, 2, 3 })
            foreach (var rotation in new[] { 0, 1, 2 })
            {
                var source = Enumerable.Range(0, count).Select(i => photos[(i + rotation) % 3]).ToArray();
                File.WriteAllBytes(Path.Combine(output, $"cover-{style}-{size}-{count}-{rotation}.png"),
                    FolderPhotoCoverService.ComposeStyle(source, size, style));
            }
        }
        Console.WriteLine("Captured " + Directory.GetFiles(output, "*.png").Length + " images.");
    }

    private static void Bench(string output)
    {
        using var first = Photo("FolderCoverSamples/alpine-lake.jpg");
        using var second = Photo("BackgroundThemes/Previews/nature-trellis.png");
        var results = new Dictionary<string, object>();
        foreach (var style in Enum.GetValues<FolderPhotoCoverStyle>())
        {
            var start = Stopwatch.GetTimestamp();
            var icon = SvgIconCache.GetFolderIcon(96, style);
            results[$"icon_{style}_coldMs"] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            results[$"icon_{style}_hit"] = Measure(() => GC.KeepAlive(SvgIconCache.GetFolderIcon(96, style)), 1000);
            GC.KeepAlive(icon);
            foreach (var size in new[] { 112, 256 })
            {
                for (var i = 0; i < 5; i++) FolderPhotoCoverService.ComposeStyle([first, second], size, style);
                results[$"cover_{style}_{size}"] = Measure(() => FolderPhotoCoverService.ComposeStyle([first, second], size, style), 60);
            }
        }
        results["workingSetBytes"] = Process.GetCurrentProcess().WorkingSet64;
        results["physicalFootprintBytes"] = MemoryDiagnostics.ReadPhysicalFootprint() ?? 0;
        results["managedBytes"] = GC.GetTotalMemory(false);
        var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "performance.json"), json);
        Console.WriteLine(json);
    }

    private static object Measure(Action action, int count)
    {
        var samples = new double[count];
        var generations = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < count; i++) { var start = Stopwatch.GetTimestamp(); action(); samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Array.Sort(samples);
        return new { p50Ms = samples[count / 2], p95Ms = samples[(int)(count * .95)], allocatedBytesPerImage = allocated / count,
            collections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - generations[i]).ToArray() };
    }

    private static void Compare(string current, string baseline)
    {
        var results = new List<object>();
        foreach (var path in Directory.GetFiles(baseline, "*.png"))
        {
            var name = Path.GetFileName(path);
            using var a = SKBitmap.Decode(path);
            using var b = SKBitmap.Decode(Path.Combine(current, name));
            if (b == null || a.Width != b.Width || a.Height != b.Height) throw new InvalidOperationException(name);
            var oldBytes = a.Bytes; var newBytes = b.Bytes;
            var max = 0; var changed = 0; var overTwo = 0;
            for (var i = 0; i < oldBytes.Length; i++)
            { var diff = Math.Abs(oldBytes[i] - newBytes[i]); max = Math.Max(max, diff); if (diff > 0) changed++; if (diff > 2) overTwo++; }
            results.Add(new { name, max, changed, overTwo, bytes = oldBytes.Length });
        }
        File.WriteAllText(Path.Combine(current, "comparison.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Compared {results.Count} images.");
    }
}
