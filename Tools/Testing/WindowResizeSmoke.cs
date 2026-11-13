#if DEBUG
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views;
using MacExplorer.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.Testing;

internal static class WindowResizeSmoke
{
    private const string Graphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    internal static async Task RunAsync(MainWindow owner)
    {
        var output = Path.Combine(RuntimePaths.TestRoot!, "window-resize");
        Directory.CreateDirectory(output);
        var results = new List<object>();
        string? error = null;
        Window? window = null;
        try
        {
            FolderTabRestoreSmoke.ActivateApplication();
            owner.ShowAndActivate();
            App.Services.GetRequiredService<ILocalizationService>().SetLanguage("zh-CN");
            var path = Path.Combine(RuntimePaths.TestRoot!, "resize-example.txt");
            await File.WriteAllTextAsync(path, "Window resize preview\nneedle\n");
            var entry = new FileSystemEntry { Name = "图片 2026-11-10 13.52.05.png", FullPath = path, Size = 1667236,
                LastModified = new DateTime(2026, 11, 10, 13, 52, 0) };
            if (Environment.GetEnvironmentVariable("MACEXPLORER_WINDOW_RESIZE_SMOKE") == "interactive")
            {
                window = new FileConflictDialog(new(entry, entry, false, false))
                {
                    Width = 440, Height = 420, SizeToContent = SizeToContent.Manual,
                    RequestedThemeVariant = ThemeVariant.Light
                };
                Console.Error.WriteLine($"Interactive resize dialog: {output}");
                await window.ShowDialog(owner);
                window = null;
                return;
            }
            if (!CGPreflightPostEventAccess()) throw new InvalidOperationException("Native pointer event access is unavailable.");
            (string Name, Func<Window> Create)[] cases =
            [
                ("conflict", () => new FileConflictDialog(new(entry, entry, false, false))),
                ("git", () => new GitDiffDialog("--- a/example.txt\n+++ b/example.txt\n@@ -1 +1 @@\n-before\n+after\n", false)),
                ("content", () => new ContentMatchDialog(entry, "needle")),
                ("remote-edit", () => new RemoteEditDialog(App.Services.GetRequiredService<IRemoteFileEditService>()))
            ];
            foreach (var (name, create) in cases)
            foreach (var dark in new[] { false, true })
            {
                window = create();
                window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                window.Width = 616; window.Height = 420;
                var closed = window.ShowDialog(owner);
                await Task.Delay(250);
                var handle = (IMacOSTopLevelPlatformHandle)window.TryGetPlatformHandle()!;
                var number = SendInteger(handle.NSWindow, Selector("windowNumber"));
                var samples = new List<JsonElement>();
                void Sample(object? sender, AvaloniaPropertyChangedEventArgs args)
                {
                    if (args.Property != TopLevel.ClientSizeProperty || samples.Count >= 256) return;
                    var bytes = new byte[4096];
                    var count = NativeSnapshot(handle.NSView, bytes, bytes.Length);
                    if (count <= 0) return;
                    using var json = JsonDocument.Parse(bytes.AsMemory(0, count)); samples.Add(json.RootElement.Clone());
                }
                window.PropertyChanged += Sample;
                var prefix = name + (dark ? "-dark" : "-light");
                var start = window.ClientSize;
                await Capture(number, Path.Combine(output, prefix + "-before.png"));
                await DragAsync(window, 200, 80, number, output, prefix + "-grow");
                var grown = window.ClientSize;
                await DragAsync(window, -200, -80, number, output, prefix + "-shrink");
                await Task.Delay(150);
                window.PropertyChanged -= Sample;
                await Capture(number, Path.Combine(output, prefix + "-after.png"));
                var resized = grown.Width > start.Width + 150 && grown.Height > start.Height + 50;
                var liveSamples = samples.Count(sample => sample.GetProperty("liveResize").GetBoolean());
                var preservesPixels = samples.All(sample => sample.GetProperty("contentsGravity").GetString() is "topLeft" or "bottomLeft"
                    && sample.GetProperty("contentsScale").GetDouble() == sample.GetProperty("windowScale").GetDouble()
                    && sample.GetProperty("animationCount").GetInt32() == 0);
                var fillsBackground = samples.All(sample => sample.GetProperty("backgroundAlpha").GetDouble() == 1
                    && sample.GetProperty("cornerRadius").GetDouble() > 0);
                results.Add(new { name, dark, resized, liveSamples, preservesPixels, fillsBackground, start, grown, end = window.ClientSize, samples });
                if (!resized || liveSamples == 0) throw new InvalidOperationException($"No native live resize observed: {prefix}.");
                if (!preservesPixels) throw new InvalidOperationException($"Render layer stretches outstanding frames: {prefix}.");
                if (!fillsBackground) throw new InvalidOperationException($"Dialog background is transparent during resize: {prefix}.");
                window.Close(); await closed; window = null;
            }
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            window?.Close();
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { results, error }, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine($"Window resize smoke: {output}; error={error}");
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(error == null ? 0 : 1);
        }
    }

    private static async Task DragAsync(Window window, double dx, double dy, nint number, string output, string prefix)
    {
        var origin = window.PointToScreen(new Point(window.ClientSize.Width - 6, window.ClientSize.Height - 6));
        var start = new NativePoint(origin.X, origin.Y);
        var drag = Task.Run(() =>
        {
            Post(5, start);
            Post(1, start);
            try
            {
                for (var step = 1; step <= 90; step++)
                {
                    Post(6, new(start.X + dx * step / 90, start.Y + dy * step / 90));
                    Thread.Sleep(16);
                }
            }
            finally { Post(2, new(start.X + dx, start.Y + dy)); }
        });
        var captures = Task.Run(async () =>
        {
            for (var frame = 0; frame < 4; frame++)
            {
                await Task.Delay(180);
                await Capture(number, Path.Combine(output, $"{prefix}-{frame}.png"));
            }
        });
        await Task.WhenAll(drag, captures);
        await Task.Delay(100);
    }

    private static async Task Capture(nint number, string path)
    {
        var start = new ProcessStartInfo("/usr/sbin/screencapture") { UseShellExecute = false };
        foreach (var arg in new[] { "-x", "-o", "-l", number.ToString(), path }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException("Native screenshot failed.");
    }

    private static void Post(int type, NativePoint position)
    {
        var ev = CGEventCreateMouseEvent(IntPtr.Zero, type, position, 0);
        try { CGEventPost(0, ev); }
        finally { CFRelease(ev); }
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(double X, double Y);
    [DllImport(Graphics)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGPreflightPostEventAccess();
    [DllImport(Graphics)] private static extern IntPtr CGEventCreateMouseEvent(IntPtr source, int type, NativePoint position, int button);
    [DllImport(Graphics)] private static extern void CGEventPost(int tap, IntPtr ev);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(IntPtr value);
    [DllImport(ObjC, EntryPoint = "sel_registerName")] private static extern IntPtr Selector(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendInteger(IntPtr target, IntPtr selector);
    [DllImport("libMacExplorerNativeDrag", EntryPoint = "me_window_render_snapshot")] private static extern int NativeSnapshot(IntPtr view, [Out] byte[] bytes, int capacity);
}
#endif
