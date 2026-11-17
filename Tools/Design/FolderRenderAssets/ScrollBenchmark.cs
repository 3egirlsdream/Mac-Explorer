using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using SkiaSharp;

internal sealed class ScrollBenchmark : Application
{
    internal static string Output = "";
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
            var list = new FastFileList { IsGrid = true, Background = Brushes.White, Foreground = Brushes.Black,
                Secondary = Brushes.Gray, Selected = Brushes.LightBlue, SelectedHover = Brushes.LightBlue,
                FontFamily = new FontFamily("PingFang SC, Helvetica Neue") };
            var window = new AppWindow { Title = "Folder render benchmark", Width = 1000, Height = 720, Content = new ScrollViewer { Content = list } };
            desktop.MainWindow = window;
            window.Opened += async (_, _) =>
            {
                try { await Run(list); desktop.Shutdown(); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(Output, "error.txt"), ex.ToString()); desktop.Shutdown(1); }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
    private static async Task Run(FastFileList list)
    {
        using var input = AssetLoader.Open(new Uri("avares://MacExplorer/Assets/FolderCoverSamples/alpine-lake.jpg"));
        using var memory = new MemoryStream(); input.CopyTo(memory); var bytes = memory.ToArray();
        using var gate = new SemaphoreSlim(2);
        var rows = Enumerable.Range(0, 10_000).Select(i => new FileSystemEntry
            { FullPath = $"/synthetic-albums/{i:D5}", Name = $"Album {i:D5}", IsDirectory = true, IconKey = "folder" }).ToArray();
        list.SetRows(rows); list.FolderCoversEnabled = true;
        var results = new Dictionary<string, object>();
        foreach (var style in Enum.GetValues<FolderPhotoCoverStyle>())
        {
            list.InvalidateFolderCovers(); list.FolderIconStyle = style;
            list.FolderCoverProvider = (_, pixels, token) => Task.Run(async () =>
            {
                await gate.WaitAsync(token);
                try
                {
                    using var photo = BoundedImageDecoder.DecodeSkia(bytes, pixels, token)!;
                    return (ThumbnailResult?)new ThumbnailResult(FolderPhotoCoverService.ComposeStyle([photo, photo], pixels, style, token), "");
                }
                finally { gate.Release(); }
            }, token);
            list.ScrollToOffset(0); await Task.Delay(400);
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
            list.ScrollToEntry(rows[5000]); await Task.Delay(500);
            var offset = list.Offset.Y; var continuous = new double[120];
            for (var frame = 0; frame < continuous.Length; frame++)
            {
                var start = Stopwatch.GetTimestamp(); list.ScrollToOffset(offset + frame * 3.5); target.Render(list);
                continuous[frame] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                await Task.Delay(16);
            }
            var images = (FastFileListImages)typeof(FastFileList).GetField("_images", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(list)!;
            results[style.ToString()] = new { rapid = Stats(rapid), continuous = Stats(continuous), scale,
                cacheBytes = images.CachedBytes, pending = images.PendingCount };
        }
        File.WriteAllText(Path.Combine(Output, "scroll.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static object Stats(double[] values) { Array.Sort(values); return new { p50Ms = values[values.Length / 2], p95Ms = values[(int)(values.Length * .95)] }; }
}
