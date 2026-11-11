using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Subscriptions;

namespace MacExplorer.Views;

public partial class SuperPreviewView
{
    private VideoPreviewHost? _videoPreview;
    private AppWindow? _videoWindow;
    private DialogHost? _videoDialog;
    private SubscriptionService? _videoSubscription;
    private bool _videoReady;

    private bool TryShowVideoPreview(string path, FileSystemEntry entry, CancellationToken token, int version)
    {
        if (_isCompactPreview || !IsVideoFile(entry) || !OperatingSystem.IsMacOS()
            || TopLevel.GetTopLevel(this)?.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle) return false;

        var host = new VideoPreviewHost(
            key => RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = key }),
            loaded =>
            {
                if (token.IsCancellationRequested || version != _operationVersion || _videoPreview == null) return;
                if (!loaded) { ShowVideoError(); return; }
                _videoReady = true;
                PreviewPlaceholder.IsVisible = false;
                UpdateVideoVisibility();
            }) { IsVisible = false };
        try
        {
            _videoPreview = host;
            host.Load(path);
            PreviewHost.Children.Add(host);
            _videoWindow = TopLevel.GetTopLevel(this) as AppWindow;
            _videoDialog = _videoWindow?.GetVisualDescendants().OfType<DialogHost>().FirstOrDefault();
            if (_videoWindow != null)
            {
                _videoWindow.PropertyChanged += OnVideoOverlayChanged;
                _videoWindow.Closed += OnVideoWindowClosed;
            }
            if (_videoDialog != null) _videoDialog.PropertyChanged += OnVideoOverlayChanged;
            _videoSubscription = SubscriptionAccess.Current;
            if (_videoSubscription != null) _videoSubscription.Changed += UpdateVideoVisibility;
            UpdateVideoVisibility();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Native video preview: {ex.Message}");
            ShowVideoError();
        }
        return true;
    }

    private void ShowVideoError() => ShowPlaceholder(LocalizationText.Get("ui.preview-video-unplayable"));

    private void OnVideoWindowClosed(object? sender, EventArgs e) => ClearVideoPreview();

    private void OnVideoOverlayChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == AppWindow.IsModalInteractionBlockedProperty || e.Property == AppWindow.IsUpgradeOpenProperty
            || e.Property == IsVisibleProperty) UpdateVideoVisibility();
    }

    private void UpdateVideoVisibility()
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateVideoVisibility);
            return;
        }
        if (_videoPreview != null)
            _videoPreview.IsVisible = _videoReady && _videoWindow?.IsModalInteractionBlocked != true
                && _videoWindow?.IsUpgradeOpen != true && _videoDialog?.IsVisible != true;
    }

    private void ClearVideoPreview()
    {
        if (_videoWindow != null)
        {
            _videoWindow.PropertyChanged -= OnVideoOverlayChanged;
            _videoWindow.Closed -= OnVideoWindowClosed;
        }
        if (_videoDialog != null) _videoDialog.PropertyChanged -= OnVideoOverlayChanged;
        if (_videoSubscription != null) _videoSubscription.Changed -= UpdateVideoVisibility;
        _videoWindow = null;
        _videoDialog = null;
        _videoSubscription = null;
        _videoReady = false;
        if (_videoPreview == null) return;
        _videoPreview.Dispose();
        PreviewHost.Children.Remove(_videoPreview);
        _videoPreview = null;
    }
}
