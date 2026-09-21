using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;

namespace MacExplorer.Views;

public partial class MainWindow
{
    private HomeFolderTransition? _homeFolderTransition;

    internal async Task OpenHomeFolderAsync(HomeFolderCard source, HomeFolderContent content)
    {
        if (_homeFolderTransition != null) return;
        var previousFocus = FocusManager?.GetFocusedElement() as Control;
        var overlayHost = WindowOverlayHost ?? HomeFolderOverlayHost;
        var wasHitTestVisible = RootLayout.IsHitTestVisible;
        var transition = new HomeFolderTransition(source, content);
        _homeFolderTransition = transition;
        content.CloseRequested += transition.Close;
        overlayHost.Children.Add(transition);
        overlayHost.IsVisible = true;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (!ReferenceEquals(_homeFolderTransition, transition)) return;
            transition.Open();
            RootLayout.IsHitTestVisible = false;
            await transition.Completion;
        }
        finally
        {
            transition.Dispose();
            content.CloseRequested -= transition.Close;
            overlayHost.Children.Remove(transition);
            if (ReferenceEquals(overlayHost, HomeFolderOverlayHost)) overlayHost.IsVisible = false;
            RootLayout.IsHitTestVisible = wasHitTestVisible;
            if (ReferenceEquals(_homeFolderTransition, transition)) _homeFolderTransition = null;
            if (previousFocus != null && TopLevel.GetTopLevel(previousFocus) != null && previousFocus.IsEffectivelyVisible) previousFocus.Focus();
            else if (TopLevel.GetTopLevel(source) != null && source.IsEffectivelyVisible) source.Focus();
        }
    }

    internal void CloseHomeFolderImmediately() => _homeFolderTransition?.Dispose();
}
