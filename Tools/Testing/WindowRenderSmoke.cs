#if DEBUG
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using MacExplorer.Views;

namespace MacExplorer.Tools.Testing;

internal static class WindowRenderSmoke
{
    internal static async Task RunAsync(MainWindow window)
    {
        if (RuntimePaths.TestRoot == null) throw new InvalidOperationException("An isolated test profile is required.");
        var exitCode = 0;
        try
        {
            for (var cycle = 0; cycle < 3; cycle++)
            {
                foreach (var state in new[] { WindowState.Maximized, WindowState.Normal })
                {
                    window.RenderDiagnostics!.RecordRequest(state, "isolated-smoke");
                    window.WindowState = state;
                    // Allow the observation timers to run naturally; do not force layout or rendering.
                    await Task.Delay(3500);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            exitCode = 1;
        }
        finally
        {
            Console.Error.WriteLine($"Window rendering diagnostics: {RuntimePaths.LogDirectory}");
            ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Shutdown(exitCode);
        }
    }
}
#endif
