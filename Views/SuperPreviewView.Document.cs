using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Services.Subscriptions;
using MacExplorer.Services.Impl;

namespace MacExplorer.Views;

public partial class SuperPreviewView
{
    private DocumentPreviewHost? _documentPreview;
    private AppWindow? _documentWindow;
    private DialogHost? _documentDialog;
    private SubscriptionService? _documentSubscription;

    internal static bool IsDocumentFile(string name)
        => Path.GetExtension(name).ToLowerInvariant() is ".pdf" or ".doc" or ".docx"
            or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".odt" or ".ods" or ".odp"
            or ".pages" or ".numbers" or ".key" or ".rtf";

    private bool TryShowDocumentPreview(string path, string name)
    {
        if (!IsDocumentFile(name) || !OperatingSystem.IsMacOS()
            || TopLevel.GetTopLevel(this)?.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle)
            return false;

        var host = new DocumentPreviewHost(key =>
            RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = key }));
        try
        {
            var result = host.Load(path, Path.GetExtension(name).Equals(".pdf", StringComparison.OrdinalIgnoreCase), !_isCompactPreview);
            if (result != 1)
            {
                host.Dispose();
                ShowPlaceholder(result == 2
                    ? LocalizationService.Current?.Get("ui.preview-pdf-locked") ?? "此 PDF 已加密，请解锁后预览"
                    : LocalizationService.Current?.Get("ui.preview-document-unreadable") ?? "无法读取此文档");
                return true;
            }
            _documentPreview = host;
            PreviewHost.Children.Add(host);
            PreviewPlaceholder.IsVisible = false;
            PreviewImage.IsVisible = false;
            PreviewTextScroll.IsVisible = false;
            FolderSummary.IsVisible = false;
            _documentWindow = TopLevel.GetTopLevel(this) as AppWindow;
            _documentDialog = _documentWindow?.GetVisualDescendants().OfType<DialogHost>().FirstOrDefault();
            if (_documentWindow != null) _documentWindow.PropertyChanged += OnDocumentOverlayChanged;
            if (_documentDialog != null) _documentDialog.PropertyChanged += OnDocumentOverlayChanged;
            _documentSubscription = SubscriptionAccess.Current;
            if (_documentSubscription != null) _documentSubscription.Changed += UpdateDocumentVisibility;
            UpdateDocumentVisibility();
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Native document preview: {ex.Message}");
            if (ReferenceEquals(_documentPreview, host)) ClearDocumentPreview();
            else host.Dispose();
            return false;
        }
    }

    private void OnDocumentOverlayChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == AppWindow.IsModalInteractionBlockedProperty || e.Property == IsVisibleProperty)
            UpdateDocumentVisibility();
    }

    private void UpdateDocumentVisibility()
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateDocumentVisibility);
            return;
        }
        if (_documentPreview != null)
            _documentPreview.IsVisible = _documentWindow?.IsModalInteractionBlocked != true
                && _documentDialog?.IsVisible != true && !SubscriptionAccess.IsLocked;
    }

    private void ClearDocumentPreview()
    {
        if (_documentWindow != null) _documentWindow.PropertyChanged -= OnDocumentOverlayChanged;
        if (_documentDialog != null) _documentDialog.PropertyChanged -= OnDocumentOverlayChanged;
        if (_documentSubscription != null) _documentSubscription.Changed -= UpdateDocumentVisibility;
        _documentSubscription = null;
        _documentWindow = null;
        _documentDialog = null;
        if (_documentPreview == null) return;
        _documentPreview.Dispose();
        PreviewHost.Children.Remove(_documentPreview);
        _documentPreview = null;
    }
}
