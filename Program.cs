using Avalonia;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace MacExplorer;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        // Avalonia's macOS render timer is backed by CVDisplayLink. macOS can
        // briefly report no active display (especially after sleep, wake, or
        // when launched from Xcode while the display is reconfiguring), which
        // otherwise makes Avalonia abort before the first window is created.
        if (OperatingSystem.IsMacOS())
            WaitForMacDisplayLinkAsync().GetAwaiter().GetResult();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static async Task WaitForMacDisplayLinkAsync()
    {
        try
        {
            IntPtr displayLink;
            while (CVDisplayLinkCreateWithActiveCGDisplays(out displayLink) != 0)
                await Task.Delay(250).ConfigureAwait(false);

            if (displayLink != IntPtr.Zero)
                CVDisplayLinkRelease(displayLink);
        }
        catch (DllNotFoundException)
        {
            // Non-standard macOS environments may not expose CoreVideo; let
            // Avalonia perform its normal platform initialization in that case.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("/System/Library/Frameworks/CoreVideo.framework/CoreVideo")]
    private static extern int CVDisplayLinkCreateWithActiveCGDisplays(out IntPtr displayLinkOut);

    [DllImport("/System/Library/Frameworks/CoreVideo.framework/CoreVideo")]
    private static extern void CVDisplayLinkRelease(IntPtr displayLink);

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        var logPath = Path.Combine(AppContext.BaseDirectory, "macexplorer_crash.log");
        try { File.AppendAllText(logPath,
            $"[{DateTime.Now:HH:mm:ss.fff}] UnhandledException: {ex}\n\n"); } catch { }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "macexplorer_crash.log");
        try { File.AppendAllText(logPath,
            $"[{DateTime.Now:HH:mm:ss.fff}] UnobservedTaskException: {e.Exception}\n\n"); } catch { }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
