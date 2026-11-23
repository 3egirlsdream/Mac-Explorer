using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Icons = MacExplorer.Assets.Icons;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Markdown;
using MacExplorer.Services.Preview;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views;

public partial class SuperPreviewView
{
    private sealed record ToolbarButton(AppButton Button, PathIcon Icon, TextBlock Label)
    {
        private string? _iconData;

        public void Update(string key, string? tooltip = null, string? icon = null)
        {
            Label.Text = LocalizationText.Get(key);
            var name = tooltip == null ? Label.Text : LocalizationText.Get(tooltip);
            AutomationProperties.SetName(Button, name);
            ToolTip.SetTip(Button, name);
            if (icon == null || icon == _iconData) return;
            Icon.Data = Geometry.Parse(icon);
            _iconData = icon;
        }
    }

    private readonly List<ToolbarButton> _toolbarButtons = [];
    private ToolbarButton _branchAction = null!;
    private ToolbarButton _zoomAction = null!;
    private ToolbarButton _copyTextAction = null!;
    private ToolbarButton _firstPageAction = null!;
    private ToolbarButton _openAction = null!;
    private ToolbarButton _extractAction = null!;
    private ToolbarButton _revealAction = null!;
    private ToolbarButton _copyPathAction = null!;
    private ILocalizationService? _toolbarLanguage;
    private string? _loadedPreviewText;
    internal Action<FileSystemEntry>? ExtractToNamedFolder { get; set; }

    private void ConfigureToolbar()
    {
        _branchAction = AddToolbarButton(Icons.ChevronDown, async () =>
        {
            if (_selectedNode is { } node) await TogglePreviewNodeAsync(node);
        });
        _zoomAction = AddToolbarButton(Icons.Resize, () =>
        {
            ToggleImageZoom();
            return Task.CompletedTask;
        });
        _copyTextAction = AddToolbarButton(Icons.Copy, () => CopyPreviewTextAsync(_loadedPreviewText));
        _firstPageAction = AddToolbarButton(Icons.FilePdf, () =>
        {
            _documentPreview?.NavigateTo(1, string.Empty);
            return Task.CompletedTask;
        });
        _openAction = AddToolbarButton(Icons.Open, async () =>
        {
            if (_selectedEntry is { } entry && GetLocalPath(entry.FullPath) is { } path
                && App.Services.GetService<IApplicationLauncherService>() is { } launcher)
                await launcher.OpenFileAsync(path);
        });
        _extractAction = AddToolbarButton(Icons.Extract, () =>
        {
            if (_selectedEntry is { } entry) ExtractToNamedFolder?.Invoke(entry);
            return Task.CompletedTask;
        });
        _revealAction = AddToolbarButton(Icons.Location, async () =>
        {
            if (GetLocalPath(_selectedNode?.RevealPath ?? _selectedEntry?.FullPath) is { } path
                && App.Services.GetService<IApplicationLauncherService>() is { } launcher)
                await launcher.RevealInFinderAsync(path);
        });
        _copyPathAction = AddToolbarButton(Icons.CopyPath, () =>
            CopyPreviewTextAsync(_selectedNode?.DisplayPath ?? _selectedEntry?.FullPath));
        NativePreviewToolbarPopup.Closed += OnNativePreviewPopupClosed;
        NativePreviewPreviousPopup.Closed += OnNativePreviewPopupClosed;
        NativePreviewNextPopup.Closed += OnNativePreviewPopupClosed;

        void OnNativePreviewPopupClosed(object? sender, EventArgs e)
        {
            // Drain the owner's layout queue after the native view is detached.
            if (TopLevel.GetTopLevel(this) is { } owner)
                Dispatcher.UIThread.Post(owner.UpdateLayout, DispatcherPriority.Background);
        }
    }

    private ToolbarButton AddToolbarButton(string icon, Func<Task> execute)
    {
        var path = new PathIcon { Width = 16, Height = 16, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Data = Geometry.Parse(icon) };
        var label = new TextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        AppTypography.BindFontSize(label, AppTypography.Caption);
        var content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 5 };
        content.Children.Add(path);
        content.Children.Add(label);
        var button = new AppButton { Classes = { "ghost", "compact", "preview-toolbar-button" }, Content = content };
        button.Click += async (_, _) => await RunToolbarActionAsync(execute);
        PreviewToolbarButtons.Children.Add(button);
        var action = new ToolbarButton(button, path, label);
        _toolbarButtons.Add(action);
        return action;
    }

    private async Task RunToolbarActionAsync(Func<Task> execute)
    {
        try { await execute(); }
        catch (Exception ex) when (TopLevel.GetTopLevel(this) is Window window)
        {
            AppToast.ShowError(window, ex.Message);
        }
    }

    private static string? GetLocalPath(string? path)
        => path != null && Path.IsPathFullyQualified(path) ? path : null;

    private static Task CopyPreviewTextAsync(string? text)
        => text != null && App.Services.GetService<IClipboardService>() is { } clipboard
            ? clipboard.CopyTextAsync(text) : Task.CompletedTask;

    private void RefreshToolbarLanguage()
    {
        UpdatePreviewToolbar();
        if (_selectedNode is { } node && (node.IsArchive || node.Entry.IsFolder)) ShowNodeSummary(node);
    }

    private void UpdatePreviewToolbar()
    {
        if (_isCompactPreview || !IsVisible) return;
        if (_selectedEntry is not { } entry)
        {
            PreviewToolbar.IsVisible = false;
            UpdatePreviewToolbarPlacement();
            return;
        }

        var canExpand = _selectedNode?.CanExpand == true;
        var expanded = _selectedNode?.IsExpanded == true;
        var archive = _selectedNode?.IsArchive == true;
        var folder = entry.IsFolder;
        var localPath = GetLocalPath(entry.FullPath);
        var revealPath = GetLocalPath(_selectedNode?.RevealPath ?? entry.FullPath);
        var launcherAvailable = App.Services.GetService<IApplicationLauncherService>() != null;
        var clipboardAvailable = App.Services.GetService<IClipboardService>() != null;
        var icon = FileIconResolver.ResolveIconKey(Path.GetExtension(entry.Name), entry.Name);
        var image = !archive && !entry.IsDirectory && icon == "file-image";
        var pdf = !entry.IsDirectory && Path.GetExtension(entry.Name).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        var text = !entry.IsDirectory && !archive && !pdf && !IsDocumentFile(entry.Name)
            && !DiffPreviewDocument.IsDiff(entry.Name) && (IsTextFile(entry) || MarkdownDocument.IsMarkdown(entry.Name));
        var media = icon is "file-video" or "file-audio";
        var zoomed = ZoomedImageScroll.IsVisible;
        _branchAction.Update(expanded ? "preview.action.collapse" : "preview.action.expand", icon: expanded ? Icons.ChevronUp : Icons.ChevronDown);
        _zoomAction.Update(zoomed ? "preview.action.fit" : "preview.action.zoom",
            zoomed ? "preview.action.fit.tip" : "preview.action.zoom.tip");
        _copyTextAction.Update("preview.action.copy-text", "preview.action.copy-text.tip");
        _firstPageAction.Update("preview.action.first-page");
        _openAction.Update("preview.action.open", media ? "preview.action.player.tip" : "preview.action.open.tip",
            media ? Icons.Play : Icons.Open);
        _extractAction.Update("preview.action.extract", "preview.action.extract.tip");
        _revealAction.Update("preview.action.reveal", "ui.runtime.086");
        _copyPathAction.Update("ui.355");

        _branchAction.Button.IsVisible = canExpand;
        _zoomAction.Button.IsVisible = image;
        _zoomAction.Button.IsEnabled = _previewBitmap != null;
        _copyTextAction.Button.IsVisible = text && clipboardAvailable;
        _copyTextAction.Button.IsEnabled = _loadedPreviewText != null;
        _firstPageAction.Button.IsVisible = pdf;
        _firstPageAction.Button.IsEnabled = _documentPreview != null;
        _openAction.Button.IsVisible = launcherAvailable && localPath != null && !archive && !folder;
        _extractAction.Button.IsVisible = archive && localPath != null && ExtractToNamedFolder != null;
        _revealAction.Button.IsVisible = launcherAvailable && revealPath != null;
        _copyPathAction.Button.IsVisible = clipboardAvailable && (localPath != null || ArchivePathHelper.IsArchivePath(entry.FullPath));
        PreviewToolbar.IsVisible = _toolbarButtons.Any(action => action.Button.IsVisible);
        UpdateToolbarLabels();
        UpdatePreviewToolbarPlacement();
    }

    private void UpdatePreviewToolbarPlacement()
    {
        if (_isCompactPreview) return;

        // AppKit previews draw above the Avalonia window. Use an owned popup
        // over those previews so the same toolbar can still receive input.
        var native = _documentPreview != null || _videoPreview != null;
        if (native != (NativePreviewToolbarSlot.Child == PreviewToolbar))
        {
            if (native)
            {
                PreviewToolbarSlot.Child = null;
                NativePreviewToolbarSlot.Child = PreviewToolbar;
            }
            else
            {
                NativePreviewToolbarPopup.IsOpen = false;
                NativePreviewToolbarSlot.Child = null;
                PreviewToolbarSlot.Child = PreviewToolbar;
            }
        }

        // Leave the AVKit playback controls reachable under the floating toolbar.
        NativePreviewToolbarAnchor.Margin = _videoPreview == null ? default : new Thickness(0, 0, 0, 64);
        var window = _documentWindow ?? _videoWindow;
        var dialog = _documentDialog ?? _videoDialog;
        var show = native && IsEffectivelyVisible && this.IsAttachedToVisualTree()
            && window is not { IsModalInteractionBlocked: true } && window is not { IsUpgradeOpen: true }
            && window is not { WindowState: WindowState.Minimized } && dialog?.IsVisible != true;
        NativePreviewToolbarPopup.IsOpen = show && PreviewToolbar.IsVisible;
        UpdatePreviewStepPlacement(native, show);
    }

    private void OnPreviewCardSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (ZoomedImageScroll.IsVisible) SizeZoomedImage();
        UpdateToolbarLabels();
    }

    private void UpdateToolbarLabels()
    {
        var showLabels = PreviewCard.Bounds.Width >= 420;
        foreach (var action in _toolbarButtons)
            action.Label.IsVisible = showLabels;
    }

    private void ToggleImageZoom()
    {
        if (_previewBitmap == null) return;
        var zoomed = !ZoomedImageScroll.IsVisible;
        if (zoomed)
        {
            ZoomedImage.Source = _previewBitmap;
            SizeZoomedImage();
            ZoomedImageScroll.Offset = default;
        }
        ZoomedImageScroll.IsVisible = zoomed;
        PreviewImage.IsVisible = !zoomed;
        UpdatePreviewToolbar();
    }

    private void SizeZoomedImage()
    {
        if (_previewBitmap is not { } bitmap || PreviewHost.Bounds.Width <= 0 || PreviewHost.Bounds.Height <= 0) return;
        var scale = 2 * Math.Min(PreviewHost.Bounds.Width / bitmap.Size.Width, PreviewHost.Bounds.Height / bitmap.Size.Height);
        ZoomedImage.Width = bitmap.Size.Width * scale;
        ZoomedImage.Height = bitmap.Size.Height * scale;
    }
}
