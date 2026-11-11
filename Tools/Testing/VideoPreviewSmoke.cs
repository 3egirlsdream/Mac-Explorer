#if DEBUG
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Views;

namespace MacExplorer.Tools.Testing;

internal static class VideoPreviewSmoke
{
    internal static async Task RunAsync(MainWindow owner)
    {
        var root = RuntimePaths.TestRoot ?? throw new InvalidOperationException("An isolated test profile is required.");
        var output = Path.Combine(root, "video-preview");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        var preview = owner.FindControl<SuperPreviewView>("SuperPreviewControl")!;
        AppWindow? probe = null;
        string? error = null;
        try
        {
            var helper = Environment.GetEnvironmentVariable("MACEXPLORER_VIDEO_FIXTURE_HELPER")
                ?? throw new InvalidOperationException("Set MACEXPLORER_VIDEO_FIXTURE_HELPER to the compiled VideoPreviewTests executable.");
            var path = Path.Combine(output, "中文 视频预览 — very long movie file name for compact window testing.MP4");
            await RunAsync(helper, ["--fixture", path]);
            var entry = Entry(path);
            owner.Width = 1050; owner.Height = 720;
            foreach (var dark in new[] { false, true })
            {
                owner.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                foreach (var narrow in new[] { false, true })
                {
                    owner.MinWidth = 0; owner.Width = narrow ? 680 : 1050;
                    await preview.OpenAsync(entry);
                    await ReadyAsync();
                    Require(Rate(Player()) == 0, "Preview must start paused.");
                    await CaptureAsync($"{(dark ? "dark" : "light")}-{(narrow ? "narrow" : "wide")}");
                    checks.Add($"{(dark ? "dark" : "light")}/{(narrow ? "narrow" : "wide")}: first frame and paused");
                    preview.Close();
                }
            }

            await preview.OpenAsync(entry);
            await ReadyAsync();
            var player = Player();
            SendVoid(player, Selector("play"));
            await WaitAsync(() => Rate(player) > 0);
            owner.IsModalInteractionBlocked = true;
            await WaitAsync(() => Rate(player) == 0 && !Host()!.IsVisible);
            owner.IsModalInteractionBlocked = false;
            await ReadyAsync();
            Require(Rate(player) == 0, "Dismissed modal resumed playback.");
            checks.Add("modal overlay pauses; dismissing keeps paused");

            var dialog = owner.GetVisualDescendants().OfType<DialogHost>().First();
            SendVoid(player, Selector("play"));
            var confirmation = dialog.ShowConfirmationAsync("Video fixture", "Playback should pause", "OK");
            await WaitAsync(() => Rate(player) == 0 && !Host()!.IsVisible);
            dialog.FindControl<Button>("CancelButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await confirmation;
            await ReadyAsync();
            Require(Rate(player) == 0, "Dismissed confirmation resumed playback.");
            checks.Add("confirmation hides player and pauses");

            SendVoid(player, Selector("play"));
            owner.WindowState = WindowState.Minimized;
            var nativeWindow = ((IMacOSTopLevelPlatformHandle)owner.TryGetPlatformHandle()!).NSWindow;
            await WaitAsync(() => Rate(player) == 0 && SendBool(nativeWindow, Selector("isMiniaturized")));
            await Task.Delay(300);
            owner.WindowState = WindowState.Normal;
            owner.ShowAndActivate();
            await WaitAsync(() => !SendBool(nativeWindow, Selector("isMiniaturized")));
            await Task.Delay(300);
            Require(Rate(player) == 0, "Restoring window resumed playback.");
            SendVoid(player, Selector("play"));
            preview.IsVisible = false;
            await WaitAsync(() => Rate(player) == 0);
            preview.IsVisible = true;
            await ReadyAsync();
            Require(Rate(player) == 0, "Showing preview resumed playback.");
            checks.Add("minimize and ancestor visibility pause without auto resume");

            foreach (var key in new ushort[] { 49, 53 })
            {
                var host = Host()!;
                var video = Send(NativeView(host), Selector("video"));
                var window = Send(video, Selector("window"));
                Require(SendPointerBool(window, Selector("makeFirstResponder:"), video), "Native player could not receive focus.");
                var eventClass = GetClass("NSEvent");
                var text = SendString(GetClass("NSString"), Selector("stringWithUTF8String:"), key == 49 ? " " : "\u001b");
                var keyEvent = CreateKeyEvent(eventClass,
                    Selector("keyEventWithType:location:modifierFlags:timestamp:windowNumber:context:characters:charactersIgnoringModifiers:isARepeat:keyCode:"),
                    10, default, 0, 0, SendInt(window, Selector("windowNumber")), IntPtr.Zero, text, text, false, key);
                SendPointer(Send(GetClass("NSApplication"), Selector("sharedApplication")), Selector("sendEvent:"), keyEvent);
                try { await WaitAsync(() => !preview.IsVisible); }
                catch (TimeoutException ex) { throw new InvalidOperationException($"Native key {key} did not close preview (effective={host.IsEffectivelyVisible}, native={NativeView(host)}).", ex); }
                try { await WaitAsync(() => NativeView(host) == IntPtr.Zero); }
                catch (TimeoutException ex) { throw new InvalidOperationException($"Native key {key} closed preview but retained the native view.", ex); }
                checks.Add($"native focus key {key}: closed and native view released");
                await preview.OpenAsync(entry);
                await ReadyAsync();
            }

            var bad = Entry(Path.Combine(output, "broken.mkv"));
            await File.WriteAllTextAsync(bad.FullPath, "unsupported video content");
            await preview.OpenAsync(bad);
            await WaitAsync(() => Host() == null && preview.FindControl<TextBlock>("PreviewPlaceholder")!.IsVisible);
            Require(preview.FindControl<TextBlock>("PreviewPlaceholder")!.Text == Services.Impl.LocalizationText.Get("ui.preview-video-unplayable"), "Missing localized video error.");
            checks.Add("unplayable video displays localized error");
            await preview.OpenAsync(bad);
            await preview.OpenAsync(entry);
            await ReadyAsync();
            Require(!preview.FindControl<TextBlock>("PreviewPlaceholder")!.IsVisible, "Stale failure replaced a newer video.");
            checks.Add("rapid replacement rejects stale error");

            var zip = Path.Combine(output, "videos.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntryFromFile(path, "影片/movie.MP4");
            var archived = new FileSystemEntry { FullPath = ArchivePathHelper.Build(zip, "影片/movie.MP4"), Name = "movie.MP4", Extension = ".MP4" };
            await preview.OpenAsync(archived);
            await ReadyAsync();
            Require(Rate(Player()) == 0, "Archive video must start paused.");
            await CaptureAsync("archive-video");
            preview.Close();
            checks.Add("archive video extracts, renders and closes");

            var references = new List<WeakReference>();
            for (var index = 0; index < 12; index++)
            {
                await preview.OpenAsync(entry);
                await ReadyAsync();
                references.Add(new WeakReference(Host()!));
                preview.Close();
            }
            await Task.Delay(200);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Require(references.All(reference => !reference.IsAlive), $"{references.Count(reference => reference.IsAlive)} of {references.Count} closed managed video hosts remained alive.");
            checks.Add("12 repeated previews release managed hosts");

            var closingPreview = new SuperPreviewView();
            probe = new AppWindow { Width = 720, Height = 480, Content = closingPreview };
            probe.Show(owner);
            await closingPreview.OpenAsync(entry);
            await WaitAsync(() => closingPreview.GetVisualDescendants().OfType<VideoPreviewHost>().SingleOrDefault() is { IsVisible: true });
            var closingHost = closingPreview.GetVisualDescendants().OfType<VideoPreviewHost>().Single();
            var closingPlayer = Send(Send(NativeView(closingHost), Selector("video")), Selector("player"));
            SendVoid(closingPlayer, Selector("play"));
            await WaitAsync(() => Rate(closingPlayer) > 0);
            probe.Close();
            await WaitAsync(() => NativeView(closingHost) == IntPtr.Zero);
            checks.Add("closing window while playing releases native view");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            probe?.Close();
            preview.Close();
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(
                new { success = error == null, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Video preview smoke: {output}; success={error == null}");
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                lifetime.Shutdown(error == null ? 0 : 1);
        }

        VideoPreviewHost? Host() => preview.GetVisualDescendants().OfType<VideoPreviewHost>().SingleOrDefault();
        IntPtr Player() => Send(Send(NativeView(Host()!), Selector("video")), Selector("player"));
        async Task ReadyAsync()
        {
            try
            {
                await WaitAsync(() => Host() is { IsVisible: true } host
                    && SendBool(Send(NativeView(host), Selector("video")), Selector("isReadyForDisplay")));
            }
            catch (TimeoutException ex)
            {
                throw new InvalidOperationException($"Video not ready: host={Host()?.IsVisible}, placeholder={preview.FindControl<TextBlock>("PreviewPlaceholder")!.Text}", ex);
            }
        }
        async Task CaptureAsync(string name)
        {
            owner.UpdateLayout();
            await Task.Delay(150);
            var native = (IMacOSTopLevelPlatformHandle)owner.TryGetPlatformHandle()!;
            await RunAsync("/usr/sbin/screencapture", ["-x", "-l", SendInt(native.NSWindow, Selector("windowNumber")).ToString(), Path.Combine(output, name + ".png")]);
        }
    }

    private static FileSystemEntry Entry(string path) => new() { FullPath = path, Name = Path.GetFileName(path), Extension = Path.GetExtension(path) };
    private static IntPtr NativeView(VideoPreviewHost host) => (IntPtr)typeof(VideoPreviewHost).GetField("_view", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
    private static float Rate(IntPtr player) => SendFloat(player, Selector("rate"));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task WaitAsync(Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("Video preview did not reach the expected state.");
            await Task.Delay(20);
        }
    }
    private static async Task RunAsync(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Require(process.ExitCode == 0, await output + await error);
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")] private static extern IntPtr GetClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")] private static extern IntPtr Selector(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr SendString(IntPtr target, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern void SendVoid(IntPtr target, IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern void SendPointer(IntPtr target, IntPtr selector, IntPtr argument);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern nint SendInt(IntPtr target, IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern float SendFloat(IntPtr target, IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool(IntPtr target, IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendPointerBool(IntPtr target, IntPtr selector, IntPtr argument);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr CreateKeyEvent(IntPtr target, IntPtr selector, nuint type, Point location,
        nuint modifiers, double timestamp, nint windowNumber, IntPtr context, IntPtr characters, IntPtr unmodified,
        [MarshalAs(UnmanagedType.I1)] bool repeat, ushort keyCode);
}
#endif
