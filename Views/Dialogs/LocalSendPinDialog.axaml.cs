using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MacExplorer.Controls;

namespace MacExplorer.Views.Dialogs;

public partial class LocalSendPinDialog : DialogWindow
{
    private readonly TaskCompletionSource<string?> _unownedResult = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LocalSendPinDialog()
    {
        InitializeComponent();
        Opened += (_, _) => PinBox.Focus();
        Closed += (_, _) => _unownedResult.TrySetResult(null);
    }

    public async Task<string?> ShowRequestAsync(Window? owner, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible) Close(null);
        }));
        if (owner is { IsVisible: true }) return await ShowDialog<string?>(owner);
        Show();
        Activate();
        return await _unownedResult.Task;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Complete(null);
    private void OnContinue(object? sender, RoutedEventArgs e) => Complete(PinBox.Text?.Trim());
    private void Complete(string? result) { _unownedResult.TrySetResult(result); Close(result); }
}
