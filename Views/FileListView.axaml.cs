using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace MacExplorer.Views;

public sealed class FileListPresentationRow
{
    public string GroupName { get; init; } = string.Empty;
    public int GroupItemCount { get; init; }
    public FileSystemEntry? Entry { get; init; }
    public bool IsGroupHeader => Entry == null;
    public bool HasEntries => Entry != null;
    public IReadOnlyList<FileSystemEntry> Entries => Entry == null ? [] : [Entry];
}

public sealed class FileGridPresentationRow
{
    public string GroupName { get; init; } = string.Empty;
    public int GroupItemCount { get; init; }
    public IReadOnlyList<FileSystemEntry> Entries { get; init; } = [];
    public bool IsGroupHeader => Entries.Count == 0;
    public bool HasEntries => Entries.Count > 0;
}

public partial class FileListView : UserControl
{
    private static readonly ByteLruCache MenuIconCache = new(8L * 1024 * 1024);
    private static readonly BitmapLruCache EntryImageCache = new(96L * 1024 * 1024);

    // Icon bindings re-evaluate on any entry property change, so converters must never
    // touch the disk; they only serve cache hits populated by the async loader below.
    internal static Bitmap? TryGetCachedEntryImage(string source)
        => EntryImageCache.TryGet(source, out var bitmap) ? bitmap : null;
    private static readonly SemaphoreSlim EntryImageLoadGate = new(4);
    private readonly ObservableCollection<FileListPresentationRow> _groupedListRows = [];
    private readonly ObservableCollection<FileGridPresentationRow> _gridRows = [];
    private readonly Dictionary<string, FileListPresentationRow> _groupedRowByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileGridPresentationRow> _gridRowByPath = new(StringComparer.Ordinal);
    private int _gridColumnCount;
    private FileListColumnWidths? _lastAppliedColumnWidths;
    private bool _anyCutApplied;

    private sealed class EntryImageLoadState
    {
        public required string Source { get; set; }
        public required CancellationTokenSource Cancellation { get; init; }
    }

    // Carries the pending thumbnail subscription for an icon so recycled virtualization
    // containers can detach it deterministically instead of accumulating lambdas.
    private sealed class EntryThumbnailAwaitState
    {
        public required FileSystemEntry Entry { get; init; }
        public required PropertyChangedEventHandler Handler { get; init; }
    }

    private static readonly AttachedProperty<EntryThumbnailAwaitState?> ThumbnailAwaitProperty =
        AvaloniaProperty.RegisterAttached<FileListView, Image, EntryThumbnailAwaitState?>("ThumbnailAwait");

    private sealed class BitmapLruCache
    {
        private readonly long _maxBytes;
        private readonly object _sync = new();
        private readonly Dictionary<string, LinkedListNode<(string Key, Bitmap Bitmap, long Bytes)>> _entries = new(StringComparer.Ordinal);
        private readonly LinkedList<(string Key, Bitmap Bitmap, long Bytes)> _lru = new();
        private long _bytes;

        public BitmapLruCache(long maxBytes) => _maxBytes = maxBytes;

        public bool TryGet(string key, out Bitmap? bitmap)
        {
            lock (_sync)
            {
                if (!_entries.TryGetValue(key, out var node))
                {
                    bitmap = null;
                    return false;
                }
                _lru.Remove(node);
                _lru.AddFirst(node);
                bitmap = node.Value.Bitmap;
                return true;
            }
        }

        public void Add(string key, Bitmap bitmap)
        {
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out var existing))
                {
                    _lru.Remove(existing);
                    _entries.Remove(key);
                    _bytes -= existing.Value.Bytes;
                    if (!ReferenceEquals(existing.Value.Bitmap, bitmap)) existing.Value.Bitmap.Dispose();
                }

                var bytes = Math.Max(1L, (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4);
                var node = _lru.AddFirst((key, bitmap, bytes));
                _entries[key] = node;
                _bytes += bytes;

                while (_bytes > _maxBytes && _lru.Last is { } last)
                {
                    _lru.RemoveLast();
                    _entries.Remove(last.Value.Key);
                    _bytes -= last.Value.Bytes;
                    last.Value.Bitmap.Dispose();
                }
            }
        }
    }

    internal sealed class ByteLruCache
    {
        private readonly long _maxBytes;
        private readonly object _sync = new();
        private readonly Dictionary<string, LinkedListNode<(string Key, byte[] Bytes)>> _entries = new(StringComparer.Ordinal);
        private readonly LinkedList<(string Key, byte[] Bytes)> _lru = new();
        private long _bytes;

        public ByteLruCache(long maxBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
            _maxBytes = maxBytes;
        }

        public byte[] GetOrAdd(string key, Func<byte[]> valueFactory)
        {
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out var cached))
                {
                    _lru.Remove(cached);
                    _lru.AddFirst(cached);
                    return cached.Value.Bytes;
                }
            }

            var created = valueFactory();
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out var cached))
                {
                    _lru.Remove(cached);
                    _lru.AddFirst(cached);
                    return cached.Value.Bytes;
                }

                var node = _lru.AddFirst((key, created));
                _entries[key] = node;
                _bytes += created.LongLength;
                while (_bytes > _maxBytes && _lru.Last is { } last)
                {
                    _lru.RemoveLast();
                    _entries.Remove(last.Value.Key);
                    _bytes -= last.Value.Bytes.LongLength;
                }
                return created;
            }
        }

        internal int Count
        {
            get { lock (_sync) return _entries.Count; }
        }

        internal long ByteCount
        {
            get { lock (_sync) return _bytes; }
        }
    }

    private FileListViewModel? _subscribedViewModel;
    private ObservableCollection<FileSystemEntry>? _subscribedEntries;
    private ContextMenu? _openMenu;
    private readonly List<Bitmap> _menuOwnedBitmaps = [];
    private int _menuRequestVersion;
    private bool _syncingSelection;
    private bool _clearingPresentationSelection;
    private TextBox? _renameEditor;
    private TextBlock? _renameLabel;
    private bool _finishingRename;
    private string? _activeRenamePath;
    private string? _suppressSlowRenamePath;
    private DateTime _suppressSlowRenameUntilUtc;
    private Point? _dragStartPoint;
    private FileSystemEntry? _dragStartEntry;
    private PointerPressedEventArgs? _dragPointerEvent;
    private Task<IReadOnlyList<IStorageItem>>? _dragStorageItemsTask;
    private Bitmap? _dragStartPreviewBitmap;
    private bool _dragStarted;
    private FileListColumn? _resizingColumn;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private FileListColumnLayoutService? _columnLayoutService;
    private FileListColumnWidths _effectiveColumnWidths = FileListColumnLayoutService.Defaults;
    private bool _columnLayoutSubscribed;
    private CancellationTokenSource? _renameDelayCts;
    private bool _collapseSelectionOnRelease;
    private FileSystemEntry? _pressedEntry;
    private Control? _dragOverVisual;
    private FileSystemEntry? _dragOverTargetEntry;
    private FileSystemEntry? _rightPressedEntry;
    private Control? _rightPressedAnchor;
    private FileSystemEntry[]? _rightClickSelectionSnapshot;
    // Avalonia's ListBox can raise a deferred SelectionChanged after a secondary
    // click has already been released. Keep a guard for the whole context-menu
    // interaction instead of only while the pointer is physically pressed.
    private bool _contextMenuSelectionGuard;
    private bool _selectionSyncQueued;
    private bool _entriesVisualRefreshQueued;
    private Vector _pendingEntriesScrollOffset;
    private int _scrollRestoreVersion;
    private int _savedScrollOffsetAppliedVersion;
    private bool _applyingViewModelEntries;
    private bool _restoringNavigationSelection;
    private bool _restoringNavigationSelectionToTop;
    private bool _allowRestoreBringIntoView;
    private DateTime _ignoreEmptySelectionUntilUtc;
    private Point? _marqueeStart;
    private bool _marqueeActive;
    private bool _suppressControlSelectionDuringMarquee;
    private KeyModifiers _marqueeModifiers;
    private HashSet<FileSystemEntry> _marqueeBaseSelection = [];
    // An entry can have several separate Finder-style hit regions (icon, name,
    // modified date, size and kind). Keeping those rectangles separate prevents
    // the whitespace between columns, or to the right of a row, from selecting
    // an unrelated item merely because it shares the same vertical row.
    private readonly Dictionary<FileSystemEntry, List<Rect>> _marqueeEntryBounds = [];
    private readonly DispatcherTimer _marqueeScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private Point _marqueeCurrentViewportPoint;
    private ScrollViewer? _marqueeScrollViewer;

    public FileListView()
    {
        InitializeComponent();
        GroupedListItems.ItemsSource = _groupedListRows;
        GridViewItems.ItemsSource = _gridRows;
        SizeChanged += (_, _) =>
        {
            RebuildGridRowsIfColumnCountChanged();
            ApplyListColumnWidths();
        };
        AddHandler(PointerPressedEvent, OnDismissClick, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnGlobalPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(Control.RequestBringIntoViewEvent, OnRequestBringIntoView, RoutingStrategies.Bubble, handledEventsToo: true);
        // Track the complete gesture at the view root. On macOS, pointer capture can
        // reroute subsequent moves above the inner ListBox/FileScroll control; root
        // tunnel handlers keep the press -> move -> release chain intact.
        AddHandler(PointerPressedEvent, OnEmptyAreaPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnMarqueePointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnMarqueePointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        _marqueeScrollTimer.Tick += (_, _) => OnMarqueeScrollTick();
    }

    private FileListViewModel? ViewModel => DataContext as FileListViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        UnsubscribeColumnLayoutService();
        if (_subscribedViewModel != null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel.SelectedEntries.CollectionChanged -= OnSelectedEntriesChanged;
            _subscribedViewModel.RenameRequested -= OnRenameRequested;
            _subscribedViewModel.ScrollToSelectionRequested -= OnScrollToSelectionRequested;
            _subscribedViewModel.CaptureNavigationAnchorRequested -= OnCaptureNavigationAnchorRequested;
        }
        SubscribeEntriesCollection(null);

        base.OnDataContextChanged(e);
        _subscribedViewModel = ViewModel;
        if (_subscribedViewModel != null)
        {
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _subscribedViewModel.SelectedEntries.CollectionChanged += OnSelectedEntriesChanged;
            _subscribedViewModel.RenameRequested += OnRenameRequested;
            _subscribedViewModel.ScrollToSelectionRequested += OnScrollToSelectionRequested;
            _subscribedViewModel.CaptureNavigationAnchorRequested += OnCaptureNavigationAnchorRequested;
            SubscribeEntriesCollection(_subscribedViewModel.Entries);
            _columnLayoutService = _subscribedViewModel.ColumnLayoutService;
            SubscribeColumnLayoutService();
        }

        UpdateViewMode();
        UpdateEmptyState();
        RebuildPresentationRows();
        QueueSelectionSynchronization();
        UpdateCutStates();
        ApplyListColumnWidths();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _columnLayoutService ??= ViewModel?.ColumnLayoutService;
        SubscribeColumnLayoutService();
        Dispatcher.UIThread.Post(ApplyListColumnWidths, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnsubscribeColumnLayoutService();
        base.OnDetachedFromVisualTree(e);
    }

    private void SubscribeColumnLayoutService()
    {
        if (_columnLayoutSubscribed || _columnLayoutService == null)
            return;

        _columnLayoutService.PreferredWidthsChanged += OnPreferredColumnWidthsChanged;
        _columnLayoutSubscribed = true;
    }

    private void UnsubscribeColumnLayoutService()
    {
        if (_columnLayoutSubscribed && _columnLayoutService != null)
            _columnLayoutService.PreferredWidthsChanged -= OnPreferredColumnWidthsChanged;

        _columnLayoutSubscribed = false;
        _columnLayoutService = null;
    }

    private void OnPreferredColumnWidthsChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
            ApplyListColumnWidths();
        else
            Dispatcher.UIThread.Post(ApplyListColumnWidths);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileListViewModel.ViewMode))
        {
            UpdateViewMode();
            RebuildPresentationRows();
            Dispatcher.UIThread.Post(() =>
            {
                ApplyListColumnWidths();
                QueueSelectionSynchronization();
            });
        }

        if (e.PropertyName is nameof(FileListViewModel.GroupField)
            or nameof(FileListViewModel.Groups))
        {
            UpdateViewMode();
            QueueEntriesVisualRefresh();
        }

        if (e.PropertyName is nameof(FileListViewModel.SortField)
            or nameof(FileListViewModel.SortAscending))
        {
            UpdateSortHeaders();
        }

        if (e.PropertyName == nameof(FileListViewModel.Entries))
        {
            if (ReferenceEquals(_subscribedEntries, ViewModel?.Entries))
            {
                UpdateEmptyState();
                UpdateCutStates();
                return;
            }

            _applyingViewModelEntries = true;
            SubscribeEntriesCollection(ViewModel?.Entries);
            var scrollMode = ViewModel?.ScrollBehaviorAfterLoad ?? FileListViewModel.ScrollMode.ResetToTop;
            var preservedOffset = GetActiveScrollViewer()?.Offset ?? default;
            UpdateEmptyState();
            RebuildPresentationRows();
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    ApplyListColumnWidths();
                    if (scrollMode == FileListViewModel.ScrollMode.RestoreNavigation)
                    {
                        ApplyScrollBehavior(scrollMode, preservedOffset);
                    }
                    else
                    {
                        SynchronizeSelectionControls();
                        ApplyScrollBehavior(scrollMode, preservedOffset);
                    }
                    UpdateCutStates();
                }
                finally
                {
                    _applyingViewModelEntries = false;
                }
            }, DispatcherPriority.Loaded);
        }

        if (e.PropertyName == nameof(FileListViewModel.IsLoading))
            UpdateEmptyState();

        if (e.PropertyName == nameof(FileListViewModel.CutPaths))
            Dispatcher.UIThread.Post(UpdateCutStates);
    }

    private void SubscribeEntriesCollection(ObservableCollection<FileSystemEntry>? entries)
    {
        if (ReferenceEquals(_subscribedEntries, entries)) return;
        if (_subscribedEntries != null)
            _subscribedEntries.CollectionChanged -= OnEntriesCollectionChanged;
        _subscribedEntries = entries;
        if (_subscribedEntries != null)
            _subscribedEntries.CollectionChanged += OnEntriesCollectionChanged;
    }

    private void OnEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyState();
        QueueEntriesVisualRefresh();
    }

    private void QueueEntriesVisualRefresh()
    {
        if (_entriesVisualRefreshQueued)
            return;

        _entriesVisualRefreshQueued = true;
        _pendingEntriesScrollOffset = GetActiveScrollViewer()?.Offset ?? default;
        Dispatcher.UIThread.Post(() =>
        {
            _entriesVisualRefreshQueued = false;
            var usesPresentationRows = GridViewItems.IsVisible || GroupedListItems.IsVisible;
            if (usesPresentationRows)
                RebuildPresentationRows();

            ApplyListColumnWidths();
            UpdateCutStates();
            QueueSelectionSynchronization();

            if (usesPresentationRows)
            {
                var preservedOffset = _pendingEntriesScrollOffset;
                Dispatcher.UIThread.Post(
                    () => ApplyScrollBehavior(FileListViewModel.ScrollMode.PreservePosition, preservedOffset),
                    DispatcherPriority.Loaded);
            }
        }, DispatcherPriority.Background);
    }

    private void UpdateCutStates()
    {
        if (ViewModel == null) return;
        // Re-runs on every batched load; skip the full walk while nothing is cut.
        if (ViewModel.CutPaths.Count == 0 && !_anyCutApplied) return;
        _anyCutApplied = false;
        foreach (var entry in ViewModel.Entries)
        {
            var isCut = ViewModel.CutPaths.Contains(entry.FullPath);
            entry.IsCut = isCut;
            if (isCut) _anyCutApplied = true;
        }
    }

    private void OnEntryImageLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Image image)
            _ = LoadEntryImageAsync(image);
    }

    private void OnEntryImageDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is not Image image) return;
        CancelEntryImageLoad(image);
        DetachThumbnailAwait(image);
        image.Tag = null;
        Dispatcher.UIThread.Post(() => _ = LoadEntryImageAsync(image), DispatcherPriority.Loaded);
    }

    private async System.Threading.Tasks.Task LoadEntryImageAsync(Image image)
    {
        if (image.DataContext is not FileSystemEntry entry) return;
        var entryPath = entry.FullPath;
        var source = !string.IsNullOrWhiteSpace(entry.IconUrl) ? entry.IconUrl : null;

        if (entry.ThumbnailUrl is { Length: > 0 } thumbnailUrl
            && Path.IsPathFullyQualified(thumbnailUrl)
            && !File.Exists(thumbnailUrl))
        {
            entry.ThumbnailUrl = null;
        }

        if (image.Tag is EntryImageLoadState activeState
            && !activeState.Cancellation.IsCancellationRequested
            && ReferenceEquals(image.DataContext, entry))
            return;

        source = !string.IsNullOrWhiteSpace(entry.ThumbnailUrl) ? entry.ThumbnailUrl : source;

        // If thumbnail not yet available for a virtual entry, listen for it
        if (string.IsNullOrWhiteSpace(source) && entry.IsVirtual)
        {
            // Detach a previous await first so recycled containers never accumulate handlers.
            DetachThumbnailAwait(image);
            PropertyChangedEventHandler? handler = null;
            handler = (_, args) =>
            {
                if (args.PropertyName == nameof(FileSystemEntry.ThumbnailUrl)
                    && !string.IsNullOrWhiteSpace(entry.ThumbnailUrl)
                    && ReferenceEquals(image.DataContext, entry))
                {
                    DetachThumbnailAwait(image);
                    Dispatcher.UIThread.Post(() => _ = LoadEntryImageAsync(image), DispatcherPriority.Loaded);
                }
            };
            entry.PropertyChanged += handler;
            image.SetValue(ThumbnailAwaitProperty, new EntryThumbnailAwaitState { Entry = entry, Handler = handler });
            return;
        }

        if (image.Tag is string currentSource && currentSource == source && image.Source != null) return;

        var cts = new CancellationTokenSource();
        var state = new EntryImageLoadState { Source = entryPath, Cancellation = cts };
        image.Tag = state;
        try
        {
            // The macOS thumbnail service supports both images and Quick Look documents.
            // Do not gate it on the icon classification: that prevented PDF, text and
            // Office documents from ever reaching the service.
            if (!entry.IsDirectory && !entry.IsVirtual && string.IsNullOrWhiteSpace(entry.ThumbnailUrl))
            {
                var thumbnailService = App.Services.GetService<IThumbnailService>();
                var thumbnail = thumbnailService == null
                    ? null
                    : await thumbnailService.GetThumbnailResultAsync(entry.FullPath, 256, cts.Token);
                if (thumbnail is { Bytes.Length: > 0 })
                {
                    if (ReferenceEquals(image.DataContext, entry) && entry.FullPath == entryPath)
                        entry.ThumbnailUrl = thumbnail.CachePath;
                }
            }

            source = !string.IsNullOrWhiteSpace(entry.ThumbnailUrl) ? entry.ThumbnailUrl : source;
            if (string.IsNullOrWhiteSpace(source) || !ReferenceEquals(image.DataContext, entry)) return;

            state.Source = source;
            var bitmap = await GetEntryBitmapAsync(source, cts.Token);
            if (bitmap != null
                && ReferenceEquals(image.DataContext, entry)
                && entry.FullPath == entryPath
                && ReferenceEquals(image.Tag, state)
                && !cts.IsCancellationRequested)
            {
                image.Source = bitmap;
                image.Tag = source;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
        finally
        {
            if (ReferenceEquals(image.Tag, state))
                image.Tag = null;
            cts.Dispose();
        }
    }

    private static void CancelEntryImageLoad(Image image)
    {
        if (image.Tag is EntryImageLoadState state)
            state.Cancellation.Cancel();
    }

    private static void DetachThumbnailAwait(Image image)
    {
        if (image.GetValue(ThumbnailAwaitProperty) is not { } state) return;
        image.SetValue(ThumbnailAwaitProperty, null);
        state.Entry.PropertyChanged -= state.Handler;
    }

    internal static async System.Threading.Tasks.Task<Bitmap?> GetEntryBitmapAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        if (EntryImageCache.TryGet(source, out var cachedBitmap)) return cachedBitmap;

        await EntryImageLoadGate.WaitAsync(cancellationToken);
        try
        {
            if (EntryImageCache.TryGet(source, out cachedBitmap)) return cachedBitmap;

            var bitmap = await System.Threading.Tasks.Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        var comma = source.IndexOf(',');
                        if (comma < 0) return null;
                        using var dataStream = new MemoryStream(Convert.FromBase64String(source[(comma + 1)..]));
                        return new Bitmap(dataStream);
                    }

                    if (!File.Exists(source)) return null;
                    using var fileStream = File.OpenRead(source);
                    return new Bitmap(fileStream);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    return null;
                }
            }, cancellationToken);
            if (bitmap != null)
                EntryImageCache.Add(source, bitmap);
            return bitmap;
        }
        finally
        {
            EntryImageLoadGate.Release();
        }
    }

    private void OnColumnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: string tag } handle
            || !TryGetFileListColumn(tag, out var column)
            || _columnLayoutService == null)
            return;
        if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;

        _resizingColumn = column;
        _resizeStartX = e.GetPosition(InteractionSurface).X;
        _resizeStartWidth = _effectiveColumnWidths[column];
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnColumnResizeMoved(object? sender, PointerEventArgs e)
    {
        if (_resizingColumn is not { } column
            || _columnLayoutService == null
            || sender is not Control handle
            || e.Pointer.Captured != handle)
            return;

        var delta = e.GetPosition(InteractionSurface).X - _resizeStartX;
        var requested = _resizeStartWidth + delta;
        var width = FileListColumnLayoutService.ClampInteractiveWidth(
            column,
            requested,
            _effectiveColumnWidths,
            GetAvailableDataWidth());
        _columnLayoutService.Preview(column, width);
        e.Handled = true;
    }

    private void OnColumnResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizingColumn is not { } column) return;
        _resizingColumn = null;
        e.Pointer.Capture(null);
        _columnLayoutService?.Commit(column);
        e.Handled = true;
    }

    private void OnColumnResizeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border { Tag: string tag }
            || !TryGetFileListColumn(tag, out var column))
            return;

        _resizingColumn = null;
        _columnLayoutService?.Reset(column);
        e.Handled = true;
    }

    private void ApplyListColumnWidths()
    {
        var preferred = _columnLayoutService?.PreferredWidths ?? FileListColumnLayoutService.Defaults;
        var effective = FileListColumnLayoutService.CalculateEffective(
            preferred,
            GetAvailableDataWidth());

        // Re-runs on every batched load and resize; when the widths are unchanged skip
        // the header assignments and the walk over the whole visual tree.
        if (_lastAppliedColumnWidths == effective)
            return;
        _lastAppliedColumnWidths = effective;
        _effectiveColumnWidths = effective;

        for (var column = 1; column <= 4 && column < ListHeaderGrid.ColumnDefinitions.Count; column++)
            ListHeaderGrid.ColumnDefinitions[column].Width = new GridLength(
                effective[(FileListColumn)(column - 1)]);

        foreach (var grid in this.GetVisualDescendants().OfType<Grid>()
                     .Where(grid => grid.Classes.Contains("file-list-row-grid")))
            ApplyColumnWidthsToRow(grid);
    }

    // Attached (not Loaded) so recycled and fresh rows get the effective widths
    // before their first measure; otherwise rows flash at the template's hardcoded
    // widths for a frame and snap into place once the Loaded pass corrects them.
    private void OnFileListRowAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Grid grid)
            ApplyColumnWidthsToRow(grid);
    }

    private void ApplyColumnWidthsToRow(Grid grid)
    {
        for (var column = 1; column <= 4 && column < grid.ColumnDefinitions.Count; column++)
            grid.ColumnDefinitions[column].Width = new GridLength(
                _effectiveColumnWidths[(FileListColumn)(column - 1)]);

        // A left-aligned name hit target sizes to its text. Without updating its
        // constraint when columns shrink, a long filename can extend over the date
        // column both visually and for pointer hit testing. Keep an 8-point gutter
        // at each side and clip the target to the effective Name column width.
        var nameTargetWidth = Math.Max(
            0,
            _effectiveColumnWidths[FileListColumn.Name] - 16);
        foreach (var target in grid.GetVisualDescendants().OfType<Control>()
                     .Where(control => control.Classes.Contains("list-name-hit")))
            target.MaxWidth = nameTargetWidth;
    }

    private double GetAvailableDataWidth()
    {
        const double iconColumnWidth = 22;
        var headerWidth = ListHeaderGrid.Bounds.Width;
        if (headerWidth <= 0)
            headerWidth = Math.Max(0, Bounds.Width - 24);

        return headerWidth > iconColumnWidth
            ? headerWidth - iconColumnWidth
            : FileListColumnLayoutService.Defaults.Total;
    }

    private static bool TryGetFileListColumn(string tag, out FileListColumn column)
    {
        if (int.TryParse(tag, out var gridColumn) && gridColumn is >= 1 and <= 4)
        {
            column = (FileListColumn)(gridColumn - 1);
            return true;
        }

        column = default;
        return false;
    }

    private void ApplyScrollBehavior(FileListViewModel.ScrollMode mode, Vector preservedOffset)
    {
        var scroll = GetActiveScrollViewer();
        if (scroll == null) return;
        switch (mode)
        {
            case FileListViewModel.ScrollMode.PreservePosition:
                var maxOffset = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
                scroll.Offset = new Vector(0, Math.Min(preservedOffset.Y, maxOffset));
                break;
            case FileListViewModel.ScrollMode.RestoreNavigation:
            case FileListViewModel.ScrollMode.ScrollToSelected:
                QueueBringSelectedEntryIntoView(mode == FileListViewModel.ScrollMode.RestoreNavigation);
                break;
            default:
                scroll.Offset = new Vector(0, 0);
                break;
        }
    }

    private ScrollViewer? GetActiveScrollViewer()
    {
        var host = FileItemsList.IsVisible ? (Control)FileItemsList
            : GridViewItems.IsVisible ? GridViewItems
            : GroupedListItems;
        return host.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
    }

    private void QueueBringSelectedEntryIntoView(bool restoreSavedViewport)
    {
        var version = ++_scrollRestoreVersion;
        _restoringNavigationSelection = true;
        _restoringNavigationSelectionToTop = restoreSavedViewport;
        _ = RestoreSelectedEntryPositionAsync(version, restoreSavedViewport);
    }

    private async Task RestoreSelectedEntryPositionAsync(int version, bool restoreSavedViewport)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var result = await Dispatcher.UIThread.InvokeAsync(
                () => TryRestoreSelectedEntryPosition(version, restoreSavedViewport),
                DispatcherPriority.Background);

            if (result == ScrollRestoreResult.Cancelled)
            {
                if (version == _scrollRestoreVersion)
                {
                    _restoringNavigationSelection = false;
                    _restoringNavigationSelectionToTop = false;
                }
                return;
            }

            if (result == ScrollRestoreResult.NoAnchor)
            {
                CompleteScrollRestore(version);
                return;
            }

            if (result == ScrollRestoreResult.Aligned)
            {
                CompleteScrollRestore(version);
                return;
            }

            await Task.Delay(16);
        }

        await Dispatcher.UIThread.InvokeAsync(
            () => CompleteRestoreWithBestEffort(version, restoreSavedViewport),
            DispatcherPriority.Background);
    }

    private enum ScrollRestoreResult
    {
        Pending,
        Aligned,
        NoAnchor,
        Cancelled
    }

    private ScrollRestoreResult TryRestoreSelectedEntryPosition(int version, bool restoreSavedViewport)
    {
        if (version != _scrollRestoreVersion || ViewModel == null)
            return ScrollRestoreResult.Cancelled;

        var selected = ViewModel.SelectedEntries.FirstOrDefault();
        if (selected == null)
            return ScrollRestoreResult.NoAnchor;

        if (restoreSavedViewport)
        {
            if (_savedScrollOffsetAppliedVersion != version
                && ViewModel.RestoredNavigationScrollOffsetY is { } savedOffset)
            {
                if (!TryApplySavedScrollOffset(savedOffset))
                    return ScrollRestoreResult.Pending;

                _savedScrollOffsetAppliedVersion = version;
                return ScrollRestoreResult.Pending;
            }

            if (FindVisibleEntryContent(selected) == null)
            {
                TryBringSelectedEntryIntoView(selected);
                return ScrollRestoreResult.Pending;
            }

            SynchronizeSelectionControls();
            if (ViewModel.RestoredNavigationAnchorViewportY != null
                && !AlignSelectedEntryToSavedViewportPosition())
            {
                return ScrollRestoreResult.Pending;
            }

            return ScrollRestoreResult.Aligned;
        }

        if (FindVisibleEntryContent(selected) == null)
        {
            TryBringSelectedEntryIntoView(selected);
            return ScrollRestoreResult.Pending;
        }

        SynchronizeSelectionControls();

        return ScrollRestoreResult.NoAnchor;
    }

    private void CompleteRestoreWithBestEffort(int version, bool restoreSavedViewport)
    {
        if (version != _scrollRestoreVersion || ViewModel == null)
            return;

        if (restoreSavedViewport)
        {
            if (_savedScrollOffsetAppliedVersion != version
                && ViewModel.RestoredNavigationScrollOffsetY is { } savedOffset)
            {
                TryApplySavedScrollOffset(savedOffset);
            }

            if (!AlignSelectedEntryToSavedViewportPosition())
                TryBringSelectedEntryIntoView();
            CompleteScrollRestore(version);
            return;
        }

        TryBringSelectedEntryIntoView();
        CompleteScrollRestore(version);
    }

    private void CompleteScrollRestore(int version)
    {
        if (version != _scrollRestoreVersion || ViewModel == null)
            return;

        ViewModel.ScrollBehaviorAfterLoad = FileListViewModel.ScrollMode.PreservePosition;
        _restoringNavigationSelection = false;
        _restoringNavigationSelectionToTop = false;
        _ignoreEmptySelectionUntilUtc = DateTime.UtcNow.AddMilliseconds(250);
    }

    private void OnRequestBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
    {
        if (_allowRestoreBringIntoView || !IsRestoringNavigationSelectionToTop())
            return;

        if (e.Source is Visual visual && !IsWithinVisual(visual, FileScroll))
            return;

        // Selection and container realization can request their own scrolling.
        // The restore loop owns the saved offset and anchor, so suppress those
        // competing requests instead of scheduling another position write.
        e.Handled = true;
    }

    private bool IsRestoringNavigationSelectionToTop()
        => _restoringNavigationSelectionToTop
           || ViewModel?.ScrollBehaviorAfterLoad == FileListViewModel.ScrollMode.RestoreNavigation
           || DateTime.UtcNow <= _ignoreEmptySelectionUntilUtc;

    private bool BringSelectedEntryIntoView()
    {
        var version = ++_scrollRestoreVersion;
        var restoreSavedViewport = ViewModel?.ScrollBehaviorAfterLoad == FileListViewModel.ScrollMode.RestoreNavigation;
        _restoringNavigationSelection = true;
        _restoringNavigationSelectionToTop = restoreSavedViewport;
        var result = TryRestoreSelectedEntryPosition(version, restoreSavedViewport);
        if (result == ScrollRestoreResult.Pending)
        {
            _ = RestoreSelectedEntryPositionAsync(version, restoreSavedViewport);
        }
        else if (result == ScrollRestoreResult.NoAnchor || result == ScrollRestoreResult.Aligned)
        {
            CompleteScrollRestore(version);
        }
        return result != ScrollRestoreResult.Pending && result != ScrollRestoreResult.Cancelled;
    }

    private static bool IsSameEntry(FileSystemEntry left, FileSystemEntry right)
        => ReferenceEquals(left, right)
           || string.Equals(left.FullPath, right.FullPath, StringComparison.Ordinal);

    private bool TryBringSelectedEntryIntoView(FileSystemEntry? selected = null)
    {
        selected ??= ViewModel?.SelectedEntries.FirstOrDefault();
        if (selected == null) return false;

        if (FileItemsList.IsVisible)
        {
            if (!FileItemsList.Items.Contains(selected))
                return false;
            ScrollIntoViewForRestore(FileItemsList, selected);
            return true;
        }
        else if (GroupedListItems.IsVisible
                 && _groupedRowByPath.TryGetValue(selected.FullPath, out var groupedRow))
        {
            ScrollIntoViewForRestore(GroupedListItems, groupedRow);
            return true;
        }
        else if (GridViewItems.IsVisible
                 && _gridRowByPath.TryGetValue(selected.FullPath, out var gridRow))
        {
            ScrollIntoViewForRestore(GridViewItems, gridRow);
            return true;
        }

        return false;
    }

    private void ScrollIntoViewForRestore(ListBox listBox, object item)
    {
        _allowRestoreBringIntoView = true;
        try
        {
            listBox.ScrollIntoView(item);
        }
        finally
        {
            _allowRestoreBringIntoView = false;
        }
    }

    private void OnCaptureNavigationAnchorRequested()
    {
        var (viewportY, scrollOffsetY) = CaptureSelectedEntryNavigationAnchor();
        ViewModel?.SaveCurrentNavigationAnchor(viewportY, scrollOffsetY);
    }

    private (double? ViewportY, double? ScrollOffsetY) CaptureSelectedEntryNavigationAnchor()
    {
        var scroll = GetActiveScrollViewer();
        if (ViewModel?.SelectedEntries.FirstOrDefault() is not { } selected)
            return (null, scroll?.Offset.Y);

        var visual = FindVisibleEntryContent(selected);
        if (scroll == null)
            return (null, null);
        if (visual == null)
            return (null, scroll.Offset.Y);

        return (visual.TranslatePoint(new Point(0, 0), scroll)?.Y, scroll.Offset.Y);
    }

    private bool TryApplySavedScrollOffset(double savedOffsetY)
    {
        var scroll = GetActiveScrollViewer();
        if (scroll == null || scroll.Viewport.Height <= 0)
            return false;

        var maxOffset = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        if (scroll.Extent.Height <= 0 && ViewModel?.Entries.Count > 0)
            return false;

        scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(savedOffsetY, 0, maxOffset));
        return true;
    }

    private bool AlignSelectedEntryToSavedViewportPosition()
    {
        var viewModel = ViewModel;
        var targetY = viewModel?.RestoredNavigationAnchorViewportY;
        if (viewModel == null || targetY == null || viewModel.SelectedEntries.FirstOrDefault() is not { } selected)
            return false;

        var scroll = GetActiveScrollViewer();
        var visual = FindVisibleEntryContent(selected);
        if (scroll == null || visual == null)
            return false;

        var point = visual.TranslatePoint(new Point(0, 0), scroll);
        if (point == null)
            return false;

        var maxOffset = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        var desiredOffsetY = scroll.Offset.Y + point.Value.Y - targetY.Value;
        scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(desiredOffsetY, 0, maxOffset));
        return true;
    }

    private Control? FindVisibleEntryContent(FileSystemEntry entry)
    {
        var host = FileItemsList.IsVisible ? (Control)FileItemsList
            : GridViewItems.IsVisible ? GridViewItems
            : GroupedListItems;

        return host.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(control =>
                IsEntryDataContext(control.DataContext, entry)
                && control.Classes.Contains("entry-content"))
            ?? host.GetVisualDescendants()
                .OfType<Control>()
                .FirstOrDefault(control => IsEntryDataContext(control.DataContext, entry));
    }

    private static bool IsEntryDataContext(object? dataContext, FileSystemEntry entry)
        => ReferenceEquals(dataContext, entry)
           || (dataContext is FileSystemEntry candidate
               && string.Equals(candidate.FullPath, entry.FullPath, StringComparison.Ordinal));

    private void OnSelectedEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        QueueSelectionSynchronization();
    }

    private void OnScrollToSelectionRequested()
    {
        Dispatcher.UIThread.Post(() =>
        {
            BringSelectedEntryIntoView();
            Dispatcher.UIThread.Post(SynchronizeSelectionControls, DispatcherPriority.Background);
        }, DispatcherPriority.Background);
    }

    private void OnRenameRequested(FileSystemEntry entry)
    {
        CancelActiveRename();

        var label = this.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(text => IsEntryNameLabelFor(text, entry));
        if (label?.Parent is not Control labelHost)
            return;

        // List view keeps the name inside a clipped Border so the rest of the
        // Name column remains canvas for marquee selection. Grid is still a
        // Panel, while Border is a Decorator; support both hosts when swapping
        // the label for the inline editor.
        var parent = labelHost as Panel;
        var decorator = labelHost as Decorator;
        if (parent == null && decorator == null)
            return;

        var layoutParent = parent ?? labelHost.FindAncestorOfType<Panel>();

        var index = parent?.Children.IndexOf(label) ?? -1;
        if (parent != null && index < 0)
            return;

        var isGridIconLabel = label.FindAncestorOfType<Border>()?.Classes.Contains("file-grid-content") == true;
        var editorWidth = GetInlineRenameEditorWidth(label, layoutParent, isGridIconLabel);
        var editor = new TextBox
        {
            Text = entry.Name,
            Margin = label.Margin,
            HorizontalAlignment = isGridIconLabel
                ? global::Avalonia.Layout.HorizontalAlignment.Center
                : global::Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = label.VerticalAlignment,
            VerticalContentAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            TextAlignment = isGridIconLabel ? TextAlignment.Center : TextAlignment.Left,
            MinWidth = isGridIconLabel ? 0 : 72,
            MinHeight = 0,
            Width = editorWidth
        };
        AppTypography.BindFontSize(editor, isGridIconLabel ? AppTypography.Caption : AppTypography.Body);
        editor.Classes.Add("inline-rename-editor");
        if (parent is Grid)
        {
            Grid.SetColumn(editor, Grid.GetColumn(label));
            Grid.SetColumnSpan(editor, Grid.GetColumnSpan(label));
            Grid.SetRow(editor, Grid.GetRow(label));
            Grid.SetRowSpan(editor, Grid.GetRowSpan(label));
        }

        if (parent != null)
        {
            parent.Children.RemoveAt(index);
            parent.Children.Insert(index, editor);
        }
        else
        {
            decorator!.Child = editor;
        }
        _renameEditor = editor;
        _renameLabel = label;
        _activeRenamePath = entry.FullPath;

        editor.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await FinishRenameAsync(entry, commit: true);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                await FinishRenameAsync(entry, commit: false);
            }
        };
        editor.LostFocus += async (_, _) => await FinishRenameAsync(entry, commit: true);

        Dispatcher.UIThread.Post(() =>
        {
            editor.Focus();
            var extensionLength = entry.IsDirectory ? 0 : Path.GetExtension(entry.Name).Length;
            editor.SelectionStart = 0;
            editor.SelectionEnd = Math.Max(0, entry.Name.Length - extensionLength);
        });
    }

    private static double GetInlineRenameEditorWidth(TextBlock label, Panel? parent, bool isGridIconLabel)
    {
        var parentWidth = parent?.Bounds.Width ?? 0;
        if (isGridIconLabel)
            return parentWidth > 0 ? parentWidth : 84;

        var availableWidth = 320d;
        if (parent is Grid grid)
        {
            // In list view the label is inside the clipped name Border, so the
            // attached Grid column belongs to that Border rather than the label.
            var columnHost = label.Parent as Control;
            var column = columnHost?.Parent is Grid
                ? Grid.GetColumn(columnHost)
                : Grid.GetColumn(label);
            if (column >= 0 && column < grid.ColumnDefinitions.Count)
            {
                var columnWidth = grid.ColumnDefinitions[column].ActualWidth;
                if (columnWidth > 0)
                {
                    const double trailingGap = 4;
                    availableWidth = columnWidth - label.Margin.Left - label.Margin.Right - trailingGap;
                }
            }
        }
        else if (parentWidth > 0)
        {
            availableWidth = parentWidth - label.Margin.Left - label.Margin.Right;
        }

        const double minimumWidth = 72;
        const double editorChromeWidth = 12;
        var maximumWidth = Math.Max(minimumWidth, availableWidth);
        var desiredWidth = Math.Ceiling(label.TextLayout.WidthIncludingTrailingWhitespace + editorChromeWidth);
        return Math.Clamp(desiredWidth, minimumWidth, maximumWidth);
    }

    private static bool IsEntryNameLabelFor(TextBlock text, FileSystemEntry entry)
    {
        if (text.DataContext is not FileSystemEntry candidate)
            return false;

        var isNameLabel = text.Classes.Contains("entry-name-text") || text.Name == "EntryNameText";
        return isNameLabel && string.Equals(candidate.FullPath, entry.FullPath, StringComparison.Ordinal);
    }

    private async System.Threading.Tasks.Task FinishRenameAsync(FileSystemEntry entry, bool commit)
    {
        if (_finishingRename || _renameEditor == null) return;
        _finishingRename = true;

        var renamePath = _activeRenamePath ?? entry.FullPath;
        var newName = _renameEditor.Text?.Trim() ?? string.Empty;
        SuppressImmediateSlowRename(renamePath);
        try
        {
            if (commit && newName.Length > 0 && !string.Equals(newName, entry.Name, StringComparison.Ordinal))
                await ViewModel!.RenameEntryAsync(entry, newName);
        }
        finally
        {
            // Keep the editor (and the submitted name) visible while the file
            // operation runs. Restoring afterward binds the label to the final
            // entry instead of briefly painting the old name.
            RestoreRenameLabel();
            _finishingRename = false;
        }
    }

    private void CancelActiveRename()
    {
        if (_renameEditor == null) return;
        _finishingRename = true;
        if (!string.IsNullOrWhiteSpace(_activeRenamePath))
            SuppressImmediateSlowRename(_activeRenamePath);
        RestoreRenameLabel();
        _finishingRename = false;
    }

    private void RestoreRenameLabel()
    {
        var editor = _renameEditor;
        var label = _renameLabel;
        _renameEditor = null;
        _renameLabel = null;
        _activeRenamePath = null;

        if (editor == null || label == null)
            return;

        if (editor.Parent is Panel parent)
        {
            var index = parent.Children.IndexOf(editor);
            if (index < 0) return;
            parent.Children.RemoveAt(index);
            parent.Children.Insert(index, label);
        }
        else if (editor.Parent is Decorator decorator)
        {
            decorator.Child = label;
        }
    }

    private void SuppressImmediateSlowRename(string path)
    {
        _suppressSlowRenamePath = path;
        _suppressSlowRenameUntilUtc = DateTime.UtcNow.AddMilliseconds(700);
    }

    private bool ConsumeImmediateSlowRenameSuppression(FileSystemEntry entry)
    {
        if (string.IsNullOrWhiteSpace(_suppressSlowRenamePath))
            return false;

        var suppress = DateTime.UtcNow <= _suppressSlowRenameUntilUtc
                       && string.Equals(_suppressSlowRenamePath, entry.FullPath, StringComparison.Ordinal);
        if (suppress || DateTime.UtcNow > _suppressSlowRenameUntilUtc)
        {
            _suppressSlowRenamePath = null;
            _suppressSlowRenameUntilUtc = default;
        }

        return suppress;
    }

    private void FillMenu(
        ItemsControl menu,
        System.Collections.Generic.IList<ContextMenuAction> actions,
        int requestVersion)
    {
        menu.Items.Clear();

        // Quick actions bar — vertical icon+text buttons at top, evenly distributed
        var quickActions = actions.Where(a => a.IsQuickAction).ToList();
        if (quickActions.Count > 0)
        {
            var quickGrid = new Grid
            {
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch
            };
            for (int i = 0; i < quickActions.Count; i++)
                quickGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            for (int i = 0; i < quickActions.Count; i++)
            {
                var qa = quickActions[i];
                var btnContent = new StackPanel
                {
                    Orientation = global::Avalonia.Layout.Orientation.Vertical,
                    Spacing = 2,
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center
                };
                if (!string.IsNullOrEmpty(qa.IconSvg))
                {
                    try
                    {
                        btnContent.Children.Add(new PathIcon
                        {
                            Data = Geometry.Parse(qa.IconSvg),
                            Width = 16, Height = 16,
                            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center
                        });
                    }
                    catch { btnContent.Children.Add(AppTypography.BindFontSize(new TextBlock { Text = qa.Label, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center }, AppTypography.Caption)); }
                }
                btnContent.Children.Add(AppTypography.BindFontSize(new TextBlock
                {
                    Text = qa.Label,
                    Foreground = new SolidColorBrush(Color.Parse("#636366")),
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center
                }, AppTypography.Meta));

                var btn = new Button
                {
                    Content = btnContent,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(0),
                    IsEnabled = qa.IsEnabled,
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch,
                    VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Stretch,
                    HorizontalContentAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalContentAlignment = global::Avalonia.Layout.VerticalAlignment.Center
                };
                btn.Classes.Add("ghost");
                var hoverBackground = new SolidColorBrush(Color.Parse("#0E000000"));
                var buttonSurface = new Border
                {
                    Width = 44,
                    Height = 44,
                    CornerRadius = new CornerRadius(6),
                    Background = Brushes.Transparent,
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                    Child = btn
                };
                buttonSurface.PointerEntered += (_, _) => buttonSurface.Background = hoverBackground;
                buttonSurface.PointerExited += (_, _) => buttonSurface.Background = Brushes.Transparent;
                ToolTip.SetTip(btn, qa.Label);
                if (qa.Execute != null)
                {
                    var captured = qa;
                    btn.Click += async (_, _) => await ExecuteMenuActionAsync(captured);
                }
                Grid.SetColumn(buttonSurface, i);
                quickGrid.Children.Add(buttonSurface);
            }
            var quickActionsHost = new MenuItem
            {
                Focusable = false,
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch,
                Template = new FuncControlTemplate<MenuItem>((_, _) => new Border
                {
                    Padding = new Thickness(4, 8, 4, 4),
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch,
                    Child = quickGrid
                })
            };
            menu.Items.Add(quickActionsHost);
            menu.Items.Add(new Separator());
        }

        // Standard menu items — strip consecutive / leading / trailing separators
        var standardActions = actions.Where(a => !a.IsQuickAction).ToList();
        var cleaned = new List<ContextMenuAction>();
        foreach (var action in standardActions)
        {
            if (action.IsSeparator && (cleaned.Count == 0 || cleaned[^1].IsSeparator))
                continue;
            cleaned.Add(action);
        }
        if (cleaned.Count > 0 && cleaned[^1].IsSeparator)
            cleaned.RemoveAt(cleaned.Count - 1);

        foreach (var action in cleaned)
        {
            if (action.IsSeparator)
            {
                menu.Items.Add(new Separator());
                continue;
            }

            var item = new MenuItem { Header = action.Label, IsEnabled = action.IsEnabled };
            if (!string.IsNullOrEmpty(action.ShortcutText))
                item.InputGesture = ParseShortcut(action.ShortcutText);
            if (!string.IsNullOrEmpty(action.IconSvg))
            {
                try { item.Icon = new PathIcon { Data = Geometry.Parse(action.IconSvg), Width = 16, Height = 16 }; }
                catch { }
            }
            if (!string.IsNullOrWhiteSpace(action.IconBase64))
                _ = LoadMenuIconAsync(item, action.IconBase64, 16, requestVersion);
            else if (action.LoadIconBase64Async != null)
                _ = LoadMenuIconAsync(item, action.LoadIconBase64Async, 16, requestVersion);
            if (action.Execute != null)
            {
                var captured = action;
                item.Click += async (_, _) => await ExecuteMenuActionAsync(captured);
            }
            if (action.SubItems is { Count: > 0 })
            {
                FillMenu(item, action.SubItems.ToList(), requestVersion);
                ContextMenuPopupStyler.Attach(item);
            }
            menu.Items.Add(item);
        }
    }

    private async System.Threading.Tasks.Task LoadMenuIconAsync(
        MenuItem item,
        string iconBase64,
        double size,
        int requestVersion)
    {
        Bitmap? bitmap = null;
        try
        {
            bitmap = await System.Threading.Tasks.Task.Run(() =>
            {
                var comma = iconBase64.IndexOf(',');
                var payload = iconBase64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0
                    ? iconBase64[(comma + 1)..]
                    : iconBase64;
                var bytes = MenuIconCache.GetOrAdd(payload, () => Convert.FromBase64String(payload));
                using var stream = new MemoryStream(bytes, writable: false);
                return new Bitmap(stream);
            });

            if (requestVersion != _menuRequestVersion)
            {
                bitmap.Dispose();
                bitmap = null;
                return;
            }

            var ownedBitmap = bitmap;
            _menuOwnedBitmaps.Add(ownedBitmap);
            bitmap = null;
            item.Icon = new Image
            {
                Source = ownedBitmap,
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform
            };
        }
        catch
        {
            bitmap?.Dispose();
            // Keep the SVG fallback when the cached image cannot be decoded.
        }
    }

    private async System.Threading.Tasks.Task LoadMenuIconAsync(
        MenuItem item,
        Func<System.Threading.Tasks.Task<string?>> loadIconBase64Async,
        double size,
        int requestVersion)
    {
        try
        {
            var iconBase64 = await loadIconBase64Async();
            if (requestVersion != _menuRequestVersion || string.IsNullOrWhiteSpace(iconBase64)) return;
            await LoadMenuIconAsync(item, iconBase64, size, requestVersion);
        }
        catch
        {
            // Keep the SVG fallback when the system icon cannot be loaded.
        }
    }

    private static KeyGesture? ParseShortcut(string text)
    {
        var parsed = text.Replace("⌘", "Cmd+").Replace("⇧", "Shift+")
            .Replace("⌥", "Alt+").Replace("⌃", "Ctrl+")
            .Replace("⌫", "Back").Replace("⌦", "Delete")
            .Replace("↩", "Enter").Replace("⇥", "Tab").Replace("⎋", "Escape");
        try { return KeyGesture.Parse(parsed); }
        catch { return null; }
    }

    private async System.Threading.Tasks.Task ShowMenuAsync(Control anchor, bool hasSelection)
    {
        if (ViewModel == null) return;

        var requestVersion = ++_menuRequestVersion;
        CloseCurrentMenu();
        var entry = hasSelection && ViewModel.SelectedEntries.Count > 0
            ? ViewModel.SelectedEntries[0]
            : null;

        if (entry != null)
            await ViewModel.ShowFileContextMenuAsync(entry, 0, 0);
        else
            await ViewModel.ShowBackgroundContextMenuAsync(0, 0);

        if (requestVersion != _menuRequestVersion || ViewModel == null)
            return;

        var menu = new ContextMenu();
        FillMenu(menu, ViewModel.ContextMenuActions, requestVersion);
        menu.AddHandler(KeyDownEvent, OnContextMenuKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        menu.Closing += OnContextMenuClosing;
        _openMenu = menu;
        menu.Open(anchor);

        if (entry == null || entry.IsVirtual || ViewModel.IsArchiveView)
            return;

        _ = LoadCompleteMenuAsync(menu, entry, requestVersion);
    }

    private async System.Threading.Tasks.Task LoadCompleteMenuAsync(
        ContextMenu menu,
        FileSystemEntry entry,
        int requestVersion)
    {
        try
        {
            var viewModel = ViewModel;
            if (viewModel == null) return;

            var completeActions = await viewModel.LoadCompleteFileContextMenuAsync(entry);
            if (requestVersion != _menuRequestVersion || !ReferenceEquals(_openMenu, menu) || ViewModel == null)
                return;

            viewModel.ContextMenuActions = new ObservableCollection<ContextMenuAction>(completeActions);
            var completeRequestVersion = ++_menuRequestVersion;
            DisposeOwnedMenuBitmaps();
            FillMenu(menu, viewModel.ContextMenuActions, completeRequestVersion);
        }
        catch
        {
            // Keep the already-open lightweight menu if dynamic menu loading fails.
        }
    }

    private async System.Threading.Tasks.Task ExecuteMenuActionAsync(ContextMenuAction action)
    {
        try
        {
            if (action.Execute != null)
                await action.Execute();
        }
        finally
        {
            DismissContextMenu();
        }
    }

    private void OnContextMenuKeyDown(object? sender, KeyEventArgs e)
    {
        TryHandleFileShortcut(e);
    }

    public void DismissContextMenu()
    {
        _menuRequestVersion++;
        CloseCurrentMenu();
        ViewModel?.CloseContextMenu();
        // The menu is no longer consuming pointer/selection events. Release the
        // secondary-click guard so keyboard and subsequent ListBox selection can
        // resume normally even when the menu was dismissed with Escape or by an
        // action rather than by a new left click.
        _contextMenuSelectionGuard = false;
        _rightClickSelectionSnapshot = null;
        _rightPressedEntry = null;
        _rightPressedAnchor = null;
    }

    private void CloseCurrentMenu()
    {
        var menu = _openMenu;
        _openMenu = null;
        if (menu != null)
        {
            menu.Closing -= OnContextMenuClosing;
            menu.Close();
        }
        DisposeOwnedMenuBitmaps();
    }

    private void OnContextMenuClosing(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu menu || !ReferenceEquals(menu, _openMenu)) return;
        _menuRequestVersion++;
        _openMenu = null;
        menu.Closing -= OnContextMenuClosing;
        DisposeOwnedMenuBitmaps();
        ViewModel?.CloseContextMenu();
        _contextMenuSelectionGuard = false;
        _rightClickSelectionSnapshot = null;
        _rightPressedEntry = null;
    }

    private void DisposeOwnedMenuBitmaps()
    {
        foreach (var bitmap in _menuOwnedBitmaps)
            bitmap.Dispose();
        _menuOwnedBitmaps.Clear();
    }

    private void OnDismissClick(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
        {
            _rightPressedEntry = null;
            _rightPressedAnchor = null;
            _rightClickSelectionSnapshot = null;
            _contextMenuSelectionGuard = false;
            DismissContextMenu();
            return;
        }

        if (!point.Properties.IsRightButtonPressed || ViewModel == null)
            return;

        ViewModel.NotifyTransientInteractionStarted();
        var sourceVisual = e.Source as Visual;
        var entry = FindDataContextInAncestors(sourceVisual) as FileSystemEntry;
        if (entry == null && !IsWithinVisual(sourceVisual, FileScroll))
            return;
        _rightPressedEntry = entry;
        _rightPressedAnchor = entry == null ? FileScroll : FindEntryAnchor(sourceVisual) ?? FileScroll;
        _contextMenuSelectionGuard = true;
        if (entry != null && !ViewModel.IsEntrySelected(entry))
        {
            ViewModel.SelectEntryForContextMenu(entry);
            QueueSelectionSynchronization();
        }
        _rightClickSelectionSnapshot = entry == null
            ? []
            : ViewModel.SelectedEntries.ToArray();
        if (entry == null)
        {
            ViewModel.ClearSelection();
        }

        // Prevent the nested ListBox from applying its default left-click-style
        // selection policy to a secondary (right) click. Finder keeps an existing
        // multi-selection when the context menu is opened on one of its members.
        e.Handled = true;
        ScheduleRightClickSelectionRestore();
    }

    private static Control? FindEntryAnchor(Visual? visual)
    {
        Control? fallback = null;
        for (; visual != null; visual = visual.GetVisualParent())
        {
            if (visual is not Control { DataContext: FileSystemEntry } control)
                continue;
            fallback ??= control;
            if (control.Classes.Contains("entry-content"))
                return control;
        }
        return fallback;
    }

    private static bool IsWithinVisual(Visual? visual, Visual ancestor)
    {
        for (; visual != null; visual = visual.GetVisualParent())
            if (ReferenceEquals(visual, ancestor))
                return true;
        return false;
    }

    private void OnListItemPointerPressed(object? sender, PointerPressedEventArgs e)
        => HandleItemPointerPressed(sender, e);

    private void OnGridItemPointerPressed(object? sender, PointerPressedEventArgs e)
        => HandleItemPointerPressed(sender, e);

    private void HandleItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not FileSystemEntry entry || ViewModel == null)
            return;

        // A virtualized ListBox can finish dispatching a SelectionChanged event
        // after the previous marquee has already been released. A real item press
        // starts a new interaction, so it is the safe point to resume the normal
        // control -> view-model selection flow.
        _suppressControlSelectionDuringMarquee = false;

        var owner = control.FindAncestorOfType<ListBox>();
        owner?.Focus();
        var point = e.GetCurrentPoint(control);

        if (point.Properties.IsRightButtonPressed)
        {
            CancelSlowRename();
            ViewModel.NotifyTransientInteractionStarted();
            _rightPressedEntry = entry;
            _rightPressedAnchor = control;
            _contextMenuSelectionGuard = true;
            if (!ViewModel.IsEntrySelected(entry))
                ViewModel.SelectEntryForContextMenu(entry);
            _rightClickSelectionSnapshot = ViewModel.SelectedEntries.ToArray();
            QueueSelectionSynchronization();
            e.Handled = true;
            ScheduleRightClickSelectionRestore();
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            _rightPressedEntry = null;
            _rightPressedAnchor = null;
            _rightClickSelectionSnapshot = null;
            _contextMenuSelectionGuard = false;
            CancelSlowRename();
            DismissContextMenu();
            var modifiers = e.KeyModifiers;
            var hasCommandModifier = modifiers.HasFlag(KeyModifiers.Meta) || modifiers.HasFlag(KeyModifiers.Control);
            var hasShiftModifier = modifiers.HasFlag(KeyModifiers.Shift);
            var wasAlreadySingleSelected = !hasCommandModifier && !hasShiftModifier
                                           && ViewModel.SelectedEntries.Count == 1
                                           && ViewModel.IsEntrySelected(entry);
            var suppressSlowRename = wasAlreadySingleSelected && ConsumeImmediateSlowRenameSuppression(entry);
            var preserveMultiSelectionForDrag = !hasCommandModifier && !hasShiftModifier
                                                && ViewModel.SelectedEntries.Count > 1
                                                && ViewModel.IsEntrySelected(entry);
            if (!preserveMultiSelectionForDrag)
                ViewModel.SelectEntry(entry, hasCommandModifier, hasShiftModifier);
            QueueSelectionSynchronization();
            _dragStartPoint = e.GetPosition(this);
            _dragStartEntry = entry;
            _dragPointerEvent = e;
            _dragStartPreviewBitmap = GetVisibleEntryBitmap(control, entry);
            _dragStorageItemsTask = null;
            _dragStarted = false;
            _collapseSelectionOnRelease = preserveMultiSelectionForDrag;
            _pressedEntry = entry;
            if (wasAlreadySingleSelected && !suppressSlowRename && !entry.IsVirtual && !ViewModel.IsArchiveView)
                _ = ScheduleSlowRenameAsync(entry);
            e.Handled = true;
        }
    }

    private async System.Threading.Tasks.Task ScheduleSlowRenameAsync(FileSystemEntry entry)
    {
        var cts = new CancellationTokenSource();
        _renameDelayCts = cts;
        try
        {
            await System.Threading.Tasks.Task.Delay(400, cts.Token);
            if (!cts.IsCancellationRequested && !_dragStarted && ViewModel?.SelectedEntries.Count == 1
                && ViewModel.IsEntrySelected(entry))
                ViewModel.RequestRename(entry);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_renameDelayCts, cts))
                _renameDelayCts = null;
            cts.Dispose();
        }
    }

    private void CancelSlowRename()
    {
        _renameDelayCts?.Cancel();
        _renameDelayCts = null;
    }

    private async void OnItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStarted || _dragStartPoint == null || _dragStartEntry == null
            || _dragPointerEvent == null || ViewModel == null)
            return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ResetDragState();
            return;
        }

        var current = e.GetPosition(this);
        var start = _dragStartPoint.Value;
        if (Math.Abs(current.X - start.X) < 5 && Math.Abs(current.Y - start.Y) < 5)
            return;

        var storageItemsTask = _dragStorageItemsTask ??= StartDragStorageItemsResolution();
        if (storageItemsTask == null)
        {
            ResetDragState();
            return;
        }

        // Starting a native drag after awaiting loses the current macOS mouse-drag event.
        // Wait for another PointerMoved event if the files are not resolved yet.
        if (!storageItemsTask.IsCompleted)
            return;

        IReadOnlyList<IStorageItem> storageItems;
        try
        {
            storageItems = storageItemsTask.GetAwaiter().GetResult();
        }
        catch
        {
            ResetDragState();
            return;
        }

        _dragStorageItemsTask = null;
        if (storageItems.Count == 0)
        {
            ResetDragState();
            return;
        }

        CancelSlowRename();
        _collapseSelectionOnRelease = false;
        _dragStarted = true;
        var representativeEntry = _dragStartEntry ?? ViewModel.SelectedEntries.FirstOrDefault();
        var dragItemCount = ViewModel.SelectedEntries.Count > 0
            ? ViewModel.SelectedEntries.Count
            : storageItems.Count;

        string? nativeDragPreviewPath = null;
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            var dragPaths = storageItems
                .Select(item => item.Path.LocalPath)
                .Where(path => File.Exists(path) || Directory.Exists(path))
                .ToArray();
            nativeDragPreviewPath = representativeEntry == null
                ? null
                : CreateNativeDragPreviewFile(representativeEntry, dragItemCount, _dragStartPreviewBitmap);
            if (topLevel != null
                && MacExplorer.Platforms.MacOS.MacNativeFileDrag.TryBeginFileDrag(
                    topLevel,
                    _dragPointerEvent.GetPosition(topLevel),
                    dragPaths,
                    nativeDragPreviewPath,
                    DragDropEffects.Copy | DragDropEffects.Move))
            {
                GC.KeepAlive(storageItems);
                return;
            }

            var data = CreateDragData(storageItems, representativeEntry, dragItemCount, _dragStartPreviewBitmap);

            // Invoke before the first await. Avalonia requires the originating press event,
            // while the current PointerMoved callback guarantees the native drag is active.
            var dragTask = DragDrop.DoDragDropAsync(
                _dragPointerEvent,
                data,
                DragDropEffects.Copy | DragDropEffects.Move);
            await dragTask;
            GC.KeepAlive(data);
            GC.KeepAlive(storageItems);
        }
        catch
        {
        }
        finally
        {
            TryDeleteFile(nativeDragPreviewPath);
            // DataTransfer owns the file items after DoDragDropAsync starts and Avalonia
            // disposes the transfer when the native drag session has fully completed.
            ResetDragState();
        }
    }

    private Task<IReadOnlyList<IStorageItem>>? StartDragStorageItemsResolution()
    {
        if (ViewModel == null || _dragStartEntry == null)
            return null;

        var paths = ViewModel.SelectedEntries.Count > 0
            ? ViewModel.SelectedEntries.Select(selected => selected.FullPath).ToArray()
            : [_dragStartEntry.FullPath];
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        return storageProvider == null
            ? null
            : ResolveStorageItemsAsync(storageProvider, paths);
    }

    private static DataTransfer CreateDragData(
        IReadOnlyList<IStorageItem> storageItems,
        FileSystemEntry? representativeEntry,
        int itemCount,
        Bitmap? visiblePreviewBitmap)
    {
        var data = new DataTransfer();
        var dragBitmap = representativeEntry == null
            ? null
            : CreateDragPreviewBitmap(representativeEntry, itemCount, visiblePreviewBitmap);

        for (var index = 0; index < storageItems.Count; index++)
        {
            var item = new DataTransferItem();
            item.SetFile(storageItems[index]);
            if (index == 0 && dragBitmap != null)
                item.SetBitmap(dragBitmap);
            data.Add(item);
        }

        return data;
    }

    private static Bitmap? CreateDragPreviewBitmap(FileSystemEntry entry, int itemCount, Bitmap? visiblePreviewBitmap)
    {
        try
        {
            var icon = LoadEntrySourceBitmap(entry) ?? visiblePreviewBitmap ?? (entry.IsDirectory
                ? SvgIconCache.GetFolderIcon(64)
                : SvgIconCache.GetFileIcon(entry.IconKey, entry.Extension, 64));
            return CreateDragPreviewBitmap(icon, Math.Max(1, itemCount));
        }
        catch
        {
            return null;
        }
    }

    private static string? CreateNativeDragPreviewFile(FileSystemEntry entry, int itemCount, Bitmap? visiblePreviewBitmap)
    {
        try
        {
            var preview = CreateDragPreviewBitmap(entry, itemCount, visiblePreviewBitmap);
            if (preview == null)
                return null;

            var path = Path.Combine(
                Path.GetTempPath(),
                $"mac-explorer-drag-{Guid.NewGuid():N}.png");
            preview.Save(path);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static Bitmap? GetVisibleEntryBitmap(Control control, FileSystemEntry entry)
    {
        return control.GetVisualDescendants()
            .OfType<Image>()
            .FirstOrDefault(image => image.Classes.Contains("entry-icon-image")
                                     && ReferenceEquals(image.DataContext, entry))
            ?.Source as Bitmap;
    }

    private static Bitmap? LoadEntrySourceBitmap(FileSystemEntry entry)
    {
        var source = !string.IsNullOrWhiteSpace(entry.ThumbnailUrl) ? entry.ThumbnailUrl : entry.IconUrl;
        if (string.IsNullOrWhiteSpace(source))
            return null;

        try
        {
            if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = source.IndexOf(',');
                if (comma < 0) return null;
                using var dataStream = new MemoryStream(Convert.FromBase64String(source[(comma + 1)..]));
                return new Bitmap(dataStream);
            }

            if (!File.Exists(source)) return null;
            using var fileStream = File.OpenRead(source);
            return new Bitmap(fileStream);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap CreateDragPreviewBitmap(Bitmap sourceIcon, int itemCount)
    {
        using var iconStream = new MemoryStream();
        sourceIcon.Save(iconStream);
        iconStream.Position = 0;
        using var iconBitmap = SKBitmap.Decode(iconStream);
        if (iconBitmap == null)
            throw new InvalidOperationException("Could not decode drag preview bitmap.");

        const int canvasSize = 96;
        using var surface = SKSurface.Create(new SKImageInfo(canvasSize, canvasSize, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using var shadowPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 44),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4)
        };
        var iconRect = FitInside(iconBitmap.Width, iconBitmap.Height, new SKRect(14, 10, 82, 78));
        var shadowRect = iconRect;
        shadowRect.Offset(1, 3);
        canvas.DrawBitmap(iconBitmap, shadowRect, shadowPaint);

        using var iconPaint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(iconBitmap, iconRect, iconPaint);

        if (itemCount > 1)
        {
            var badgeText = itemCount > 99 ? "99+" : itemCount.ToString();
            var badgeRect = new SKRect(56, 54, 88, 82);
            using var badgePaint = new SKPaint { Color = new SKColor(0, 122, 255), IsAntialias = true };
            canvas.DrawRoundRect(badgeRect, 14, 14, badgePaint);

            using var textPaint = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true
            };
            using var typeface = SKTypeface.FromFamilyName(
                FontManager.Current.DefaultFontFamily.Name,
                SKFontStyle.Bold);
            using var font = new SKFont(typeface, badgeText.Length > 2 ? 12 : 15);
            var metrics = font.Metrics;
            var baseline = badgeRect.MidY - (metrics.Ascent + metrics.Descent) / 2;
            canvas.DrawText(badgeText, badgeRect.MidX, baseline, SKTextAlign.Center, font, textPaint);
        }

        canvas.Flush();
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return new Bitmap(data.AsStream());
    }

    private static SKRect FitInside(int sourceWidth, int sourceHeight, SKRect bounds)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return bounds;

        var scale = Math.Min(bounds.Width / sourceWidth, bounds.Height / sourceHeight);
        var width = sourceWidth * scale;
        var height = sourceHeight * scale;
        var left = bounds.Left + (bounds.Width - width) / 2;
        var top = bounds.Top + (bounds.Height - height) / 2;
        return new SKRect(left, top, left + width, top + height);
    }

    private static async Task<IReadOnlyList<IStorageItem>> ResolveStorageItemsAsync(
        IStorageProvider storageProvider,
        IEnumerable<string> paths)
    {
        var result = new List<IStorageItem>();
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal))
        {
            try
            {
                var uri = new Uri(path, UriKind.Absolute);
                IStorageItem? storageItem = Directory.Exists(path)
                    ? await storageProvider.TryGetFolderFromPathAsync(uri)
                    : await storageProvider.TryGetFileFromPathAsync(uri);
                if (storageItem != null)
                    result.Add(storageItem);
            }
            catch
            {
            }
        }
        return result;
    }

    private static async Task DisposeStorageItemsWhenReadyAsync(Task<IReadOnlyList<IStorageItem>> task)
    {
        try
        {
            foreach (var storageItem in await task)
                storageItem.Dispose();
        }
        catch
        {
        }
    }

    private void OnItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // The native drag session receives the same mouse-up event. Keep its source data
        // alive until DoDragDropAsync completes instead of resetting it here.
        if (_dragStarted)
            return;

        if (_collapseSelectionOnRelease && !_dragStarted && _pressedEntry != null && ViewModel != null)
        {
            ViewModel.SelectEntry(_pressedEntry);
            QueueSelectionSynchronization();
        }
        ResetDragState();
    }

    private async void OnGlobalPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_rightPressedAnchor == null)
            return;

        var contextAnchor = _rightPressedAnchor;
        var hasSelection = _rightPressedEntry != null;
        RestoreRightClickSelection();
        _rightPressedAnchor = null;
        e.Handled = true;
        ResetDragState();
        await ShowMenuAsync(contextAnchor, hasSelection);
        // Keep the guard and snapshot until the next real pointer interaction.
        // This covers a virtualized ListBox callback that arrives after the
        // PointerReleased event and after the menu has been opened.
        Dispatcher.UIThread.Post(() =>
        {
            if (_contextMenuSelectionGuard)
                RestoreRightClickSelection();
        }, DispatcherPriority.Input);
    }

    private void ScheduleRightClickSelectionRestore()
    {
        if (!_contextMenuSelectionGuard || _rightPressedEntry == null)
            return;

        var contextEntry = _rightPressedEntry;
        Dispatcher.UIThread.Post(() =>
        {
            // The pointer may already have been released or replaced by another
            // context click. Only restore the snapshot belonging to this press.
            if (!ReferenceEquals(_rightPressedEntry, contextEntry))
                return;
            RestoreRightClickSelection();
        }, DispatcherPriority.Input);
    }

    private void RestoreRightClickSelection()
    {
        if (ViewModel == null || _rightClickSelectionSnapshot is not { } snapshot)
            return;

        var currentPaths = ViewModel.SelectedEntries
            .Select(entry => entry.FullPath)
            .ToArray();
        var snapshotPaths = snapshot
            .Select(entry => entry.FullPath)
            .ToArray();
        if (!currentPaths.SequenceEqual(snapshotPaths, StringComparer.Ordinal))
            ViewModel.SetSelection(snapshot, snapshot.LastOrDefault());
        QueueSelectionSynchronization();
    }

    private void ResetDragState()
    {
        var pendingStorageItems = _dragStorageItemsTask;
        _dragStartPoint = null;
        _dragStartEntry = null;
        _dragPointerEvent = null;
        _dragStorageItemsTask = null;
        _dragStartPreviewBitmap = null;
        _dragStarted = false;
        _collapseSelectionOnRelease = false;
        _pressedEntry = null;
        if (pendingStorageItems != null)
            _ = DisposeStorageItemsWhenReadyAsync(pendingStorageItems);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var paths = GetDroppedPaths(e.DataTransfer);
        if (paths.Length == 0)
        {
            ClearDragOverVisual();
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var target = FindDropTarget(e);
        _dragOverTargetEntry = target;
        var targetDirectory = target?.FullPath ?? ViewModel?.CurrentPath;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Alt)
            && !string.IsNullOrWhiteSpace(targetDirectory)
            && paths.All(path => IsSameDestination(path, targetDirectory)))
        {
            ClearDragOverVisual();
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (target != null && paths.Any(path => IsSamePath(path, target.FullPath)))
        {
            ClearDragOverVisual();
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        SetDragOverVisual(target == null ? null : FindEntryContent(e.Source as Visual));

        e.DragEffects = e.KeyModifiers.HasFlag(KeyModifiers.Alt)
            ? DragDropEffects.Copy
            : DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        _dragOverTargetEntry = null;
        ClearDragOverVisual();
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var target = FindDropTarget(e) ?? _dragOverTargetEntry;
        _dragOverTargetEntry = null;
        ClearDragOverVisual();
        var viewModel = ViewModel;
        if (viewModel == null) return;
        try
        {
            var paths = GetDroppedPaths(e.DataTransfer);
            if (paths.Length == 0) return;

            var targetDirectory = target?.FullPath ?? viewModel.CurrentPath;
            if (!VirtualPath.IsRemotePath(targetDirectory) && !Directory.Exists(targetDirectory)) return;
            if (target != null && paths.Any(path => IsSamePath(path, target.FullPath)))
            {
                e.Handled = true;
                return;
            }

            var forceCopy = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            e.Handled = true;

            // Remote targets always copy (never move)
            var isRemoteTarget = VirtualPath.IsRemotePath(targetDirectory);
            var hasRemoteSource = paths.Any(p => VirtualPath.IsRemotePath(p));
            if (!forceCopy && !isRemoteTarget && !hasRemoteSource)
                paths = paths.Where(path => !IsSameDestination(path, targetDirectory)).ToArray();
            if (paths.Length == 0)
                return;

            // Use copy for remote targets or remote sources
            if (forceCopy || isRemoteTarget || hasRemoteSource)
            {
                var bridge = App.Services.GetRequiredService<IDragDropService>();
                await bridge.DropFilesAsync(
                    paths,
                    targetDirectory,
                    forceCopy: true,
                    forceMove: false);
                return;
            }

            var fileService = App.Services.GetRequiredService<IFileService>();
            var lookedUp = await Task.WhenAll(
                paths.Distinct(StringComparer.Ordinal).Select(fileService.GetEntryAsync));
            var sourceEntries = lookedUp.OfType<FileSystemEntry>().ToList();
            if (sourceEntries.Count == 0) return;

            var targetEntry = target ?? await fileService.GetEntryAsync(targetDirectory)
                ?? new FileSystemEntry
                {
                    FullPath = targetDirectory,
                    Name = Path.GetFileName(targetDirectory),
                    IsDirectory = true
                };

            await viewModel.MoveEntriesAsync(sourceEntries, targetEntry);
        }
        catch (Exception ex)
        {
            viewModel.StatusText = $"拖放失败: {ex.Message}";
        }
    }

    private FileSystemEntry? FindDropTarget(DragEventArgs e)
    {
        var sourceTarget = FindDropTarget(e.Source as Visual);
        if (sourceTarget != null)
            return sourceTarget;

        var hit = FileScroll.InputHitTest(e.GetPosition(FileScroll));
        return hit is Visual visual ? FindDropTarget(visual) : null;
    }

    private static bool IsSameDestination(string sourcePath, string targetDirectory)
    {
        try
        {
            var destinationPath = Path.Combine(targetDirectory, Path.GetFileName(sourcePath));
            return IsSamePath(sourcePath, destinationPath);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static Control? FindEntryContent(Visual? visual)
    {
        for (; visual != null; visual = visual.GetVisualParent())
            if (visual is Control control && control.Classes.Contains("entry-content"))
                return control;
        return null;
    }

    private void SetDragOverVisual(Control? control)
    {
        if (ReferenceEquals(_dragOverVisual, control)) return;
        ClearDragOverVisual();
        _dragOverVisual = control;
        _dragOverVisual?.Classes.Add("drag-over");
    }

    private void ClearDragOverVisual()
    {
        _dragOverVisual?.Classes.Remove("drag-over");
        _dragOverVisual = null;
    }

    private static FileSystemEntry? FindDropTarget(Visual? visual)
    {
        for (; visual != null; visual = visual.GetVisualParent())
            if (visual is Control { DataContext: FileSystemEntry { IsDirectory: true } entry })
                return entry;
        return null;
    }

    private static string[] GetDroppedPaths(IDataTransfer data)
    {
        return data.TryGetFiles()?
            .Select(item => item.Path.LocalPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray() ?? [];
    }

    private void OnEmptyAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel == null) return;
        CancelSlowRename();

        var sourceVisual = e.Source as Visual;
        if (!IsWithinVisual(sourceVisual, FileScroll))
            return;
        // The list intentionally leaves the transparent remainder of a row as
        // marquee canvas for left-button drags. A secondary click is different:
        // Finder treats any point inside that row as the row's context target.
        // Let OnDismissClick keep the existing multi-selection instead of letting
        // this canvas handler turn a right-click on row whitespace into a blank
        // background click.
        if (e.GetCurrentPoint(FileScroll).Properties.IsRightButtonPressed
            && FindDataContextInAncestors(sourceVisual) is FileSystemEntry)
            return;
        // A ListBoxItem owns the complete layout cell, including the transparent
        // spacing around an icon. Finder treats that spacing as canvas: only the
        // visible entry card starts an item click/drag; its surrounding gap starts
        // a marquee gesture.
        if (FindEntryContentInAncestors(sourceVisual) is not null)
            return;

        Focus();
        var point = e.GetCurrentPoint(FileScroll);
        if (point.Properties.IsLeftButtonPressed)
        {
            // Nested ListBoxes can emit delayed SelectionChanged events while
            // their visual selection is being cleared. Those events describe the
            // previous selection and must not feed it back into the shared model
            // during the new rubber-band gesture.
            _suppressControlSelectionDuringMarquee = true;
            _rightPressedEntry = null;
            _rightPressedAnchor = null;
            _marqueeScrollViewer = GetActiveFileScrollViewer();
            _marqueeCurrentViewportPoint = e.GetPosition(FileScroll);
            _marqueeStart = ViewportToContent(_marqueeCurrentViewportPoint);
            _marqueeModifiers = e.KeyModifiers;
            _marqueeBaseSelection = ViewModel.SelectedEntries.ToHashSet();
            _marqueeEntryBounds.Clear();
            CaptureRealizedEntryBounds();
            if (!HasAdditiveSelectionModifier(_marqueeModifiers))
                ViewModel.ClearSelection();
            DismissContextMenu();
            e.Pointer.Capture(this);
            e.Handled = true;
        }
        else if (point.Properties.IsRightButtonPressed)
        {
            _rightPressedEntry = null;
            _rightPressedAnchor = FileScroll;
            _contextMenuSelectionGuard = true;
            _rightClickSelectionSnapshot = [];
            ViewModel.ClearSelection();
            e.Handled = true;
        }
    }

    private void OnMarqueePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_marqueeStart == null || ViewModel == null) return;

        // The pointer is captured by FileListView from the left-button press.
        // On macOS, subsequent captured PointerMoved events can legitimately
        // arrive without IsLeftButtonPressed set (the native event's button mask
        // is not repeated after capture). Treating that as a release makes a
        // drag started in the Name-column whitespace look like a click. The
        // matching PointerReleased handler is the authoritative end of capture.

        _marqueeCurrentViewportPoint = e.GetPosition(FileScroll);
        var clampedViewport = ClampToFileArea(_marqueeCurrentViewportPoint);
        var current = ViewportToContent(clampedViewport);
        var start = _marqueeStart.Value;
        if (!_marqueeActive && Math.Abs(current.X - start.X) < 4 && Math.Abs(current.Y - start.Y) < 4)
            return;

        _marqueeActive = true;
        UpdateMarqueeSelection(current);
        UpdateMarqueeScrollTimer();
        e.Handled = true;
    }

    private void OnMarqueePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_marqueeStart == null) return;
        EndMarquee(e.Pointer);
        e.Handled = true;
    }

    private void EndMarquee(IPointer pointer)
    {
        pointer.Capture(null);
        _marqueeStart = null;
        _marqueeActive = false;
        _marqueeBaseSelection.Clear();
        _marqueeEntryBounds.Clear();
        _marqueeScrollViewer = null;
        _marqueeScrollTimer.Stop();
        SelectionMarquee.IsVisible = false;
        // Keep suppressing delayed nested-ListBox callbacks until the next real
        // pointer interaction. Some recycled containers report their old
        // selection after ApplicationIdle; releasing the guard too early would
        // re-add those stale entries to the final marquee result.
    }

    private static bool HasAdditiveSelectionModifier(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Meta) || modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Shift);

    private Point ClampToFileArea(Point point) => new(
        Math.Clamp(point.X, 0, Math.Max(0, FileScroll.Bounds.Width)),
        Math.Clamp(point.Y, 0, Math.Max(0, FileScroll.Bounds.Height)));

    private static Rect RectFromPoints(Point first, Point second) => new(
        Math.Min(first.X, second.X), Math.Min(first.Y, second.Y),
        Math.Abs(second.X - first.X), Math.Abs(second.Y - first.Y));

    private void CaptureRealizedEntryBounds()
    {
        var offset = _marqueeScrollViewer?.Offset ?? default;
        var realizedBounds = new Dictionary<FileSystemEntry, List<Rect>>();
        foreach (var control in FileScroll.GetVisualDescendants().OfType<Control>()
                     .Where(control => control.DataContext is FileSystemEntry
                                       && control.Classes.Contains("entry-content")
                                       && IsInActivePresentation(control)))
        {
            var origin = control.TranslatePoint(default, FileScroll);
            if (origin is { } point && control.DataContext is FileSystemEntry entry)
            {
                var bounds = new Rect(
                    new Point(point.X + offset.X, point.Y + offset.Y),
                    control.Bounds.Size);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    continue;
                if (!realizedBounds.TryGetValue(entry, out var entryBounds))
                    realizedBounds[entry] = entryBounds = [];
                entryBounds.Add(bounds);
            }
        }

        // Replace rather than append: virtualized controls are recycled while
        // scrolling, so their translated rectangles must follow the current item.
        foreach (var (entry, bounds) in realizedBounds)
            _marqueeEntryBounds[entry] = bounds;
        var realizedEntries = realizedBounds.Keys.ToHashSet();

        // Virtualization may provide data items before it materializes their visual
        // templates (especially on the first drag after navigation). Finder list rows
        // are uniform, so fill any missing logical bounds from the list index instead
        // of treating the gesture as a background click with zero hits.
        if (FileItemsList.IsVisible && ViewModel != null
            && FileItemsList.TranslatePoint(default, FileScroll) is { } listOrigin)
        {
            var entryIndexes = ViewModel.Entries
                .Select((entry, index) => (entry, index))
                .ToDictionary(item => item.entry, item => item.index);
            var realizedRows = FileItemsList.GetVisualDescendants()
                .OfType<ListBoxItem>()
                .Select(row =>
                {
                    var origin = row.TranslatePoint(default, FileScroll);
                    return (row, origin);
                })
                .Where(item => item.origin is not null
                               && item.row.DataContext is FileSystemEntry entry
                               && entryIndexes.ContainsKey(entry))
                .Select(item =>
                {
                    var entry = (FileSystemEntry)item.row.DataContext!;
                    return (entry, index: entryIndexes[entry],
                        top: item.origin!.Value.Y + offset.Y,
                        height: item.row.Bounds.Height);
                })
                .OrderBy(item => item.index)
                .ToArray();

            // Derive the pitch from the actual virtualized ListBoxItem positions.
            // The inner hit targets are shorter than a row and are not suitable
            // for this measurement; using their height caused cumulative drift at
            // the bottom of large folders.
            var measuredPitches = realizedRows
                .Zip(realizedRows.Skip(1), (first, second) =>
                    (second.top - first.top) / Math.Max(1, second.index - first.index))
                .Where(pitch => pitch > 0.1)
                .OrderBy(pitch => pitch)
                .ToArray();
            var rowHeight = measuredPitches.Length > 0
                ? measuredPitches[measuredPitches.Length / 2]
                : realizedRows.Select(row => row.height).Where(height => height > 0)
                    .DefaultIfEmpty(30).Max() + 2;
            rowHeight = Math.Max(1, rowHeight);

            var logicalOriginY = realizedRows.Length > 0
                ? realizedRows.Average(row => row.top - row.index * rowHeight)
                : listOrigin.Y + offset.Y;
            var fallbackX = listOrigin.X + 12;
            var fallbackWidth = 22 + Math.Max(0, _effectiveColumnWidths[FileListColumn.Name]);
            for (var index = 0; index < ViewModel.Entries.Count; index++)
            {
                var entry = ViewModel.Entries[index];
                // Rebuild every row on each pass. A recycled row may still expose
                // its previous visual bounds for one layout turn after scrolling;
                // the logical row model is authoritative for marquee selection.
                _marqueeEntryBounds[entry] = [new Rect(
                    fallbackX + offset.X,
                    logicalOriginY + index * rowHeight,
                    fallbackWidth,
                    rowHeight)];
            }
        }

        if (GridViewItems.IsVisible && ViewModel != null
            && GridViewItems.TranslatePoint(default, FileScroll) is { } gridOrigin)
        {
            const double cellWidth = 120;
            const double cellHeight = 116;
            const double cardInsetX = 10;
            const double cardInsetY = 6;
            const double cardWidth = 100;
            const double cardHeight = 104;
            var columns = Math.Max(1, _gridColumnCount > 0 ? _gridColumnCount : CalculateGridColumnCount());
            if (ViewModel.GroupField == GroupField.None)
            {
                for (var index = 0; index < ViewModel.Entries.Count; index++)
                {
                    var entry = ViewModel.Entries[index];
                    if (!realizedEntries.Contains(entry))
                        _marqueeEntryBounds[entry] = [new Rect(
                            gridOrigin.X + offset.X + index % columns * cellWidth + cardInsetX,
                            gridOrigin.Y + offset.Y + index / columns * cellHeight + cardInsetY,
                            cardWidth,
                            cardHeight)];
                }
            }
            else
            {
                var y = gridOrigin.Y;
                foreach (var row in _gridRows)
                {
                    if (row.IsGroupHeader)
                    {
                        y += 32;
                        continue;
                    }
                    for (var column = 0; column < row.Entries.Count; column++)
                    {
                        var entry = row.Entries[column];
                        if (!realizedEntries.Contains(entry))
                            _marqueeEntryBounds[entry] = [new Rect(
                                gridOrigin.X + offset.X + column * cellWidth + cardInsetX,
                                y + offset.Y + cardInsetY,
                                cardWidth,
                                cardHeight)];
                    }
                    y += cellHeight;
                }
            }
        }

        if (GroupedListItems.IsVisible
            && GroupedListItems.TranslatePoint(default, FileScroll) is { } groupedOrigin)
        {
            var y = groupedOrigin.Y;
            foreach (var row in _groupedListRows)
            {
                if (row.IsGroupHeader)
                {
                    y += 32;
                    continue;
                }
                if (row.Entry is { } entry && !realizedEntries.Contains(entry))
                    _marqueeEntryBounds[entry] = [new Rect(
                        groupedOrigin.X + offset.X + 12,
                        y + offset.Y,
                        22 + Math.Max(0, _effectiveColumnWidths[FileListColumn.Name]),
                        30)];
                y += 30;
            }
        }
    }

    private ScrollViewer? GetActiveFileScrollViewer() => FileScroll.GetVisualDescendants()
        .OfType<ScrollViewer>()
        .FirstOrDefault(viewer => viewer.IsVisible);

    /// <summary>
    /// Hidden presentation hosts remain in Avalonia's visual tree when the user
    /// switches between list and icon modes. Their recycled item containers still
    /// have coordinates, but must never participate in the current marquee hit
    /// test. Walk up to the presentation host instead of relying on the child's
    /// local IsVisible value (which can remain true under a hidden ancestor).
    /// </summary>
    private bool IsInActivePresentation(Visual visual)
    {
        for (Visual? current = visual; current != null && !ReferenceEquals(current, FileScroll); current = current.GetVisualParent())
        {
            if (ReferenceEquals(current, FileItemsList))
                return FileItemsList.IsVisible;
            if (ReferenceEquals(current, GroupedListItems))
                return GroupedListItems.IsVisible;
            if (ReferenceEquals(current, GridViewItems))
                return GridViewItems.IsVisible;
        }

        return false;
    }

    private bool IsListMarqueePresentation =>
        FileItemsList.IsVisible || GroupedListItems.IsVisible;

    private Point ViewportToContent(Point point)
    {
        var offset = _marqueeScrollViewer?.Offset ?? default;
        return new Point(point.X + offset.X, point.Y + offset.Y);
    }

    private void UpdateMarqueeSelection(Point currentContentPoint)
    {
        if (_marqueeStart == null || ViewModel == null) return;
        CaptureRealizedEntryBounds();
        var rectangle = RectFromPoints(_marqueeStart.Value, currentContentPoint);
        var offset = _marqueeScrollViewer?.Offset ?? default;
        var visibleContent = new Rect(offset.X, offset.Y, FileScroll.Bounds.Width, FileScroll.Bounds.Height);
        var visibleRectangle = IntersectRects(rectangle, visibleContent);
        if (visibleRectangle.Width > 0 && visibleRectangle.Height > 0)
        {
            SelectionMarquee.Margin = new Thickness(
                visibleRectangle.X - offset.X,
                visibleRectangle.Y - offset.Y,
                0, 0);
            SelectionMarquee.Width = visibleRectangle.Width;
            SelectionMarquee.Height = visibleRectangle.Height;
            SelectionMarquee.IsVisible = true;
        }
        else
        {
            SelectionMarquee.IsVisible = false;
        }

        // Finder's list view treats the row as the selectable unit: a drag may
        // start in the blank part of the Name column and still select rows by
        // their vertical centers. Icon view uses the center of a visible card,
        // so grazing an adjacent card at an edge does not select it.
        var hits = _marqueeEntryBounds
            .Where(item => IsListMarqueePresentation
                ? item.Value.Any(bounds => rectangle.Top <= bounds.Center.Y
                                           && bounds.Center.Y <= rectangle.Bottom)
                : item.Value.Any(bounds => rectangle.Contains(bounds.Center)))
            .Select(item => item.Key)
            .ToHashSet();
        IEnumerable<FileSystemEntry> selection;
        var command = _marqueeModifiers.HasFlag(KeyModifiers.Meta) || _marqueeModifiers.HasFlag(KeyModifiers.Control);
        if (command)
            selection = _marqueeBaseSelection.Where(entry => !hits.Contains(entry))
                .Concat(hits.Where(entry => !_marqueeBaseSelection.Contains(entry)));
        else if (_marqueeModifiers.HasFlag(KeyModifiers.Shift))
            selection = _marqueeBaseSelection.Concat(hits);
        else
            selection = hits;
        ViewModel.SetSelection(selection);
        QueueSelectionSynchronization();
    }

    private static Rect IntersectRects(Rect first, Rect second)
    {
        var left = Math.Max(first.Left, second.Left);
        var top = Math.Max(first.Top, second.Top);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);
        return right > left && bottom > top ? new Rect(left, top, right - left, bottom - top) : default;
    }

    private void UpdateMarqueeScrollTimer()
    {
        const double edge = 36;
        var shouldScroll = _marqueeCurrentViewportPoint.Y < edge
                           || _marqueeCurrentViewportPoint.Y > FileScroll.Bounds.Height - edge;
        if (shouldScroll && _marqueeActive)
            _marqueeScrollTimer.Start();
        else
            _marqueeScrollTimer.Stop();
    }

    private void OnMarqueeScrollTick()
    {
        if (!_marqueeActive || _marqueeStart == null || _marqueeScrollViewer == null)
        {
            _marqueeScrollTimer.Stop();
            return;
        }

        const double edge = 36;
        var distance = _marqueeCurrentViewportPoint.Y < edge
            ? _marqueeCurrentViewportPoint.Y - edge
            : _marqueeCurrentViewportPoint.Y > FileScroll.Bounds.Height - edge
                ? _marqueeCurrentViewportPoint.Y - (FileScroll.Bounds.Height - edge)
                : 0;
        if (distance == 0)
        {
            _marqueeScrollTimer.Stop();
            return;
        }

        var speed = Math.Clamp(Math.Abs(distance) * 0.35, 4, 28) * Math.Sign(distance);
        var maxOffset = Math.Max(0, _marqueeScrollViewer.Extent.Height - _marqueeScrollViewer.Viewport.Height);
        var nextY = Math.Clamp(_marqueeScrollViewer.Offset.Y + speed, 0, maxOffset);
        if (Math.Abs(nextY - _marqueeScrollViewer.Offset.Y) < 0.01)
        {
            _marqueeScrollTimer.Stop();
            return;
        }

        _marqueeScrollViewer.Offset = new Vector(_marqueeScrollViewer.Offset.X, nextY);
        Dispatcher.UIThread.Post(() =>
        {
            if (!_marqueeActive) return;
            var current = ViewportToContent(ClampToFileArea(_marqueeCurrentViewportPoint));
            UpdateMarqueeSelection(current);
        }, DispatcherPriority.Render);
    }

    private static object? FindDataContextInAncestors(Visual? visual)
    {
        for (; visual != null; visual = visual.GetVisualParent())
            if (visual is Control { DataContext: FileSystemEntry entry })
                return entry;
        return null;
    }

    internal static FileSystemEntry? FindEntryContentInAncestors(Visual? visual)
    {
        for (; visual != null; visual = visual.GetVisualParent())
            if (visual is Control { DataContext: FileSystemEntry entry } control
                && control.Classes.Contains("entry-content"))
                return entry;
        return null;
    }

    private void OnControlSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_contextMenuSelectionGuard)
        {
            // A ListBox may still raise SelectionChanged after the tunnel handler
            // consumed the secondary-click press. Never feed that transient,
            // single-item control state back into the shared multi-selection.
            RestoreRightClickSelection();
            e.Handled = true;
            return;
        }

        if (_syncingSelection || _suppressControlSelectionDuringMarquee
            || _marqueeStart != null
            || sender is not ListBox listBox || ViewModel == null)
            return;

        var selected = listBox.SelectedItems?.OfType<FileSystemEntry>().ToList() ?? [];
        if (selected.Count == 0
            && ViewModel.SelectedEntries.Count > 0
            && (_applyingViewModelEntries
                || _restoringNavigationSelection
                || DateTime.UtcNow <= _ignoreEmptySelectionUntilUtc
                || ViewModel.ScrollBehaviorAfterLoad is FileListViewModel.ScrollMode.RestoreNavigation
                    or FileListViewModel.ScrollMode.ScrollToSelected))
        {
            e.Handled = true;
            return;
        }

        ViewModel.SetSelection(selected, selected.LastOrDefault());
    }

    private void OnPresentationRowSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_clearingPresentationSelection || sender is not ListBox listBox || listBox.SelectedIndex < 0)
            return;

        _clearingPresentationSelection = true;
        try
        {
            listBox.SelectedIndex = -1;
            e.Handled = true;
        }
        finally
        {
            _clearingPresentationSelection = false;
        }
    }


    private void SynchronizeSelectionControls()
    {
        if (ViewModel == null || _syncingSelection) return;
        _syncingSelection = true;
        try
        {
            var selectedPaths = ViewModel.SelectedEntries.Select(entry => entry.FullPath).ToHashSet(StringComparer.Ordinal);
            if (selectedPaths.Count == 0)
            {
                // Batched loads re-run this per batch; with nothing selected just drop
                // stale control selections instead of scanning every item.
                foreach (var listBox in GetActiveSelectionLists())
                {
                    if (listBox.SelectedItems is { Count: > 0 })
                        listBox.SelectedItems.Clear();
                }
                return;
            }
            foreach (var listBox in GetActiveSelectionLists())
            {
                if (listBox.SelectedItems == null) continue;
                var desiredItems = listBox.Items
                    .OfType<FileSystemEntry>()
                    .Where(entry => selectedPaths.Contains(entry.FullPath))
                    .ToList();
                var currentItems = listBox.SelectedItems.OfType<FileSystemEntry>().ToList();
                if (currentItems.Count == desiredItems.Count
                    && currentItems.SequenceEqual(desiredItems))
                {
                    continue;
                }

                listBox.SelectedItems.Clear();
                foreach (var entry in desiredItems)
                    listBox.SelectedItems.Add(entry);
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void QueueSelectionSynchronization()
    {
        if (_selectionSyncQueued)
            return;

        _selectionSyncQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _selectionSyncQueued = false;
            if (_restoringNavigationSelection
                || ViewModel?.ScrollBehaviorAfterLoad == FileListViewModel.ScrollMode.RestoreNavigation)
                return;

            SynchronizeSelectionControls();
        }, DispatcherPriority.Background);
    }

    private IEnumerable<ListBox> GetActiveSelectionLists()
    {
        if (FileItemsList.IsVisible)
        {
            foreach (var list in GetSelectionListsWithin(FileItemsList))
                yield return list;
        }
        if (GroupedListItems.IsVisible)
        {
            foreach (var list in GetSelectionListsWithin(GroupedListItems))
                yield return list;
        }
        if (GridViewItems.IsVisible)
        {
            foreach (var list in GetSelectionListsWithin(GridViewItems))
                yield return list;
        }
    }

    private static IEnumerable<ListBox> GetSelectionListsWithin(ListBox root)
    {
        yield return root;
        foreach (var list in root.GetVisualDescendants().OfType<ListBox>())
            yield return list;
    }

    private void UpdateViewMode()
    {
        if (ViewModel == null) return;
        var isGrid = ViewModel.ViewMode == ViewMode.Grid;
        var isGrouped = ViewModel.GroupField != GroupField.None;
        FileItemsList.IsVisible = !isGrid && !isGrouped;
        GroupedListItems.IsVisible = !isGrid && isGrouped;
        GridViewItems.IsVisible = isGrid;
        ListHeaderPanel.IsVisible = !isGrid;
        UpdateSortHeaders();
    }

    private void RebuildPresentationRows()
    {
        if (ViewModel == null) return;

        _groupedListRows.Clear();
        _groupedRowByPath.Clear();
        // Presentation rows are only materialized while their host is on screen;
        // switching view/group modes re-runs this after UpdateViewMode flipped
        // visibility, so off-screen row sets stay empty.
        if (GroupedListItems.IsVisible && ViewModel.GroupField != GroupField.None)
        {
            foreach (var group in ViewModel.Groups)
            {
                _groupedListRows.Add(new FileListPresentationRow
                {
                    GroupName = group.Name,
                    GroupItemCount = group.Entries.Count
                });
                foreach (var entry in group.Entries)
                {
                    var row = new FileListPresentationRow { Entry = entry };
                    _groupedListRows.Add(row);
                    _groupedRowByPath[entry.FullPath] = row;
                }
            }
        }

        if (GridViewItems.IsVisible)
            RebuildGridRows(CalculateGridColumnCount());
    }

    private int CalculateGridColumnCount()
        => Math.Max(1, (int)Math.Floor(Math.Max(120, Bounds.Width - 16) / 120));

    private void RebuildGridRowsIfColumnCountChanged()
    {
        if (!GridViewItems.IsVisible) return;
        var columns = CalculateGridColumnCount();
        if (columns == _gridColumnCount) return;
        RebuildGridRows(columns);
    }

    private void RebuildGridRows(int columns)
    {
        if (ViewModel == null) return;
        _gridColumnCount = columns;
        _gridRows.Clear();
        _gridRowByPath.Clear();

        IEnumerable<(string? Header, IReadOnlyList<FileSystemEntry> Entries)> groups =
            ViewModel.GroupField == GroupField.None
                ? [(null, ViewModel.Entries)]
                : ViewModel.Groups.Select(group => ((string?)group.Name, (IReadOnlyList<FileSystemEntry>)group.Entries));
        foreach (var (header, entries) in groups)
        {
            if (header != null)
                _gridRows.Add(new FileGridPresentationRow
                {
                    GroupName = header,
                    GroupItemCount = entries.Count
                });
            for (var i = 0; i < entries.Count; i += columns)
            {
                var take = Math.Min(columns, entries.Count - i);
                var slice = new FileSystemEntry[take];
                for (var j = 0; j < take; j++)
                    slice[j] = entries[i + j];
                var row = new FileGridPresentationRow
                {
                    Entries = slice
                };
                _gridRows.Add(row);
                foreach (var entry in slice)
                    _gridRowByPath[entry.FullPath] = row;
            }
        }
    }

    private void UpdateSortHeaders()
    {
        if (ViewModel == null) return;
        NameHeader.Text = GetHeaderText("名称", SortField.Name);
        ModifiedHeader.Text = GetHeaderText("修改日期", SortField.Modified);
        SizeHeader.Text = GetHeaderText("大小", SortField.Size);
        TypeHeader.Text = GetHeaderText("类型", SortField.Type);
    }

    private string GetHeaderText(string label, SortField field)
        => ViewModel?.SortField == field ? $"{label} {(ViewModel.SortAscending ? "▲" : "▼")}" : label;

    private void UpdateEmptyState()
    {
        if (ViewModel == null) return;
        EmptyState.IsVisible = !ViewModel.IsLoading && ViewModel.Entries.Count == 0;
        EmptyStateText.Text = ViewModel.IsSearchMode ? "没有找到匹配的项目" : "此文件夹为空";
    }

    private void OnSortByName(object? sender, PointerPressedEventArgs e) => SetSortFromHeader(SortField.Name, e);
    private void OnSortByModified(object? sender, PointerPressedEventArgs e) => SetSortFromHeader(SortField.Modified, e);
    private void OnSortBySize(object? sender, PointerPressedEventArgs e) => SetSortFromHeader(SortField.Size, e);
    private void OnSortByType(object? sender, PointerPressedEventArgs e) => SetSortFromHeader(SortField.Type, e);

    private void SetSortFromHeader(SortField field, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        ViewModel?.SetSort(field);
        e.Handled = true;
    }

    private void OnFileItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: FileSystemEntry entry } || ViewModel == null) return;
        CancelSlowRename();
        if (entry.IsDirectory)
        {
            ViewModel.SetSelection([entry], entry);
            _ = ViewModel.NavigateToAsync(entry.FullPath);
        }
        else _ = ViewModel.OpenEntryAsync(entry);
        e.Handled = true;
    }

    private void OnFileListKeyDown(object? sender, KeyEventArgs e)
    {
        // EndMarquee intentionally holds this guard through one idle turn to
        // absorb stale virtualized ListBox callbacks. A keyboard gesture is a
        // new, intentional selection interaction and must release that guard;
        // otherwise Arrow/Shift+Arrow selection would remain disabled until the
        // user clicked an item again.
        if (!IsTextInputSource(e.Source))
            _suppressControlSelectionDuringMarquee = false;
        TryHandleFileShortcut(e);
    }

    public bool TryHandleFileShortcut(KeyEventArgs e)
    {
        if (ViewModel == null) return false;
        if (e.Handled || IsTextInputSource(e.Source)) return false;

        var commandModifier = e.KeyModifiers.HasFlag(KeyModifiers.Meta)
                              || e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (commandModifier
            && e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            && !e.KeyModifiers.HasFlag(KeyModifiers.Alt)
            && e.Key == Key.C)
        {
            DismissContextMenu();
            _ = ViewModel.CopyPathAsync();
            e.Handled = true;
            return true;
        }

        if (commandModifier && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            switch (e.Key)
            {
                case Key.C:
                    DismissContextMenu();
                    if (!ViewModel.IsArchiveView && ViewModel.SelectedEntries.Count > 0)
                        ViewModel.CopySelected();
                    e.Handled = true;
                    return true;
                case Key.X:
                    DismissContextMenu();
                    if (!ViewModel.IsArchiveView && ViewModel.SelectedEntries.Count > 0)
                        ViewModel.CutSelected();
                    e.Handled = true;
                    return true;
                case Key.V:
                    DismissContextMenu();
                    if (!ViewModel.IsArchiveView)
                        _ = ViewModel.PasteAsync();
                    e.Handled = true;
                    return true;
                case Key.A:
                    DismissContextMenu();
                    ViewModel.SelectAll();
                    e.Handled = true;
                    return true;
                case Key.O when ViewModel.SelectedEntries.Count == 1:
                    DismissContextMenu();
                    _ = ViewModel.OpenEntryAsync(ViewModel.SelectedEntries[0]);
                    e.Handled = true;
                    return true;
                case Key.R:
                    DismissContextMenu();
                    _ = ViewModel.RefreshAsync();
                    e.Handled = true;
                    return true;
                case Key.I when ViewModel.SelectedEntries.Count == 1:
                    DismissContextMenu();
                    _ = ViewModel.ShowMetadataAsync(ViewModel.SelectedEntries[0]);
                    e.Handled = true;
                    return true;
                case Key.N:
                    DismissContextMenu();
                    if (!ViewModel.IsArchiveView)
                    {
                        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                            _ = ViewModel.CreateNewFolderAsync();
                        else
                            _ = ViewModel.CreateNewFileAsync(".txt");
                    }
                    e.Handled = true;
                    return true;
                case Key.P when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    DismissContextMenu();
                    ViewModel.TogglePreviewPane();
                    e.Handled = true;
                    return true;
                case Key.H:
                    DismissContextMenu();
                    ViewModel.GoHome();
                    e.Handled = true;
                    return true;
                case Key.Up:
                    DismissContextMenu();
                    _ = ViewModel.NavigateUpAsync();
                    e.Handled = true;
                    return true;
                case Key.OemOpenBrackets when ViewModel.CanGoBack:
                    DismissContextMenu();
                    _ = ViewModel.NavigateBackAsync();
                    e.Handled = true;
                    return true;
                case Key.OemCloseBrackets when ViewModel.CanGoForward:
                    DismissContextMenu();
                    _ = ViewModel.NavigateForwardAsync();
                    e.Handled = true;
                    return true;
                case Key.Back:
                    DismissContextMenu();
                    if (!ViewModel.IsArchiveView)
                        ViewModel.ShowDeleteConfirmDialog();
                    e.Handled = true;
                    return true;
            }
        }

        if (e.Key == Key.Delete)
        {
            DismissContextMenu();
            if (!ViewModel.IsArchiveView)
                ViewModel.ShowDeleteConfirmDialog();
            e.Handled = true;
            return true;
        }

        switch (e.Key)
        {
            case Key.Space:
                _ = ViewModel.QuickLookSelectedAsync();
                e.Handled = true;
                break;
            case Key.Enter when ViewModel.SelectedEntries.Count == 1:
                DismissContextMenu();
                if (ViewModel.IsArchiveView)
                    _ = ViewModel.OpenEntryAsync(ViewModel.SelectedEntries[0]);
                else
                    ViewModel.RequestRename(ViewModel.SelectedEntries[0]);
                e.Handled = true;
                break;
            case Key.Escape:
                DismissContextMenu();
                e.Handled = true;
                break;
            case Key.Back when !ViewModel.IsArchiveView:
                _ = ViewModel.NavigateUpAsync();
                e.Handled = true;
                break;
            case Key.Right when e.KeyModifiers.HasFlag(KeyModifiers.Meta) && ViewModel.CanGoForward:
                _ = ViewModel.NavigateForwardAsync();
                e.Handled = true;
                break;
        }

        return e.Handled;
    }

    private static bool IsTextInputSource(object? source)
    {
        if (source is TextBox) return true;
        return source is Visual visual && visual.FindAncestorOfType<TextBox>() != null;
    }
}
