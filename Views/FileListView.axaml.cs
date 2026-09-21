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

namespace MacExplorer.Views;

public partial class FileListView : UserControl
{
    private static readonly ByteLruCache MenuIconCache = new(8L * 1024 * 1024);
    private static readonly BitmapLruCache EntryImageCache = new(96L * 1024 * 1024);

    // Icon bindings re-evaluate on any entry property change, so converters must never
    // touch the disk; they only serve cache hits populated by the async loader below.
    internal static Bitmap? TryGetCachedEntryImage(string source)
        => EntryImageCache.TryGet(source, out var bitmap) ? bitmap : null;
    private static readonly SemaphoreSlim EntryImageLoadGate = new(4);
    private FileListColumnWidths? _lastAppliedColumnWidths;
    private bool _anyCutApplied;

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
    private TextBox? _renameEditor;
    private bool _finishingRename;
    private string? _activeRenamePath;
    private string? _suppressSlowRenamePath;
    private DateTime _suppressSlowRenameUntilUtc;
    private Point? _dragStartPoint;
    private FileSystemEntry? _dragStartEntry;
    private PointerPressedEventArgs? _dragPointerEvent;
    private Task<IReadOnlyList<IStorageItem>>? _dragStorageItemsTask;
    private Bitmap? _dragStartPreviewBitmap;
    private Control? _dragCaptureControl;
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
    private FileSystemEntry? _dragOverTargetEntry;
    private FileSystemEntry? _rightPressedEntry;
    private Control? _rightPressedAnchor;
    private bool _selectionSyncQueued;
    private bool _entriesVisualRefreshQueued;
    private bool _sizeRefreshQueued;
    private int _scrollRestoreVersion;
    private bool _restoringNavigationSelection;
    private bool _restoringNavigationSelectionToTop;
    private Point? _marqueeStart;
    private IPointer? _marqueePointer;
    private bool _marqueeActive;
    private KeyModifiers _marqueeModifiers;
    private FileSystemEntry? _marqueeClickEntry;
    private HashSet<FileSystemEntry> _marqueeBaseSelection = [];
    private readonly DispatcherTimer _marqueeScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private Point _marqueeCurrentViewportPoint;
    private ScrollViewer? _marqueeScrollViewer;

    public FileListView()
    {
        InitializeComponent();
        InitializeSnapshotAnchoring();
        InitializeFastFileList();
        SizeChanged += (_, _) =>
        {
            if (_sizeRefreshQueued) return;
            _sizeRefreshQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _sizeRefreshQueued = false;
                ApplyListColumnWidths();
            }, DispatcherPriority.Render);
        };
        AddHandler(PointerPressedEvent, OnDismissClick, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnGlobalPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(Control.RequestBringIntoViewEvent, OnRequestBringIntoView, RoutingStrategies.Bubble, handledEventsToo: true);
        // Track the complete gesture at the view root. On macOS, pointer capture can
        // reroute subsequent moves above the file surface; root
        // tunnel handlers keep the press -> move -> release chain intact.
        AddHandler(PointerPressedEvent, OnEmptyAreaPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnMarqueePointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnMarqueePointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        _marqueeScrollTimer.Tick += (_, _) => OnMarqueeScrollTick();
    }

    private FileListViewModel? ViewModel => DataContext as FileListViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        CancelActiveRename();
        ResetDragState();
        ColumnFilterPopup.IsOpen = false;
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
        FastList.ThumbnailProvider = _subscribedViewModel == null ? null : _subscribedViewModel.GetListThumbnailAsync;
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
        SyncFastListRows();
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
        CancelSlowRename();
        ResetDragState();
        ColumnFilterPopup.IsOpen = false;
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
        if (e.PropertyName == nameof(FileListViewModel.IsContextMenuVisible) && ViewModel?.IsContextMenuVisible == false)
            DismissContextMenu();

        if (e.PropertyName == nameof(FileListViewModel.ViewMode))
        {
            UpdateViewMode();
            SyncFastListRows();
            Dispatcher.UIThread.Post(() =>
            {
                ApplyListColumnWidths();
                QueueSelectionSynchronization();
            });
        }

        if (e.PropertyName is nameof(FileListViewModel.GroupField)
            or nameof(FileListViewModel.Groups)
            or nameof(FileListViewModel.IsHomePage)
            or nameof(FileListViewModel.CurrentPath)
            or nameof(FileListViewModel.IsRemoteView)
            or nameof(FileListViewModel.IsArchiveView)
            or nameof(FileListViewModel.IsAiView)
            or nameof(FileListViewModel.IsTagView))
        {
            UpdateViewMode();
            QueueEntriesVisualRefresh();
        }

        if (e.PropertyName is nameof(FileListViewModel.SortField)
            or nameof(FileListViewModel.SortAscending)
            or nameof(FileListViewModel.ColumnFilters))
        {
            UpdateSortHeaders();
            UpdateEmptyState();
            RefreshColumnFilterOptions();
        }
        if (e.PropertyName == nameof(FileListViewModel.CurrentPath))
            ColumnFilterPopup.IsOpen = false;

        if (e.PropertyName == nameof(FileListViewModel.Entries))
        {
            RefreshColumnFilterOptions();
            if (ReferenceEquals(_subscribedEntries, ViewModel?.Entries))
            {
                UpdateEmptyState();
                UpdateCutStates();
                return;
            }

            SubscribeEntriesCollection(ViewModel?.Entries);
            var scrollMode = ViewModel?.ScrollBehaviorAfterLoad ?? FileListViewModel.ScrollMode.ResetToTop;
            var preservedOffset = GetActiveScrollViewer()?.Offset ?? default;
            UpdateEmptyState();
            SyncFastListRows();
            Dispatcher.UIThread.Post(() =>
            {
                ApplyListColumnWidths();
                SynchronizeSelectionControls();
                ApplyScrollBehavior(scrollMode, preservedOffset);
                UpdateCutStates();
            }, DispatcherPriority.Loaded);
        }

        if (e.PropertyName is nameof(FileListViewModel.IsLoading) or nameof(FileListViewModel.ReadErrorMessage)
            or nameof(FileListViewModel.SearchQuery) or nameof(FileListViewModel.SearchScopePath) or nameof(FileListViewModel.IsSearchMode) or nameof(FileListViewModel.StatusText))
            UpdateEmptyState();

        if (e.PropertyName is nameof(FileListViewModel.IsLoading) or nameof(FileListViewModel.IsDirectoryLoading))
            UpdateFastListLoading();

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
        if (FastListActive) SyncFastListRows(force: true);
        UpdateEmptyState();
        QueueEntriesVisualRefresh();
    }

    private void QueueEntriesVisualRefresh()
    {
        if (_entriesVisualRefreshQueued) return;
        _entriesVisualRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _entriesVisualRefreshQueued = false;
            SyncFastListRows();
            ApplyListColumnWidths();
            UpdateCutStates();
            QueueSelectionSynchronization();
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
        FastList.ColumnWidths = effective;
        PositionFastRenameEditor();

        // Re-runs on every batched load and resize; when the widths are unchanged skip
        // the header assignments.
        if (_lastAppliedColumnWidths == effective)
            return;
        _lastAppliedColumnWidths = effective;
        _effectiveColumnWidths = effective;

        for (var column = 1; column <= 4 && column < ListHeaderGrid.ColumnDefinitions.Count; column++)
            ListHeaderGrid.ColumnDefinitions[column].Width = new GridLength(
                effective[(FileListColumn)(column - 1)]);

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

    private ScrollViewer? GetActiveScrollViewer() => FastListActive ? FastListHost : null;

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
        if (version != _scrollRestoreVersion || ViewModel == null) return ScrollRestoreResult.Cancelled;
        if (ViewModel.SelectedEntries.FirstOrDefault() is not { } selected) return ScrollRestoreResult.NoAnchor;
        if (!FastListActive || FastList.Viewport.Height <= 0) return ScrollRestoreResult.Pending;
        if (restoreSavedViewport && ViewModel.RestoredNavigationScrollOffsetY is { } saved)
            FastList.ScrollToOffset(saved);
        var y = restoreSavedViewport ? ViewModel.RestoredNavigationAnchorViewportY : null;
        return FastList.ScrollToEntry(selected, y.HasValue ? y.Value - 4 : null)
            ? ScrollRestoreResult.Aligned : ScrollRestoreResult.NoAnchor;
    }

    private void CompleteRestoreWithBestEffort(int version, bool restoreSavedViewport)
    {
        if (version != _scrollRestoreVersion || ViewModel == null) return;
        TryRestoreSelectedEntryPosition(version, restoreSavedViewport);
        CompleteScrollRestore(version);
    }

    private void CompleteScrollRestore(int version)
    {
        if (version != _scrollRestoreVersion || ViewModel == null)
            return;

        ViewModel.ScrollBehaviorAfterLoad = FileListViewModel.ScrollMode.PreservePosition;
        _restoringNavigationSelection = false;
        _restoringNavigationSelectionToTop = false;
    }

    private void OnRequestBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
    {
        if (!IsRestoringNavigationSelectionToTop())
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
           || ViewModel?.ScrollBehaviorAfterLoad == FileListViewModel.ScrollMode.RestoreNavigation;

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

    private void OnCaptureNavigationAnchorRequested()
    {
        var (viewportY, scrollOffsetY) = CaptureSelectedEntryNavigationAnchor();
        ViewModel?.SaveCurrentNavigationAnchor(viewportY, scrollOffsetY);
    }

    private (double? ViewportY, double? ScrollOffsetY) CaptureSelectedEntryNavigationAnchor()
    {
        if (ViewModel?.SelectedEntries.FirstOrDefault() is not { } selected)
            return (null, GetActiveScrollViewer()?.Offset.Y);
        var index = FastList.IndexOf(selected);
        return (index < 0 ? null : FastList.RowBounds(index).Y + 4, FastList.Offset.Y);
    }

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
        if (ViewModel?.IsBrowseOnly == true || !FastListActive) return;
        CancelActiveRename();
        BeginFastRename(entry);
    }

    private void BindRenameEditor(TextBox editor, FileSystemEntry entry)
    {
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
            if (!ReferenceEquals(_renameEditor, editor)) return;
            editor.Focus();
            var extensionLength = entry.IsDirectory ? 0 : Path.GetExtension(entry.Name).Length;
            editor.SelectionStart = 0;
            editor.SelectionEnd = Math.Max(0, entry.Name.Length - extensionLength);
        });
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
            // operation runs, then let the list draw the updated entry name.
            RemoveRenameEditor();
            _finishingRename = false;
        }
    }

    private void CancelActiveRename()
    {
        if (_renameEditor == null) return;
        _finishingRename = true;
        if (!string.IsNullOrWhiteSpace(_activeRenamePath))
            SuppressImmediateSlowRename(_activeRenamePath);
        RemoveRenameEditor();
        _finishingRename = false;
    }

    private void RemoveRenameEditor()
    {
        var editor = _renameEditor;
        _renameEditor = null;
        _activeRenamePath = null;
        if (editor != null) FastRenameOverlay.Children.Remove(editor);
        FastList.EditingPath = null;
        FastList.InvalidateVisual();
        FastList.Focus();
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
        int requestVersion,
        FileSystemEntry? markdownEntry = null)
    {
        menu.Items.Clear();

        // Quick actions bar — vertical icon+text buttons at top, evenly distributed
        var quickActions = actions.Where(a => a.IsQuickAction).ToList();
        var editAction = menu is ContextMenu ? CreateMarkdownEditAction(markdownEntry) : null;
        if (editAction != null)
        {
            quickActions.Insert(0, editAction);
            // Preserve the existing 44x44 button surfaces; grow the menu instead of squeezing them.
            menu.Width = double.NaN;
            menu.MaxWidth = double.PositiveInfinity;
            menu.MinWidth = Math.Max(menu.MinWidth, quickActions.Count * 44 + 8);
        }
        if (quickActions.Count > 0)
        {
            var quickGrid = new Grid
            {
                MinWidth = editAction != null ? quickActions.Count * 44 : 0,
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
                    Classes = { "menu-quick-label" },
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
                btn.Classes.Add("menu-quick-action");
                var buttonSurface = new Border
                {
                    Width = 44,
                    Height = 44,
                    CornerRadius = new CornerRadius(6),
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                    Child = btn
                };
                buttonSurface.Classes.Add("menu-quick-surface");
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
            if (action.IsCheckable)
            {
                item.ToggleType = MenuItemToggleType.CheckBox;
                item.IsChecked = action.IsChecked;
                if (action.IsIndeterminate)
                    item.Header = action.Label + "（部分文件）";
            }
            if (!string.IsNullOrEmpty(action.ShortcutText))
                item.InputGesture = ParseShortcut(action.ShortcutText);
            if (action.IconImage != null)
                item.Icon = new Image { Source = action.IconImage, Width = 18, Height = 18 };
            else if (!string.IsNullOrEmpty(action.IconSvg))
            {
                try
                {
                    var icon = new PathIcon { Data = Geometry.Parse(action.IconSvg), Width = 16, Height = 16 };
                    if (action.IconColor != null) icon.Foreground = Brush.Parse(action.IconColor);
                    item.Icon = icon;
                }
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
        if (ViewModel == null || ViewModel.IsBrowseOnly) return;

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
        FillMenu(menu, ViewModel.ContextMenuActions, requestVersion, entry);
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
            FillMenu(menu, viewModel.ContextMenuActions, completeRequestVersion, entry);
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
        _rightPressedEntry = null;
        _rightPressedAnchor = null;
    }

    public bool TryDismissContextMenu()
    {
        if (_openMenu == null && ViewModel?.IsContextMenuVisible != true)
            return false;
        DismissContextMenu();
        Focus();
        return true;
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
            DismissContextMenu();
            return;
        }

        if (!point.Properties.IsRightButtonPressed || ViewModel == null)
            return;

        ViewModel.NotifyTransientInteractionStarted();
        var sourceVisual = e.Source as Visual;
        var entry = EntryAtPointer(e);
        if (FastListActive && entry != null) _fastFocusedPath = entry.FullPath;
        if (entry == null && !IsWithinVisual(sourceVisual, FileScroll))
            return;
        _rightPressedEntry = entry;
        _rightPressedAnchor = entry == null ? FileScroll : FastList;
        if (entry != null && !ViewModel.IsEntrySelected(entry))
        {
            ViewModel.SelectEntryForContextMenu(entry);
            QueueSelectionSynchronization();
        }
        if (entry == null)
        {
            ViewModel.ClearSelection();
        }

        e.Handled = true;
    }

    private static bool IsWithinVisual(Visual? visual, Visual ancestor)
    {
        for (; visual != null; visual = visual.GetVisualParent())
            if (ReferenceEquals(visual, ancestor))
                return true;
        return false;
    }

    private void HandleEntryPointerPressed(Control control, FileSystemEntry entry, PointerPressedEventArgs e)
    {
        if (ViewModel == null) return;

        FastList.Focus();
        var point = e.GetCurrentPoint(control);

        if (point.Properties.IsRightButtonPressed)
        {
            CancelSlowRename();
            ViewModel.NotifyTransientInteractionStarted();
            _rightPressedEntry = entry;
            _rightPressedAnchor = control;
            if (!ViewModel.IsEntrySelected(entry))
                ViewModel.SelectEntryForContextMenu(entry);
            QueueSelectionSynchronization();
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            _rightPressedEntry = null;
            _rightPressedAnchor = null;
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
            ResetDragState();
            _dragStartPoint = e.GetPosition(this);
            _dragStartEntry = entry;
            _dragPointerEvent = e;
            _dragStartPreviewBitmap = GetVisibleEntryBitmap(control, entry);
            _dragStorageItemsTask = null;
            _dragStarted = false;
            _collapseSelectionOnRelease = preserveMultiSelectionForDrag;
            _pressedEntry = entry;
            _dragCaptureControl = control;
            control.PointerCaptureLost += OnItemPointerCaptureLost;
            e.Pointer.Capture(control);
            if (!ViewModel.IsBrowseOnly && wasAlreadySingleSelected && !suppressSlowRename && !entry.IsVirtual && !ViewModel.IsArchiveView)
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
        // Capture owns this press until release/capture loss. Captured macOS moves
        // can omit the button mask, just like the marquee gesture below.
        var current = e.GetPosition(this);
        var start = _dragStartPoint.Value;
        if (Math.Abs(current.X - start.X) < 5 && Math.Abs(current.Y - start.Y) < 5)
            return;

        // A drag gesture must cancel slow rename even while fallback data is pending.
        CancelSlowRename();
        _collapseSelectionOnRelease = false;
        var representativeEntry = _dragStartEntry;
        var dragPaths = GetLocalDragPaths(ViewModel.SelectedEntries.Count > 0
            ? ViewModel.SelectedEntries : [representativeEntry]);
        if (dragPaths.Length == 0)
        {
            ResetDragState();
            return;
        }

        try
        {
            // AppKit only needs file URLs. Do not resolve storage items or touch the
            // file system before handing it the current mouse-drag event.
            if (_dragStorageItemsTask == null && OperatingSystem.IsMacOS()
                && TopLevel.GetTopLevel(this) is { } topLevel)
            {
                using var nativePreview = CreateDragPreviewBitmap(representativeEntry, dragPaths.Length, _dragStartPreviewBitmap);
                _dragStarted = true;
                // Release Avalonia capture before AppKit takes ownership of the gesture.
                e.Pointer.Capture(null);
                if (MacExplorer.Platforms.MacOS.MacNativeFileDrag.TryBeginFileDrag(
                    topLevel, e.GetPosition(topLevel), dragPaths, nativePreview,
                    AllowedDragEffects, OnNativeDragSession))
                {
                    ResetDragState();
                    return;
                }
                _dragStarted = false;
                e.Pointer.Capture(_dragCaptureControl);
            }

            var storageItemsTask = _dragStorageItemsTask ??= StartDragStorageItemsResolution();
            if (storageItemsTask == null)
            {
                ResetDragState();
                return;
            }
            // The portable fallback must also start inside a pointer callback.
            // Capture keeps delivering movement outside the original item bounds.
            if (!storageItemsTask.IsCompleted) return;
            var storageItems = storageItemsTask.GetAwaiter().GetResult();
            if (storageItems.Count == 0)
            {
                ResetDragState();
                return;
            }

            _dragStarted = true;
            using var preview = CreateDragPreviewBitmap(representativeEntry, storageItems.Count, _dragStartPreviewBitmap);
            using var data = CreateDragData(storageItems, preview);
            _dragStorageItemsTask = null; // DataTransfer now owns the storage items.
            var fallbackEffect = DragDropEffects.None;
            DragSessionChanged?.Invoke(new(FileDragPhase.Started, default, DragDropEffects.None));
            try
            {
                fallbackEffect = await DragDrop.DoDragDropAsync(_dragPointerEvent, data, AllowedDragEffects);
            }
            finally
            {
                DragSessionChanged?.Invoke(new(FileDragPhase.Ended, default, fallbackEffect));
                ResetDragState();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"File drag failed: {ex}");
            if (ViewModel != null) ViewModel.StatusText = $"拖动失败: {ex.Message}";
            ResetDragState();
        }
    }

    internal static string[] GetLocalDragPaths(IEnumerable<FileSystemEntry> entries)
        => entries.Where(entry => !entry.IsVirtual && Path.IsPathFullyQualified(entry.FullPath)
                                  && !VirtualPath.IsRemotePath(entry.FullPath))
            .Select(entry => entry.IsDirectory && !Path.EndsInDirectorySeparator(entry.FullPath)
                ? entry.FullPath + Path.DirectorySeparatorChar : entry.FullPath)
            .Distinct(StringComparer.Ordinal).ToArray();

    private void OnItemPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_dragStarted) return;
        CancelSlowRename();
        ResetDragState();
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

    private static DataTransfer CreateDragData(IReadOnlyList<IStorageItem> storageItems, Bitmap? preview)
    {
        var data = new DataTransfer();
        for (var index = 0; index < storageItems.Count; index++)
        {
            var item = new DataTransferItem();
            item.SetFile(storageItems[index]);
            if (index == 0 && preview != null) item.SetBitmap(preview);
            data.Add(item);
        }
        return data;
    }

    private static Bitmap? GetVisibleEntryBitmap(Control control, FileSystemEntry entry)
        => (control as MacExplorer.Controls.FastFileList)?.GetEntryBitmap(entry);

    internal static void PrepareFileDrag()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            // Exercise drawing, badge text and the native pixel bridge after the
            // window is visible, before the first pointer gesture needs them.
            using var preview = CreateDragPreviewBitmap(new FileSystemEntry { IsDirectory = true }, 2, null);
            MacExplorer.Platforms.MacOS.MacNativeFileDrag.Prepare(preview);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"File drag preparation failed: {ex.Message}");
        }
    }

    internal static Bitmap CreateDragPreviewBitmap(FileSystemEntry entry, int itemCount, Bitmap? sourceIcon)
    {
        // Render cached pixels directly. No image decoding, PNG encoding or temp files.
        var preview = new RenderTargetBitmap(new PixelSize(96, 96), new Vector(96, 96));
        try
        {
            using var drawing = preview.CreateDrawingContext();
            if (sourceIcon != null)
            {
                var size = sourceIcon.PixelSize.ToSize(1);
                var scale = Math.Min(68 / size.Width, 68 / size.Height);
                var destination = new Rect(48 - size.Width * scale / 2, 44 - size.Height * scale / 2,
                    size.Width * scale, size.Height * scale);
                drawing.DrawImage(sourceIcon, new Rect(size), destination);
            }
            else
            {
                // The existing Fluent geometry is available even before thumbnails load.
                var geometry = Geometry.Parse(entry.IsDirectory ? Assets.Icons.Folder : Assets.Icons.File);
                using var transform = drawing.PushTransform(Matrix.CreateScale(68d / 24, 68d / 24)
                    * Matrix.CreateTranslation(14, 10));
                drawing.DrawGeometry(Brushes.DodgerBlue, null, geometry);
            }
            if (itemCount > 1)
            {
                var badge = new Rect(56, 54, 32, 28);
                drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(0, 122, 255)), null, badge, 14, 14);
                var label = itemCount > 99 ? "99+" : itemCount.ToString();
                var text = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, new Typeface(FontManager.Current.DefaultFontFamily, FontStyle.Normal, FontWeight.Bold),
                    label.Length > 2 ? 12 : 15, Brushes.White);
                drawing.DrawText(text, new Point(badge.Center.X - text.Width / 2, badge.Center.Y - text.Height / 2));
            }
            return preview;
        }
        catch
        {
            preview.Dispose();
            throw;
        }
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
        _rightPressedAnchor = null;
        e.Handled = true;
        ResetDragState();
        await ShowMenuAsync(contextAnchor, hasSelection);
    }

    private void ResetDragState()
    {
        var pendingStorageItems = _dragStorageItemsTask;
        var pointer = _dragPointerEvent?.Pointer;
        var capturedControl = _dragCaptureControl;
        _dragCaptureControl = null;
        _dragStartPoint = null;
        _dragStartEntry = null;
        _dragPointerEvent = null;
        _dragStorageItemsTask = null;
        _dragStartPreviewBitmap = null;
        _dragStarted = false;
        _collapseSelectionOnRelease = false;
        _pressedEntry = null;
        if (capturedControl != null)
        {
            capturedControl.PointerCaptureLost -= OnItemPointerCaptureLost;
            if (ReferenceEquals(pointer?.Captured, capturedControl)) pointer.Capture(null);
        }
        if (pendingStorageItems != null)
            _ = DisposeStorageItemsWhenReadyAsync(pendingStorageItems);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (ViewModel?.IsBrowseOnly == true) { e.DragEffects = DragDropEffects.None; e.Handled = true; return; }
        var paths = GetDroppedPaths(e.DataTransfer);
        if (paths.Length == 0 || ViewModel == null || ViewModel.IsHomePage || ViewModel.IsArchiveView)
        {
            ClearDragOverVisual();
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var target = FindDropTarget(e);
        if (target == null && ViewModel?.IsTagView == true)
        {
            _dragOverTargetEntry = null;
            ClearDragOverVisual();
            e.DragEffects = paths.All(Services.Impl.FileTagService.IsSupportedPath) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            return;
        }
        _dragOverTargetEntry = target;
        var targetDirectory = target?.FullPath ?? ViewModel?.CurrentPath;
        var effect = string.IsNullOrWhiteSpace(targetDirectory)
            || ViewModel?.IsDirectoryLoading == true || FastListActive && FastList.IsLoading
            || ViewModel?.IsRemoteLocationDisconnected == true
            || target?.IsWritable == false
            // Keep drag-over free of file-system I/O (network mounts can block).
            // Existence is checked once, at the final drop position.
            || !VirtualPath.IsRemotePath(targetDirectory) && !Path.IsPathFullyQualified(targetDirectory)
            ? DragDropEffects.None : FileDropPolicy.GetEffect(paths, targetDirectory);
        if (effect == DragDropEffects.None)
        {
            ClearDragOverVisual();
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (FastListActive) FastList.DropTargetPath = target?.FullPath;

        e.DragEffects = effect;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        _dragOverTargetEntry = null;
        ClearDragOverVisual();
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel?.IsBrowseOnly == true) { e.DragEffects = DragDropEffects.None; e.Handled = true; return; }
        // Re-hit-test the release position. A previously hovered folder must not
        // receive files after the pointer has moved onto blank space or another tab.
        var target = FindDropTarget(e);
        _dragOverTargetEntry = null;
        ClearDragOverVisual();
        var viewModel = ViewModel;
        if (viewModel == null) return;
        e.Handled = true;
        e.DragEffects = DragDropEffects.None;
        if (viewModel.IsHomePage || viewModel.IsArchiveView || viewModel.IsRemoteLocationDisconnected) return;
        try
        {
            var paths = GetDroppedPaths(e.DataTransfer);
            if (paths.Length == 0) return;

            if (target == null && viewModel.CurrentTag is { } tag)
            {
                if (!paths.All(Services.Impl.FileTagService.IsSupportedPath)) return;
                e.DragEffects = DragDropEffects.Copy;
                await viewModel.SetFileTagAsync(paths, tag, true);
                return;
            }
            if (viewModel.IsDirectoryLoading || FastListActive && FastList.IsLoading) return;
            var targetDirectory = target?.FullPath ?? viewModel.CurrentPath;
            if (target?.IsWritable == false || !VirtualPath.IsRemotePath(targetDirectory) && !Directory.Exists(targetDirectory)) return;
            var effect = FileDropPolicy.GetEffect(paths, targetDirectory);
            if (effect == DragDropEffects.None) return;
            e.DragEffects = effect;
            if (effect == DragDropEffects.Copy)
            {
                var bridge = App.Services.GetRequiredService<IDragDropService>();
                var copied = await bridge.DropFilesAsync(
                    paths,
                    targetDirectory,
                    forceCopy: true,
                    forceMove: false);
                if (!copied) e.DragEffects = DragDropEffects.None;
                return;
            }

            paths = paths.Where(path => !FileDropPolicy.IsSameDestination(path, targetDirectory)).ToArray();
            if (paths.Length == 0)
                return;

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
            e.DragEffects = DragDropEffects.None;
            viewModel.StatusText = $"拖放失败: {ex.Message}";
        }
    }

    private FileSystemEntry? FindDropTarget(DragEventArgs e)
        => FastList.EntryAt(e.GetPosition(FastList)) is { IsDirectory: true } directory ? directory : null;

    private void ClearDragOverVisual() => FastList.DropTargetPath = null;

    internal static string[] GetDroppedPaths(IDataTransfer data)
    {
        return data.TryGetFiles()?
            .Select(item => Path.TrimEndingDirectorySeparator(item.Path.LocalPath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray() ?? [];
    }

    private void OnEmptyAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel == null || IsTextInputSource(e.Source)) return;
        var wasRenaming = _renameEditor != null;
        CancelSlowRename();

        var sourceVisual = e.Source as Visual;
        if (!IsWithinVisual(sourceVisual, FileScroll))
            return;
        // Scrollbar presses must reach the thumb/track before this tunnel handler
        // captures the pointer for a canvas marquee.
        if (sourceVisual is ScrollBar || sourceVisual?.FindAncestorOfType<ScrollBar>() != null)
            return;
        // Empty-state buttons (retry, clear filters, etc.) are controls, not canvas.
        if (sourceVisual is Button || sourceVisual?.FindAncestorOfType<Button>() != null)
            return;
        // List rows end at the Kind column's right edge; the remainder is
        // marquee/background canvas. Inside that extent — including the gaps
        // between cells — a secondary click is the row's context target:
        // let OnDismissClick keep the existing multi-selection instead of
        // letting this canvas handler turn it into a blank background click.
        if (e.GetCurrentPoint(FileScroll).Properties.IsRightButtonPressed
            && EntryAtPointer(e) != null)
            return;
        // Icon and name content starts an item gesture; surrounding gaps start a marquee.
        if (FastListActive && IsWithinVisual(sourceVisual, FastList) && FastList.EntryAt(e.GetPosition(FastList), contentOnly: true) != null)
            return;

        Focus();
        var point = e.GetCurrentPoint(FileScroll);
        var rowEntry = ViewModel.ViewMode == ViewMode.List
            ? EntryAtPointer(e)
            : null;
        if (point.Properties.IsLeftButtonPressed
            && e.ClickCount == 2
            && rowEntry is { } doubleClickedEntry)
        {
            OpenEntryFromGesture(doubleClickedEntry);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed && e.ClickCount == 2
            && e.KeyModifiers == KeyModifiers.None && EntryAtPointer(e) == null
            && !IsGroupHeaderAtPointer(e)
            && ViewModel.DoubleClickEmptyAreaGoUp && !ViewModel.IsHomePage
            && !ViewModel.IsDirectoryLoading && !(FastListActive && FastList.IsLoading)
            && !wasRenaming && !_dragStarted && !_marqueeActive)
        {
            EndMarquee(e.Pointer);
            DismissContextMenu();
            _ = ViewModel.NavigateUpAsync();
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            _rightPressedEntry = null;
            _rightPressedAnchor = null;
            _marqueeScrollViewer = GetActiveScrollViewer();
            _marqueeCurrentViewportPoint = e.GetPosition(FileScroll);
            _marqueeStart = ViewportToContent(_marqueeCurrentViewportPoint);
            _marqueeModifiers = e.KeyModifiers;
            _marqueeClickEntry = rowEntry;
            _marqueeBaseSelection = ViewModel.SelectedEntries.ToHashSet();
            // Row whitespace can still become a click. Keep the previous selection
            // until release replaces it or movement produces the marquee selection.
            if (rowEntry == null && !HasAdditiveSelectionModifier(_marqueeModifiers))
                ViewModel.ClearSelection();
            DismissContextMenu();
            // Keep the fast list under the pointer while waiting to distinguish
            // a row-whitespace click from a marquee, so its hover does not flash off.
            _marqueePointer = e.Pointer;
            e.Pointer.Capture(FastList);
            e.Handled = true;
        }
        else if (point.Properties.IsRightButtonPressed)
        {
            _marqueeClickEntry = null;
            _rightPressedEntry = null;
            _rightPressedAnchor = FileScroll;
            ViewModel.ClearSelection();
            e.Handled = true;
        }
    }

    private bool IsGroupHeaderAtPointer(PointerEventArgs e)
        => IsWithinVisual(e.Source as Visual, FastList) && FastList.IsGroupHeaderAt(e.GetPosition(FastList));

    private void OnMarqueePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_marqueeStart == null || ViewModel == null) return;
        if (FastListActive) FastList.UpdatePointerPosition(e.GetPosition(FastList));

        // The file surface captures the pointer from the left-button press.
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
        var clickEntry = !_marqueeActive ? _marqueeClickEntry : null;
        var modifiers = _marqueeModifiers;
        EndMarquee(e.Pointer);
        if (clickEntry != null && ViewModel != null)
        {
            if (FastListActive) _fastFocusedPath = clickEntry.FullPath;
            var hasCommandModifier = modifiers.HasFlag(KeyModifiers.Meta)
                                     || modifiers.HasFlag(KeyModifiers.Control);
            ViewModel.SelectEntry(clickEntry, hasCommandModifier, modifiers.HasFlag(KeyModifiers.Shift));
            QueueSelectionSynchronization();
        }
        e.Handled = true;
    }

    private void EndMarquee(IPointer pointer)
    {
        _marqueePointer = null;
        pointer.Capture(null);
        _marqueeStart = null;
        _marqueeActive = false;
        _marqueeClickEntry = null;
        _marqueeBaseSelection.Clear();
        _marqueeScrollViewer = null;
        _marqueeScrollTimer.Stop();
        SelectionMarquee.IsVisible = false;
    }

    private static bool HasAdditiveSelectionModifier(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Meta) || modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Shift);

    private Point ClampToFileArea(Point point) => new(
        Math.Clamp(point.X, 0, Math.Max(0, FileScroll.Bounds.Width)),
        Math.Clamp(point.Y, 0, Math.Max(0, FileScroll.Bounds.Height)));

    private static Rect RectFromPoints(Point first, Point second) => new(
        Math.Min(first.X, second.X), Math.Min(first.Y, second.Y),
        Math.Abs(second.X - first.X), Math.Abs(second.Y - first.Y));

    private Point ViewportToContent(Point point)
    {
        var offset = _marqueeScrollViewer?.Offset ?? default;
        return new Point(point.X + offset.X, point.Y + offset.Y);
    }

    private void UpdateMarqueeSelection(Point currentContentPoint)
    {
        if (_marqueeStart == null || ViewModel == null) return;
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
        var fastOrigin = FastList.TranslatePoint(default, FileScroll) ?? default;
        var hits = FastList.EntriesInRectangle(rectangle.Translate(new Vector(-fastOrigin.X, -fastOrigin.Y))).ToHashSet();
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

    private void SynchronizeSelectionControls() => FastList.InvalidateVisual();

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

    private void UpdateViewMode()
    {
        var visible = ViewModel is { IsHomePage: false };
        if (visible != FastListActive) CancelActiveRename();
        FastListHost.IsVisible = visible;
        FastList.IsVisible = visible;
        FastList.IsGrid = ViewModel?.ViewMode == ViewMode.Grid;
        SyncFastListRows();
        ListHeaderPanel.IsVisible = visible && !FastList.IsGrid;
        UpdateSortHeaders();
    }

    private async void ClearEmptySearch(object? sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
            await ViewModel.ExitSearchAsync();
    }

    private void UpdateEmptyState()
    {
        if (ViewModel == null) return;
        EmptyState.IsVisible = !ViewModel.IsLoading && ViewModel.Entries.Count == 0;
        var readFailed = !string.IsNullOrWhiteSpace(ViewModel.ReadErrorMessage) && !ViewModel.IsSearchMode;
        var searchFailed = ViewModel.IsSearchMode && ViewModel.StatusText.StartsWith("搜索失败:", StringComparison.Ordinal);
        var disconnected = ViewModel.IsRemoteView && ViewModel.StatusText == "服务器未连接";
        EmptyStateText.Text = readFailed ? "无法读取此位置"
            : searchFailed ? "无法完成搜索"
            : disconnected ? "未连接"
            : ViewModel.HasFileListFilters || ViewModel.IsSearchMode ? "未找到匹配的文件"
            : ViewModel.IsTagView ? "此标签下暂无文件" : "此文件夹为空";
        EmptyStateHint.Text = readFailed ? ViewModel.ReadErrorMessage
            : searchFailed ? ViewModel.StatusText
            : disconnected ? "请从侧栏或“连接远程服务器”入口连接"
            : ViewModel.HasFileListFilters ? "试试减少筛选条件，或清除筛选查看全部文件。"
            : ViewModel.IsSearchMode ? $"“{ViewModel.SearchQuery}”\n范围：{ViewModel.SearchScopePath}（包含已索引子文件夹）"
            : ViewModel.IsTagView && !ViewModel.IsBrowseOnly ? "拖入或粘贴文件以添加标签，原文件位置保持不变。"
            : string.Empty;
        EmptyStateHint.IsVisible = !string.IsNullOrEmpty(EmptyStateHint.Text);
        ClearEmptyFiltersButton.IsVisible = ViewModel.HasFileListFilters && !readFailed && !searchFailed && !disconnected;
        ClearEmptySearchButton.IsVisible = ViewModel.IsSearchMode;
        RetryReadButton.IsVisible = readFailed;
    }

    private void OpenEntryFromGesture(FileSystemEntry entry)
    {
        if (ViewModel == null) return;
        CancelSlowRename();
        if (entry.IsDirectory) ViewModel.SetSelection([entry], entry);
        if (entry.IsFolder) _ = ViewModel.NavigateToAsync(entry.FullPath);
        else _ = ViewModel.OpenEntryAsync(entry);
    }

    private void OnFileListKeyDown(object? sender, KeyEventArgs e)
    {
        TryHandleFileShortcut(e);
        HandleFastListNavigation(e);
    }

    public bool TryHandleFileShortcut(KeyEventArgs e)
    {
        if (ViewModel?.IsBrowseOnly == true) return TryHandleBrowseShortcut(e);
        if (ColumnFilterPopup.IsOpen) return false;
        if (ViewModel == null) return false;
        if (e.Handled || IsTextInputSource(e.Source)) return false;
        var commandModifier = e.KeyModifiers.HasFlag(KeyModifiers.Meta)
                              || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if ((ViewModel.IsDirectoryLoading || FastListActive && FastList.IsLoading) && !(commandModifier && e.Key is
            Key.H or Key.Up or Key.OemOpenBrackets or Key.OemCloseBrackets or Key.R))
        {
            e.Handled = true;
            return true;
        }

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
