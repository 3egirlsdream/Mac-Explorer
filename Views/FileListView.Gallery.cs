using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;

namespace MacExplorer.Views;

public partial class FileListView
{
    private FileGalleryView? _gallery;
    private HomeThumbnailCache? _galleryThumbnails;
    private SuperPreviewView? _galleryPreview;
    private FileSystemEntry? _galleryEntry;
    private int _galleryIndex;
    private string? _galleryPath;
    private CancellationTokenSource? _galleryRequest;
    private Visual[] _galleryAncestors = [];
    private bool _galleryRefreshQueued;
    private bool _syncingGallerySelection;
    private double _zoomWheelDelta;
    private long _lastZoomWheelInput;

    internal bool IsGalleryVisible => GalleryHost.IsVisible;
    internal ListBox? GalleryFilmstrip => _gallery?.GalleryStrip;
    private ListBox GalleryStrip => _gallery!.GalleryStrip;
    internal Task<bool> GalleryPreviewReady { get; private set; } = Task.FromResult(false);

    private void InitializeGallery()
    {
        AddHandler(PointerWheelChangedEvent, OnGalleryWheel, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) =>
        {
            _galleryAncestors = this.GetVisualAncestors().Prepend(this).ToArray();
            foreach (var ancestor in _galleryAncestors) ancestor.PropertyChanged += OnGalleryVisibilityChanged;
            QueueGalleryRefresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            foreach (var ancestor in _galleryAncestors) ancestor.PropertyChanged -= OnGalleryVisibilityChanged;
            _galleryAncestors = [];
            ReleaseGalleryContent();
        };
    }

    private void AttachGallery()
    {
        if (_gallery == null)
        {
            _gallery = new FileGalleryView();
            ConfigureGalleryStrip();
        }
        GalleryHost.Content = _gallery;
    }

    private void ConfigureGalleryStrip()
    {
        GalleryStrip.ItemTemplate = new FuncDataTemplate<FileSystemEntry>((entry, _) =>
        {
            if (entry == null) return null;
            var label = new TextBlock
            {
                Text = entry.IconDisplayName, TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            AppTypography.BindFontSize(label, AppTypography.Caption);
            var cell = new StackPanel
            {
                Width = 84, Spacing = 3, Children = { CreateGalleryImage(entry), label }
            };
            AutomationProperties.SetName(cell, entry.DisplayName);
            ToolTip.SetTip(cell, entry.DisplayName);
            return cell;
        });
        GalleryStrip.SelectionChanged += OnGallerySelectionChanged;
        GalleryStrip.DoubleTapped += (_, e) =>
        {
            if (GalleryItemFromSource(e.Source) is not { } entry) return;
            OpenEntryFromGesture(entry);
            e.Handled = true;
        };
        GalleryStrip.AddHandler(KeyDownEvent, (_, e) => TryHandleGalleryKey(e), RoutingStrategies.Tunnel);
    }

    private HomeFileImage CreateGalleryImage(FileSystemEntry entry)
    {
        var image = new HomeFileImage(entry, FileThumbnailSizing.StripSize,
            ViewModel!.GetListThumbnailAsync, _galleryThumbnails ??= new HomeThumbnailCache());
        image.Classes.Add("gallery-thumbnail");
        return image;
    }

    private void OnGalleryVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty || !IsGalleryVisible) return;
        if (IsEffectivelyVisible) QueueGalleryRefresh();
        else ReleaseGalleryContent();
    }

    private void ApplyThumbnailZoom()
    {
        var showGallery = FastList.IsVisible && ViewModel?.IsGalleryMode == true;
        var wasGallery = IsGalleryVisible;
        if (showGallery && !wasGallery)
        {
            CancelActiveRename();
            ResetDragState();
            var (first, end) = FastList.VisibleRange;
            _galleryIndex = first < end ? first : 0;
            _galleryPath = ViewModel!.CurrentPath;
            AttachGallery();
        }
        else if (!showGallery)
        {
            var level = ViewModel?.ThumbnailZoomLevel ?? FileThumbnailSizing.DefaultGridLevel;
            FastList.GridIconSize = FileThumbnailSizing.GetGridSize(level);
        }

        GalleryHost.IsVisible = showGallery;
        FastListHost.IsVisible = FastList.IsVisible && !showGallery;
        if (showGallery)
        {
            QueueGalleryRefresh();
            if (!wasGallery)
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsGalleryVisible && IsEffectivelyVisible) GalleryStrip.Focus();
                }, DispatcherPriority.Loaded);
        }
        else if (wasGallery)
        {
            var entry = _galleryEntry;
            ReleaseGalleryContent();
            GalleryHost.Content = null;
            _galleryEntry = null;
            _galleryPath = null;
            if (entry != null && FastList.IndexOf(entry) >= 0)
            {
                _fastFocusedPath = FastList.KeyboardFocusPath = entry.FullPath;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsGalleryVisible && FastList.IsEffectivelyVisible) FastList.ScrollToEntry(entry);
                }, DispatcherPriority.Loaded);
            }
            if (FastList.IsVisible) FastList.Focus();
        }
    }

    private void OnGalleryWheel(object? sender, PointerWheelEventArgs e)
    {
        var overStrip = IsGalleryVisible && IsWithinVisual(e.Source as Visual, GalleryStrip);
        if (overStrip && e.KeyModifiers == KeyModifiers.None && Math.Abs(e.Delta.Y) > Math.Abs(e.Delta.X)
            && GalleryStrip.FindDescendantOfType<ScrollViewer>() is { } scroll)
        {
            scroll.Offset = scroll.Offset.WithX(scroll.Offset.X - e.Delta.Y * 92);
            e.Handled = true;
        }
        if (ViewModel is not { ViewMode: ViewMode.Grid, IsBrowseOnly: false } vm
            || e.KeyModifiers != KeyModifiers.Control || Math.Abs(e.Delta.Y) <= Math.Abs(e.Delta.X)
            || !(IsGalleryVisible ? overStrip : IsWithinVisual(e.Source as Visual, FastListHost)))
        {
            _zoomWheelDelta = 0;
            return;
        }
        e.Handled = true;
        var now = Environment.TickCount64;
        if (now - _lastZoomWheelInput > 200 || Math.Sign(_zoomWheelDelta) != Math.Sign(e.Delta.Y))
            _zoomWheelDelta = 0;
        _lastZoomWheelInput = now;
        _zoomWheelDelta += e.Delta.Y;
        if (Math.Abs(_zoomWheelDelta) < 1) return;
        vm.SetThumbnailZoomLevel(vm.ThumbnailZoomLevel + Math.Sign(_zoomWheelDelta));
        _zoomWheelDelta = 0;
    }

    internal bool TryExitGallery()
    {
        if (!IsGalleryVisible || ViewModel == null) return false;
        ViewModel.SetThumbnailZoomLevel(FileThumbnailSizing.LargestGridLevel);
        return true;
    }

    private bool TryHandleGalleryKey(KeyEventArgs e)
    {
        if (e.Handled || !IsGalleryVisible || e.KeyModifiers != KeyModifiers.None) return false;
        if (e.Key == Key.Escape)
        {
            e.Handled = TryDismissContextMenu() || TryExitGallery();
            return e.Handled;
        }
        if (!IsWithinVisual(e.Source as Visual, GalleryStrip) || FastList.Rows.Count == 0) return false;
        if (e.Key == Key.Enter && _galleryEntry is { } entry)
            OpenEntryFromGesture(entry);
        else if (e.Key is Key.Left or Key.Right)
            SelectGalleryItem(Math.Clamp(_galleryIndex + (e.Key == Key.Left ? -1 : 1), 0, FastList.Rows.Count - 1));
        else
            return false;
        return e.Handled = true;
    }

    private void QueueGalleryRefresh()
    {
        if (!IsGalleryVisible || _galleryRefreshQueued) return;
        _galleryRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _galleryRefreshQueued = false;
            RefreshGallery();
        }, DispatcherPriority.Background);
    }

    private void RefreshGallery()
    {
        if (!IsGalleryVisible || !IsEffectivelyVisible || VisualRoot == null || ViewModel is not { } vm) return;
        if (_galleryPath != vm.CurrentPath)
        {
            _galleryPath = vm.CurrentPath;
            _galleryIndex = 0;
            _galleryEntry = null;
            ReleaseGalleryContent();
        }
        var rows = FastList.Rows;
        if (vm.IsDirectoryLoading || _fastRowsPath != vm.CurrentPath || rows.Count == 0)
        {
            ReleaseGalleryContent();
            return;
        }

        _syncingGallerySelection = true;
        try
        {
            if (!ReferenceEquals(GalleryStrip.ItemsSource, rows)) GalleryStrip.ItemsSource = rows;
        }
        finally { _syncingGallerySelection = false; }

        var selected = vm.SelectedEntries.LastOrDefault();
        var index = selected == null ? -1 : FastList.IndexOfPath(selected.FullPath);
        if (index < 0 && _galleryEntry != null) index = FastList.IndexOfPath(_galleryEntry.FullPath);
        SelectGalleryItem(index < 0 ? Math.Clamp(_galleryIndex, 0, rows.Count - 1) : index);
    }

    private FileSystemEntry? GalleryItemFromSource(object? source)
        => (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.Content as FileSystemEntry;

    private void OnGallerySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingGallerySelection || !IsGalleryVisible || ViewModel?.IsDirectoryLoading != false
            || GalleryStrip.SelectedItem is not FileSystemEntry entry) return;
        var index = FastList.IndexOfPath(entry.FullPath);
        if (index >= 0) SelectGalleryItem(index);
    }

    private void SelectGalleryItem(int index)
    {
        var entry = FastList.Rows[index];
        _galleryIndex = index;
        _syncingGallerySelection = true;
        try
        {
            GalleryStrip.SelectedIndex = index;
            GalleryStrip.ScrollIntoView(index);
        }
        finally { _syncingGallerySelection = false; }
        ViewModel!.SelectEntry(entry);
        if (ReferenceEquals(_galleryEntry, entry) && _galleryRequest != null) return;
        _galleryEntry = entry;
        GalleryPreviewReady = StartGalleryPreview(entry);
    }

    private async Task<bool> StartGalleryPreview(FileSystemEntry entry)
    {
        CancelGalleryPreview();
        var gallery = _gallery!;
        gallery.GalleryPlaceholderName.Text = entry.DisplayName;
        gallery.GalleryPlaceholderKind.Text = entry.KindText;
        gallery.GalleryPlaceholderImage.Content = CreateGalleryImage(entry);
        gallery.GalleryPlaceholder.IsVisible = true;

        var request = _galleryRequest = new CancellationTokenSource();
        if (entry.IsDirectory || !entry.IsReadable || entry.IsVirtual || !Path.IsPathFullyQualified(entry.FullPath))
            return false;

        var token = request.Token;
        try
        {
            // Let the filmstrip settle during fast keyboard navigation before opening the source.
            await Task.Delay(120, token);
            var preview = _galleryPreview ??= new SuperPreviewView(isCompactPreview: true, isGalleryPreview: true);
            gallery.GalleryPreviewHost.Content = preview;
            var ready = await preview.OpenGalleryFileAsync(entry, token);
            if (token.IsCancellationRequested) return false;
            if (!ready)
            {
                preview.Close();
                return false;
            }
            gallery.GalleryPlaceholder.IsVisible = false;
            gallery.GalleryPlaceholderImage.Content = null;
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
    }

    private void CancelGalleryPreview()
    {
        _galleryRequest?.Cancel();
        _galleryRequest?.Dispose();
        _galleryRequest = null;
        _galleryPreview?.Close();
    }

    private void ReleaseGalleryContent()
    {
        if (_gallery == null) return;
        CancelGalleryPreview();
        _gallery.GalleryPlaceholder.IsVisible = false;
        _gallery.GalleryPlaceholderImage.Content = null;
        GalleryStrip.ItemsSource = null;
        _gallery.GalleryPreviewHost.Content = null;
        _galleryThumbnails?.Dispose();
        _galleryThumbnails = null;
    }
}
