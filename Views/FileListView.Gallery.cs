using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Icons = MacExplorer.Assets.Icons;

namespace MacExplorer.Views;

public partial class FileListView
{
    private const double MinGalleryStripHeight = 84;
    private const double DefaultGalleryStripHeight = 108;
    private const double MaxGalleryStripHeight = 168;
    private const string GalleryStripHeightSettingKey = "gallery_strip_height";
    private const int MaxFolderScanForCount = 1000;

    private static readonly HashSet<string> TransparentImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".ico", ".svg", ".svgz", ".webp", ".gif", ".icns", ".tif", ".tiff"
    };

    private sealed class GalleryStripMetrics : INotifyPropertyChanged
    {
        private double _stripHeight = DefaultGalleryStripHeight;
        public event PropertyChangedEventHandler? PropertyChanged;

        public double StripHeight
        {
            get => _stripHeight;
            set
            {
                var clamped = Math.Round(Math.Clamp(value, MinGalleryStripHeight, MaxGalleryStripHeight));
                if (Math.Abs(_stripHeight - clamped) < 0.5) return;
                _stripHeight = clamped;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StripHeight)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FrameHeight)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FrameWidth)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CellWidth)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageSize)));
            }
        }

        public double FrameHeight => Math.Clamp(Math.Round(_stripHeight - 48), 36, 120);
        public double FrameWidth => Math.Round(FrameHeight * 1.28);
        public double CellWidth => FrameWidth + 8;
        public double ImageSize => Math.Max(26, FrameHeight - 12);
    }

    private FileGalleryView? _gallery;
    private HomeThumbnailCache? _galleryThumbnails;
    private SuperPreviewView? _galleryPreview;
    private FileSystemEntry? _galleryEntry;
    private int _galleryIndex;
    private int _galleryStepDirection;
    private string? _galleryPath;
    private CancellationTokenSource? _galleryRequest;
    private CancellationTokenSource? _galleryTagRequest;
    private Visual[] _galleryAncestors = [];
    private ScrollViewer? _galleryStripScroll;
    private readonly GalleryStripMetrics _galleryStripMetrics = new();
    private bool _galleryRefreshQueued;
    private bool _syncingGallerySelection;
    private bool? _galleryCheckerOverride;
    private bool _isDraggingGallerySplitter;
    private double _splitterDragStartY;
    private double _splitterStartHeight;
    private string? _galleryFolderSummaryText;
    private double _zoomWheelDelta;
    private long _lastZoomWheelInput;

    internal bool IsGalleryVisible => GalleryHost.IsVisible;
    internal bool KeepThumbnailZoomFocus { get; set; }
    internal ListBox? GalleryFilmstrip => _gallery?.GalleryStrip;
    internal FileGalleryView? GalleryView => _gallery;
    internal SuperPreviewView? GalleryPreview => _galleryPreview;
    private ListBox GalleryStrip => _gallery!.GalleryStrip;
    internal Task<bool> GalleryPreviewReady { get; private set; } = Task.FromResult(false);

    private void InitializeGallery()
    {
        AddHandler(PointerWheelChangedEvent, OnGalleryWheel, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) =>
        {
            _galleryAncestors = this.GetVisualAncestors().Prepend(this).ToArray();
            foreach (var ancestor in _galleryAncestors) ancestor.PropertyChanged += OnGalleryVisibilityChanged;
            if (ViewModel != null) ViewModel.PropertyChanged += OnGalleryViewModelPropertyChanged;
            QueueGalleryRefresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            foreach (var ancestor in _galleryAncestors) ancestor.PropertyChanged -= OnGalleryVisibilityChanged;
            _galleryAncestors = [];
            if (ViewModel != null) ViewModel.PropertyChanged -= OnGalleryViewModelPropertyChanged;
            ReleaseGalleryContent();
        };
    }

    private void OnGalleryViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileListViewModel.IsInfoPanelVisible) && _gallery != null && IsGalleryVisible)
        {
            UpdateGalleryStageMargins();
            SyncGalleryBarState();
        }
    }

    private void AttachGallery()
    {
        if (_gallery == null)
        {
            _gallery = new FileGalleryView();
            RestoreGalleryStripHeight();
            ConfigureGalleryView(_gallery);
        }
        UpdateGalleryStageMargins();
        GalleryHost.Content = _gallery;
    }

    private void UpdateGalleryStageMargins()
    {
        if (_gallery == null) return;
        // InfoPanelPane already reserves a 6px resize handle on its left; tighten the
        // stage's right margin by 6px when open so left and right breathing room match.
        var infoOpen = ViewModel?.IsInfoPanelVisible == true;
        _gallery.GalleryStage.Margin = new Thickness(10, 8, infoOpen ? 4 : 10, 0);
    }

    private void RestoreGalleryStripHeight()
    {
        var saved = App.Services?.GetService<ISettingsService>()?.Get(GalleryStripHeightSettingKey, DefaultGalleryStripHeight)
            ?? DefaultGalleryStripHeight;
        ApplyGalleryStripHeight(saved, persist: false);
    }

    private void ApplyGalleryStripHeight(double height, bool persist)
    {
        _galleryStripMetrics.StripHeight = height;
        if (_gallery != null)
            _gallery.GalleryStrip.Height = _galleryStripMetrics.StripHeight;
        if (persist)
            App.Services?.GetService<ISettingsService>()?.Set(GalleryStripHeightSettingKey, _galleryStripMetrics.StripHeight);
    }

    private void ConfigureGalleryView(FileGalleryView gallery)
    {
        gallery.GalleryStrip.Height = _galleryStripMetrics.StripHeight;
        ConfigureGalleryStrip(gallery);
        ConfigureGalleryStageControls(gallery);
        ConfigureGallerySplitter(gallery);
    }

    private void ConfigureGalleryStrip(FileGalleryView gallery)
    {
        var strip = gallery.GalleryStrip;
        strip.IsTextSearchEnabled = false;
        BindTypeAhead(strip);
        strip.ItemTemplate = new FuncDataTemplate<FileSystemEntry>((entry, _) =>
        {
            if (entry == null) return null;
            var image = CreateGalleryImage(entry, _galleryStripMetrics.ImageSize);
            image.Bind(Layoutable.WidthProperty, new Binding(nameof(GalleryStripMetrics.ImageSize)) { Source = _galleryStripMetrics });
            image.Bind(Layoutable.HeightProperty, new Binding(nameof(GalleryStripMetrics.ImageSize)) { Source = _galleryStripMetrics });
            image.HorizontalAlignment = HorizontalAlignment.Center;
            image.VerticalAlignment = VerticalAlignment.Center;

            var frameGrid = new Grid();
            frameGrid.Children.Add(image);
            if (CreateGalleryMediaBadge(entry) is { } badge)
                frameGrid.Children.Add(badge);

            var frame = new Border { Classes = { "gallery-thumb-frame" }, Child = frameGrid };
            frame.Bind(Layoutable.WidthProperty, new Binding(nameof(GalleryStripMetrics.FrameWidth)) { Source = _galleryStripMetrics });
            frame.Bind(Layoutable.HeightProperty, new Binding(nameof(GalleryStripMetrics.FrameHeight)) { Source = _galleryStripMetrics });

            var label = new FileNameText
            {
                Text = entry.DisplayName,
                TextAlignment = TextAlignment.Center,
                Classes = { "gallery-item-name" }
            };
            AppTypography.BindFontSize(label, AppTypography.Caption);
            var labelPill = new Border { Classes = { "gallery-label-pill" }, Child = label };

            var cell = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { frame, labelPill }
            };
            cell.Bind(Layoutable.WidthProperty, new Binding(nameof(GalleryStripMetrics.CellWidth)) { Source = _galleryStripMetrics });
            AutomationProperties.SetName(cell, entry.DisplayName);
            ToolTip.SetTip(cell, entry.DisplayName);
            return cell;
        });

        strip.SelectionChanged += OnGallerySelectionChanged;
        strip.DoubleTapped += (_, e) =>
        {
            if (GalleryItemFromSource(e.Source) is not { } entry) return;
            OpenEntryFromGesture(entry);
            e.Handled = true;
        };
        strip.TemplateApplied += (_, _) =>
        {
            Dispatcher.UIThread.Post(() => AttachGalleryStripScroll(gallery), DispatcherPriority.Loaded);
        };
        strip.AddHandler(KeyDownEvent, (_, e) => TryHandleGalleryKey(e), RoutingStrategies.Tunnel);
    }

    private void ConfigureGalleryStageControls(FileGalleryView gallery)
    {
        gallery.GalleryStage.SizeChanged += (_, _) =>
            gallery.GalleryActionBar.UpdateAvailableWidth(gallery.GalleryStage.Bounds.Width);
        gallery.GalleryPreviousButton.Click += (_, _) => StepGallery(-1);
        gallery.GalleryNextButton.Click += (_, _) => StepGallery(1);
        gallery.GalleryFolderOpenButton.Click += (_, _) =>
        {
            if (_galleryEntry is { } entry) OpenEntryFromGesture(entry, doubleClick: false);
        };
        gallery.GalleryFolderCard.DoubleTapped += (_, e) =>
        {
            if (_galleryEntry is { } entry)
            {
                OpenEntryFromGesture(entry, doubleClick: false);
                e.Handled = true;
            }
        };
        gallery.GalleryPlaceholder.DoubleTapped += (_, e) =>
        {
            if (_galleryEntry is { } entry)
            {
                OpenEntryFromGesture(entry, doubleClick: false);
                e.Handled = true;
            }
        };

        var bar = gallery.GalleryActionBar;
        bar.PlayVideoClicked += () =>
        {
            _galleryPreview?.StartGalleryVideoPlayback();
            SyncGalleryBarState();
        };
        bar.ZoomClicked += () =>
        {
            _galleryPreview?.ToggleGalleryZoom();
            SyncGalleryBarState();
        };
        bar.RotateClicked += () =>
        {
            _galleryPreview?.RotateGalleryImage(90);
            SyncGalleryBarState();
        };
        bar.CheckerboardToggled += enabled =>
        {
            if (_galleryPreview == null) return;
            _galleryCheckerOverride = enabled;
            _galleryPreview.GalleryShowCheckerboard = enabled;
            SyncGalleryBarState();
        };
        bar.CopyImageClicked += () =>
        {
            if (_galleryPreview != null) _ = _galleryPreview.CopyCurrentImageAsync();
        };
        bar.CopyTextClicked += () =>
        {
            if (_galleryPreview != null) _ = _galleryPreview.CopyLoadedTextAsync();
        };
        bar.FirstPageClicked += () => _galleryPreview?.NavigateDocumentFirstPage();
        bar.ExtractClicked += () =>
        {
            if (_galleryEntry is { } entry) ViewModel?.ExtractToNamedFolder(entry);
        };
        bar.TagClicked += ToggleGalleryTagPopup;
        bar.TagPopupClosed += () =>
        {
            if (_galleryEntry is { } entry && IsGalleryVisible)
                _ = LoadGalleryTagDotsAsync(entry);
        };
        bar.OpenClicked += () =>
        {
            if (_galleryEntry is { } entry) OpenEntryFromGesture(entry, doubleClick: false);
        };
        bar.QuickLookClicked += OpenGalleryQuickLook;
        bar.InfoClicked += () =>
        {
            ViewModel?.ToggleInfoPanel();
            UpdateGalleryStageMargins();
            SyncGalleryBarState();
        };
        bar.DeleteClicked += () =>
        {
            if (_galleryEntry != null) ViewModel?.ShowDeleteConfirmDialog();
        };
        bar.RevealClicked += () =>
        {
            if (_galleryEntry is { } entry && Path.IsPathFullyQualified(entry.FullPath)
                && App.Services?.GetService<IApplicationLauncherService>() is { } launcher)
                _ = launcher.RevealInFinderAsync(entry.FullPath);
        };
        bar.CopyPathClicked += () =>
        {
            if (_galleryEntry is { } entry && App.Services?.GetService<IClipboardService>() is { } clipboard)
                _ = clipboard.CopyTextAsync(entry.FullPath);
        };
    }

    private void ConfigureGallerySplitter(FileGalleryView gallery)
    {
        var splitter = gallery.GalleryStripSplitter;
        splitter.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(splitter).Properties.IsLeftButtonPressed) return;
            _isDraggingGallerySplitter = true;
            _splitterDragStartY = e.GetPosition(gallery).Y;
            _splitterStartHeight = _galleryStripMetrics.StripHeight;
            e.Pointer.Capture(splitter);
            e.Handled = true;
        };
        splitter.PointerMoved += (_, e) =>
        {
            if (!_isDraggingGallerySplitter) return;
            var deltaY = e.GetPosition(gallery).Y - _splitterDragStartY;
            ApplyGalleryStripHeight(_splitterStartHeight - deltaY, persist: false);
            e.Handled = true;
        };
        splitter.PointerReleased += (_, e) =>
        {
            if (!_isDraggingGallerySplitter) return;
            _isDraggingGallerySplitter = false;
            if (Equals(e.Pointer.Captured, splitter)) e.Pointer.Capture(null);
            ApplyGalleryStripHeight(_galleryStripMetrics.StripHeight, persist: true);
            UpdateGalleryEdgeFades();
            e.Handled = true;
        };
        splitter.PointerCaptureLost += (_, _) =>
        {
            if (!_isDraggingGallerySplitter) return;
            _isDraggingGallerySplitter = false;
            ApplyGalleryStripHeight(_galleryStripMetrics.StripHeight, persist: true);
        };
        splitter.DoubleTapped += (_, e) =>
        {
            ApplyGalleryStripHeight(DefaultGalleryStripHeight, persist: true);
            UpdateGalleryEdgeFades();
            e.Handled = true;
        };
    }

    private void AttachGalleryStripScroll(FileGalleryView gallery)
    {
        var scroll = gallery.GalleryStrip.FindDescendantOfType<ScrollViewer>();
        if (ReferenceEquals(_galleryStripScroll, scroll)) return;
        if (_galleryStripScroll != null) _galleryStripScroll.ScrollChanged -= OnGalleryStripScrollChanged;
        _galleryStripScroll = scroll;
        if (_galleryStripScroll != null)
        {
            _galleryStripScroll.ScrollChanged += OnGalleryStripScrollChanged;
            UpdateGalleryEdgeFades();
        }
    }

    private void OnGalleryStripScrollChanged(object? sender, ScrollChangedEventArgs e) => UpdateGalleryEdgeFades();

    private void UpdateGalleryEdgeFades()
    {
        if (_gallery == null) return;
        if (_galleryStripScroll == null) AttachGalleryStripScroll(_gallery);
        if (_galleryStripScroll is not { } scroll)
        {
            _gallery.GalleryEdgeFadeLeft.IsVisible = false;
            _gallery.GalleryEdgeFadeRight.IsVisible = false;
            return;
        }
        var maxOffset = Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width);
        _gallery.GalleryEdgeFadeLeft.IsVisible = maxOffset > 2 && scroll.Offset.X > 2;
        _gallery.GalleryEdgeFadeRight.IsVisible = maxOffset > 2 && scroll.Offset.X < maxOffset - 2;
    }

    private static Control? CreateGalleryMediaBadge(FileSystemEntry entry)
    {
        if (entry.IsDirectory) return null;
        var key = FileIconResolver.ResolveIconKey(Path.GetExtension(entry.Name), entry.Name);
        if (key != "file-video") return null;
        return new Border
        {
            Classes = { "gallery-media-badge" },
            Child = new PathIcon
            {
                Data = Geometry.Parse(Icons.Play),
                Width = 8,
                Height = 8,
                Margin = new Thickness(1, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.White
            }
        };
    }

    private HomeFileImage CreateGalleryImage(FileSystemEntry entry, double size = FileThumbnailSizing.StripSize)
    {
        var image = new HomeFileImage(entry, size,
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
        if (wasGallery != showGallery) _typeAhead.Reset();
        if (showGallery && !wasGallery)
        {
            CancelActiveRename();
            ResetDragState();
            var (first, end) = FastList.VisibleRange;
            _galleryIndex = first < end ? first : 0;
            _galleryPath = ViewModel!.CurrentPath;
            _galleryCheckerOverride = null;
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
            {
                ViewMotion.AnimateEnter(GalleryHost, fromOpacity: 0, durationMs: 200);
                if (_gallery != null)
                {
                    ViewMotion.AnimateEnter(_gallery.GalleryPreviewHost, fromScale: 0.96, fromOpacity: 0, durationMs: 220);
                    ViewMotion.AnimateEnter(_gallery.GalleryPlaceholder, fromScale: 0.96, fromOpacity: 0, durationMs: 220);
                    ViewMotion.AnimateEnter(_gallery.GalleryStrip, fromY: 24, fromOpacity: 0, durationMs: 220);
                }
            }
            if (!wasGallery && !KeepThumbnailZoomFocus)
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsGalleryVisible && IsEffectivelyVisible && !KeepThumbnailZoomFocus) GalleryStrip.Focus();
                }, DispatcherPriority.Loaded);
        }
        else if (wasGallery)
        {
            var entry = _galleryEntry;
            ReleaseGalleryContent();
            GalleryHost.Content = null;
            _galleryEntry = null;
            _galleryPath = null;
            if (FastListHost.IsVisible)
                ViewMotion.AnimateEnter(FastListHost, fromScale: 1.02, fromOpacity: 0.2, durationMs: 180);
            if (entry != null && FastList.IndexOf(entry) >= 0)
            {
                _fastFocusedPath = FastList.KeyboardFocusPath = entry.FullPath;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsGalleryVisible && FastList.IsEffectivelyVisible) FastList.ScrollToEntry(entry);
                }, DispatcherPriority.Loaded);
            }
            if (FastList.IsVisible && !KeepThumbnailZoomFocus) FastList.Focus();
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
        if (e.Handled || !IsGalleryVisible) return false;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            if (_gallery?.GalleryActionBar.IsAnyPopupOpen == true)
            {
                _gallery.GalleryActionBar.CloseTagPopup();
                e.Handled = true;
                return true;
            }
            if (_galleryPreview?.IsGalleryImageZoomed == true)
            {
                _galleryPreview.ResetGalleryZoom();
                SyncGalleryBarState();
                e.Handled = true;
                return true;
            }
            e.Handled = TryDismissContextMenu() || TryExitGallery();
            return e.Handled;
        }

        if (IsTextInputSource(e.Source) || FastList.Rows.Count == 0) return false;

        if (e.KeyModifiers == KeyModifiers.Shift && e.Key == Key.R && _galleryPreview?.HasGalleryImage == true)
        {
            _galleryPreview.RotateGalleryImage(-90);
            SyncGalleryBarState();
            return e.Handled = true;
        }

        if (e.KeyModifiers != KeyModifiers.None) return false;

        switch (e.Key)
        {
            case Key.Enter when IsWithinVisual(e.Source as Visual, GalleryStrip) && _galleryEntry is { } entry:
                OpenEntryFromGesture(entry, doubleClick: false);
                return e.Handled = true;
            case Key.Left:
                StepGallery(-1);
                return e.Handled = true;
            case Key.Right:
                StepGallery(1);
                return e.Handled = true;
            case Key.Home:
                SelectGalleryItem(0);
                return e.Handled = true;
            case Key.End:
                SelectGalleryItem(FastList.Rows.Count - 1);
                return e.Handled = true;
            case Key.R when _galleryPreview?.HasGalleryImage == true:
                _galleryPreview.RotateGalleryImage(90);
                SyncGalleryBarState();
                return e.Handled = true;
            case Key.Z when _galleryPreview?.HasGalleryImage == true:
                _galleryPreview.ToggleGalleryZoom();
                SyncGalleryBarState();
                return e.Handled = true;
            case Key.C when _galleryPreview?.HasGalleryImage == true:
                var next = !_galleryPreview.GalleryShowCheckerboard;
                _galleryCheckerOverride = next;
                _galleryPreview.GalleryShowCheckerboard = next;
                SyncGalleryBarState();
                return e.Handled = true;
            case Key.T when _gallery?.GalleryActionBar.TagButton.IsVisible == true:
                ToggleGalleryTagPopup();
                return e.Handled = true;
            case Key.I:
                ViewModel?.ToggleInfoPanel();
                UpdateGalleryStageMargins();
                SyncGalleryBarState();
                return e.Handled = true;
            default:
                return false;
        }
    }

    private void StepGallery(int delta)
    {
        if (!IsGalleryVisible || FastList.Rows.Count == 0) return;
        var next = Math.Clamp(_galleryIndex + delta, 0, FastList.Rows.Count - 1);
        if (next != _galleryIndex) SelectGalleryItem(next);
    }

    private void ToggleGalleryTagPopup()
    {
        if (_gallery == null || _galleryEntry is not { } entry || ViewModel?.FileTagService == null || entry.IsVirtual) return;
        _gallery.GalleryActionBar.ToggleTagPopup(ViewModel, entry.FullPath);
    }

    private void OpenGalleryQuickLook()
    {
        if (_galleryEntry is not { } entry || entry.IsVirtual) return;
        if (TopLevel.GetTopLevel(this) is MainWindow mainWindow)
            _ = mainWindow.OpenSuperPreviewForEntryAsync(entry);
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
        var rows = FastList.Rows;
        if (rows.Count == 0) return;
        index = Math.Clamp(index, 0, rows.Count - 1);
        var entry = rows[index];
        var previousIndex = _galleryIndex;
        var hadEntry = _galleryEntry != null;
        _galleryStepDirection = hadEntry ? Math.Sign(index - previousIndex) : 0;
        _galleryIndex = index;
        _syncingGallerySelection = true;
        try
        {
            GalleryStrip.SelectedIndex = index;
            GalleryStrip.ScrollIntoView(index);
        }
        finally { _syncingGallerySelection = false; }

        CenterGalleryFilmstripItem(index);
        ViewModel!.SelectEntry(entry);
        UpdateGalleryStepButtons();
        if (ReferenceEquals(_galleryEntry, entry) && _galleryRequest != null)
        {
            SyncGalleryBarState();
            return;
        }

        _galleryEntry = entry;
        _galleryFolderSummaryText = null;
        _gallery?.GalleryActionBar.CloseTagPopup();
        SyncGalleryBarState();
        _ = LoadGalleryTagDotsAsync(entry);
        GalleryPreviewReady = StartGalleryPreview(entry);
    }

    private void CenterGalleryFilmstripItem(int index)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsGalleryVisible || _gallery == null) return;
            if (_galleryStripScroll == null) AttachGalleryStripScroll(_gallery);
            if (_galleryStripScroll is not { } scroll || scroll.Viewport.Width <= 0) return;
            var maxOffset = Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width);
            if (maxOffset <= 1)
            {
                UpdateGalleryEdgeFades();
                return;
            }
            var stride = _galleryStripMetrics.CellWidth + 10;
            var targetX = Math.Clamp(12 + index * stride - (scroll.Viewport.Width - stride) / 2, 0, maxOffset);
            scroll.Offset = scroll.Offset.WithX(targetX);
            UpdateGalleryEdgeFades();
        }, DispatcherPriority.Loaded);
    }

    private void UpdateGalleryStepButtons()
    {
        if (_gallery == null) return;
        var count = FastList.Rows.Count;
        var hasNative = _galleryPreview?.HasNativePreview == true && _gallery.GalleryPreviewHost.IsVisible;
        _gallery.GalleryPreviousSlot.IsVisible = !hasNative && count > 1 && _galleryIndex > 0;
        _gallery.GalleryNextSlot.IsVisible = !hasNative && count > 1 && _galleryIndex >= 0 && _galleryIndex < count - 1;
    }

    private void SyncGalleryBarState()
    {
        if (_gallery == null) return;
        var entry = _galleryEntry;
        var count = FastList.Rows.Count;
        if (entry == null || count == 0)
        {
            _gallery.GalleryActionBar.IsVisible = false;
            return;
        }

        var ext = Path.GetExtension(entry.Name);
        var icon = FileIconResolver.ResolveIconKey(ext, entry.Name);
        var isImage = _galleryPreview?.HasGalleryImage == true && icon != "file-video";
        var isPdf = !entry.IsDirectory && ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        var isArchive = !entry.IsDirectory && icon == "file-archive";
        var isLocal = !entry.IsVirtual && Path.IsPathFullyQualified(entry.FullPath);
        var clipboardAvailable = App.Services?.GetService<IClipboardService>() != null;
        var launcherAvailable = App.Services?.GetService<IApplicationLauncherService>() != null;
        var infoOpen = ViewModel?.IsInfoPanelVisible == true;

        _gallery.GalleryActionBar.ApplyState(new PreviewActionBarState
        {
            CounterText = $"{_galleryIndex + 1} / {count}",
            FileName = entry.DisplayName,
            MetaText = BuildGalleryBarMeta(entry, isImage),
            CompactIdentity = infoOpen,
            ShowPlayVideo = _galleryPreview?.CanPlayGalleryVideo == true,
            ShowZoom = isImage,
            IsZoomed = isImage && _galleryPreview!.IsGalleryImageZoomed,
            ShowRotate = isImage,
            ShowCheckerboard = isImage,
            IsCheckerboardChecked = isImage && _galleryPreview!.GalleryShowCheckerboard,
            ShowCopyImage = isImage && clipboardAvailable,
            ShowCopyText = _galleryPreview?.CanCopyPreviewText == true && clipboardAvailable,
            ShowFirstPage = isPdf && _galleryPreview?.CanJumpToFirstPage == true,
            ShowExtract = isArchive && isLocal && ViewModel != null,
            ShowTag = ViewModel?.FileTagService != null && !entry.IsVirtual,
            ShowOpen = !entry.IsVirtual,
            OpenUsesPlayIcon = icon is "file-audio",
            ShowQuickLook = !entry.IsVirtual && !entry.IsDirectory,
            ShowInfoToggle = true,
            IsInfoVisible = infoOpen,
            ShowDelete = !entry.IsVirtual && entry.IsWritable,
            ShowReveal = launcherAvailable && isLocal,
            ShowCopyPath = clipboardAvailable && !entry.IsVirtual
        });
        _gallery.GalleryActionBar.UpdateAvailableWidth(_gallery.GalleryStage.Bounds.Width);
        _gallery.GalleryActionBar.IsVisible = _gallery.GalleryActionBar.HasVisibleContent;
        UpdateGalleryStepButtons();
    }

    private string BuildGalleryBarMeta(FileSystemEntry entry, bool hasImage)
    {
        if (hasImage && _galleryPreview?.GalleryImagePixelSize is { } px)
        {
            var rot = _galleryPreview.GalleryImageRotation;
            var parts = new List<string>(3) { $"{px.Width} × {px.Height}" };
            if (!string.IsNullOrWhiteSpace(entry.FormattedSize)) parts.Add(entry.FormattedSize);
            if (rot != 0) parts.Add($"{rot}°");
            return string.Join(" · ", parts);
        }
        if (entry.IsDirectory && !string.IsNullOrWhiteSpace(_galleryFolderSummaryText))
            return _galleryFolderSummaryText;
        if (!string.IsNullOrWhiteSpace(entry.FormattedSize) && !entry.IsDirectory)
            return $"{entry.KindText} · {entry.FormattedSize}";
        return entry.KindText;
    }

    private async Task LoadGalleryTagDotsAsync(FileSystemEntry entry)
    {
        CancelGalleryTagRequest();
        if (_gallery == null) return;
        _gallery.GalleryActionBar.SetTags(null);

        if (entry.IsVirtual || ViewModel?.FileTagService is not { } tagService) return;
        var cts = _galleryTagRequest = new CancellationTokenSource();
        var token = cts.Token;
        try
        {
            var tags = await tagService.GetFileTagsAsync(entry.FullPath, token);
            if (token.IsCancellationRequested || !IsGalleryVisible || _gallery == null || !ReferenceEquals(_galleryEntry, entry))
                return;
            _gallery.GalleryActionBar.SetTags(tags);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { }
    }

    private void CancelGalleryTagRequest()
    {
        _galleryTagRequest?.Cancel();
        _galleryTagRequest?.Dispose();
        _galleryTagRequest = null;
    }

    private async Task<bool> StartGalleryPreview(FileSystemEntry entry)
    {
        CancelGalleryPreview();
        var gallery = _gallery!;
        var step = _galleryStepDirection;
        gallery.GalleryFolderCard.IsVisible = false;
        gallery.GalleryFolderCollageGrid.Children.Clear();
        gallery.GalleryFolderIconHost.Content = null;

        if (entry.IsDirectory && !entry.IsApplication && !entry.IsVirtual && Path.IsPathFullyQualified(entry.FullPath))
        {
            gallery.GalleryPreviewHost.IsVisible = false;
            gallery.GalleryPlaceholder.IsVisible = false;
            gallery.GalleryPlaceholderImage.Content = null;
            var folderRequest = _galleryRequest = new CancellationTokenSource();
            await LoadGalleryFolderCardAsync(entry, folderRequest.Token);
            if (step != 0 && gallery.GalleryFolderCard.IsVisible)
                ViewMotion.AnimateEnter(gallery.GalleryFolderCard, fromX: step * 14, fromOpacity: 0.25, durationMs: ViewMotion.FastDurationMs);
            return false;
        }

        gallery.GalleryPreviewHost.IsVisible = true;
        gallery.GalleryPlaceholderName.Text = entry.DisplayName;
        gallery.GalleryPlaceholderKind.Text = string.IsNullOrWhiteSpace(entry.FormattedSize) || entry.IsDirectory
            ? entry.KindText
            : $"{entry.KindText} · {entry.FormattedSize}";
        gallery.GalleryPlaceholderImage.Content = CreateGalleryImage(entry, 96);
        gallery.GalleryPlaceholder.IsVisible = true;
        if (step != 0)
            ViewMotion.AnimateEnter(gallery.GalleryPlaceholder, fromX: step * 14, fromOpacity: 0.25, durationMs: ViewMotion.FastDurationMs);

        var request = _galleryRequest = new CancellationTokenSource();
        if (entry.IsDirectory || !entry.IsReadable || entry.IsVirtual || !Path.IsPathFullyQualified(entry.FullPath))
            return false;

        var token = request.Token;
        try
        {
            // Let the filmstrip settle during fast keyboard navigation before opening the source.
            await Task.Delay(90, token);
            if (_galleryPreview == null)
            {
                _galleryPreview = new SuperPreviewView(isCompactPreview: true, isGalleryPreview: true);
                _galleryPreview.GalleryImageStateChanged += () =>
                {
                    if (IsGalleryVisible) SyncGalleryBarState();
                };
            }
            var ext = Path.GetExtension(entry.Name);
            _galleryPreview.GalleryShowCheckerboard = _galleryCheckerOverride ?? TransparentImageExtensions.Contains(ext);
            gallery.GalleryPreviewHost.Content = _galleryPreview;
            var ready = await _galleryPreview.OpenGalleryFileAsync(entry, token);
            if (token.IsCancellationRequested) return false;
            if (!ready)
            {
                _galleryPreview.Close();
                SyncGalleryBarState();
                return false;
            }
            gallery.GalleryPlaceholder.IsVisible = false;
            gallery.GalleryPlaceholderImage.Content = null;
            SyncGalleryBarState();
            if (!_galleryPreview.HasNativePreview)
                ViewMotion.AnimateEnter(gallery.GalleryPreviewHost, fromX: step * 16, fromOpacity: 0.2, durationMs: 170);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
    }

    private async Task LoadGalleryFolderCardAsync(FileSystemEntry entry, CancellationToken token)
    {
        if (_gallery == null) return;
        _gallery.GalleryFolderName.Text = entry.DisplayName;
        _gallery.GalleryFolderMeta.Text = string.IsNullOrWhiteSpace(entry.ModifiedText)
            ? entry.KindText
            : $"{entry.KindText} · {entry.ModifiedText}";
        _gallery.GalleryFolderEmptyIcon.IsVisible = true;
        _gallery.GalleryFolderIconHost.Content = CreateGalleryImage(entry, 96);
        _gallery.GalleryFolderCard.IsVisible = true;

        var showHidden = ViewModel?.HideDotFiles == false;
        (int Count, bool CountTruncated, FileSystemEntry[] Samples) scan;
        try
        {
            scan = await Task.Run(() => ScanFolderCollageSamples(entry.FullPath, showHidden, token), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch { return; }

        if (token.IsCancellationRequested || !IsGalleryVisible || _gallery == null || !ReferenceEquals(_galleryEntry, entry))
            return;

        if (scan.Samples.Length > 0)
        {
            _gallery.GalleryFolderEmptyIcon.IsVisible = false;
            _gallery.GalleryFolderIconHost.Content = null;
            _gallery.GalleryFolderCollageGrid.Children.Clear();
            foreach (var sample in scan.Samples)
            {
                var tileImage = CreateGalleryImage(sample, 56);
                tileImage.HorizontalAlignment = HorizontalAlignment.Center;
                tileImage.VerticalAlignment = VerticalAlignment.Center;
                _gallery.GalleryFolderCollageGrid.Children.Add(new Border
                {
                    Classes = { "gallery-collage-tile" },
                    Margin = new Thickness(3),
                    Child = tileImage
                });
            }
        }

        var countText = scan.Count == 0
            ? LocalizationText.Get("file_gallery.empty_folder")
            : scan.CountTruncated
                ? LocalizationText.Get("file_gallery.folder_items_more", scan.Count)
                : LocalizationText.Get("file_gallery.folder_items", scan.Count);

        _galleryFolderSummaryText = countText;
        _gallery.GalleryFolderMeta.Text = string.IsNullOrWhiteSpace(entry.ModifiedText)
            ? countText
            : $"{countText} · {entry.ModifiedText}";
        SyncGalleryBarState();

        if (scan.Count == 0) return;
        try
        {
            var sizeResult = await DirectorySizeCalculator.CalculateAsync(entry.FullPath, token);
            if (token.IsCancellationRequested || !IsGalleryVisible || _gallery == null || !ReferenceEquals(_galleryEntry, entry))
                return;
            var formattedSize = FileSystemEntry.FormatSize(sizeResult.Bytes, isDirectory: false);
            var sizeLabel = sizeResult.IsComplete ? formattedSize : $"≥ {formattedSize}";
            _galleryFolderSummaryText = $"{countText} · {sizeLabel}";
            _gallery.GalleryFolderMeta.Text = string.IsNullOrWhiteSpace(entry.ModifiedText)
                ? _galleryFolderSummaryText
                : $"{_galleryFolderSummaryText} · {entry.ModifiedText}";
            SyncGalleryBarState();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { }
    }

    private static (int Count, bool CountTruncated, FileSystemEntry[] Samples) ScanFolderCollageSamples(
        string folderPath, bool showHidden, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        DirectoryAccess.Current.EnsureAccess(folderPath);
        if (!Directory.Exists(folderPath)) return (0, false, []);

        var count = 0;
        var truncated = false;
        var visualSamples = new List<FileSystemEntry>(4);
        var otherSamples = new List<FileSystemEntry>(4);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };

        foreach (var info in new DirectoryInfo(folderPath).EnumerateFileSystemInfos("*", options))
        {
            token.ThrowIfCancellationRequested();
            if (!showHidden && info.Name.StartsWith('.')) continue;
            count++;
            if (count > MaxFolderScanForCount)
            {
                count = MaxFolderScanForCount;
                truncated = true;
                break;
            }
            if (visualSamples.Count >= 4) continue;

            var isDir = (info.Attributes & FileAttributes.Directory) != 0;
            var ext = isDir ? string.Empty : info.Extension;
            var iconKey = isDir ? "folder" : FileIconResolver.ResolveIconKey(ext, info.Name);
            var isVisual = !isDir && iconKey is "file-image" or "file-video" or "file-pdf";
            if (!isVisual && otherSamples.Count >= 4) continue;

            var sample = new FileSystemEntry
            {
                FullPath = info.FullName,
                Name = info.Name,
                IsDirectory = isDir,
                Extension = ext,
                IsReadable = true,
                IconKey = iconKey
            };
            if (isVisual) visualSamples.Add(sample);
            else otherSamples.Add(sample);
        }

        var combined = new List<FileSystemEntry>(4);
        combined.AddRange(visualSamples);
        for (var i = 0; i < otherSamples.Count && combined.Count < 4; i++)
            combined.Add(otherSamples[i]);
        return (count, truncated, combined.ToArray());
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
        CancelGalleryTagRequest();
        _gallery.GalleryActionBar.CloseTagPopup();
        _gallery.GalleryActionBar.SetTags(null);
        _gallery.GalleryActionBar.IsVisible = false;
        _gallery.GalleryPreviousSlot.IsVisible = false;
        _gallery.GalleryNextSlot.IsVisible = false;
        _gallery.GalleryFolderCard.IsVisible = false;
        _gallery.GalleryFolderCollageGrid.Children.Clear();
        _gallery.GalleryFolderIconHost.Content = null;
        _gallery.GalleryPlaceholder.IsVisible = false;
        _gallery.GalleryPlaceholderImage.Content = null;
        _gallery.GalleryEdgeFadeLeft.IsVisible = false;
        _gallery.GalleryEdgeFadeRight.IsVisible = false;
        GalleryStrip.ItemsSource = null;
        _gallery.GalleryPreviewHost.Content = null;
        _galleryThumbnails?.Dispose();
        _galleryThumbnails = null;
    }
}
