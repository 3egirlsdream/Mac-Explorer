using Avalonia.Controls;
using Avalonia.Threading;

namespace MacExplorer.PluginUi;

public static class PluginWindows
{
    public static async Task<bool> ShowAsync(Func<Window> create, CancellationToken token)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Window? window = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            window = create();
            window.Closed += (_, _) => completion.TrySetResult(window.Tag is true);
            window.Show();
            window.Activate();
        });
        using var registration = token.Register(() => Dispatcher.UIThread.Post(() => window?.Close()));
        return await completion.Task.WaitAsync(token);
    }

    // Set completion before closing; a title-bar close is cancellation.
    public static void Complete(Window window) { window.Tag = true; window.Close(); }
}
