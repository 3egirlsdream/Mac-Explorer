using Avalonia.Rendering.Composition;
using MacExplorer.Controls;

namespace MacExplorer.Views;

public partial class MainWindow
{
    private bool _activateWhenShown;
    private Task? _initialFrameCommit;
    private Task? _initialWindowShow;

    internal void ShowAndActivate()
    {
        _activateWhenShown = true;
        Show();
    }

    public override void Show()
    {
        if (_initialWindowShow is { IsCompleted: false }) return;
        if (!_initialized) _initialWindowShow = ShowInitialWindowAsync();
        else _ = ShowWithBackgroundAsync();
    }

    private async Task ShowInitialWindowAsync()
    {
        await PrepareInitialFolderTabsAsync();
        if (!_shutdownStarted) await ShowWithBackgroundAsync();
    }

    private Task ShowWithBackgroundAsync()
    {
        if (WindowBackgroundTheme.GetController(this) is { } background)
            return background.ShowWhenReadyAsync(ShowPreparedWindow);
        ShowPreparedWindow();
        return Task.CompletedTask;
    }

    private void ShowPreparedWindow()
    {
        if (_shutdownStarted) return;
        base.Show();
        if (!_activateWhenShown || _shutdownStarted || !IsVisible) return;
        _activateWhenShown = false;
        Activate();
    }

    internal Task WaitForInitialFrameAsync() => _initialFrameCommit ??=
        ElementComposition.GetElementVisual(this)?.Compositor.RequestCommitAsync() ?? Task.CompletedTask;
}
