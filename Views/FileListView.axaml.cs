using System;
using System.Collections.Concurrent;
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
    public FileSystemEntry? Entry { get; init; }
    public bool IsGroupHeader => Entry == null;
    public bool HasEntries => Entry != null;
    public IReadOnlyList<FileSystemEntry> Entries => Entry == null ? [] : [Entry];
}

public sealed class FileGridPresentationRow
{
    public string GroupName { get; init; } = string.Empty;
    public IReadOnlyList<FileSystemEntry> Entries { get; init; } = [];
    public bool IsGroupHeader => Entries.Count == 0;
    public bool HasEntries => Entries.Count > 0;
}

public partial class FileListView : UserControl
{
    private static readonly ConcurrentDictionary<string, Bitmap> MenuIconCache = new(StringComparer.Ordinal);
    private static readonly BitmapLruCache EntryImageCache = new(96L * 1024 * 1024);
    private static readonly SemaphoreSlim EntryImageLoadGate = new(4);
    private readonly ObservableCollection<FileListPresentationRow> _groupedListRows = [];
    private readonly ObservableCollection<FileGridPresentationRow> _gridRows = [];
    private readonly Dictionary<string, FileListPresentationRow> _groupedRowByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileGridPresentationRow> _gridRowByPath = new(StringComparer.Ordinal);
    private int _gridColumnCount;

    private sealed class EntryImageLoadState
    {
        public required string Source { get; set; }
        public required CancellationTokenSource Cancellation { get; init; }
    }

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
    private FileListViewModel? _subscribedViewModel;
    private ObservableCollection<FileSystemEntry>? _subscribedEntries;
    private ContextMenu? _openMenu;
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
    private int _resizingColumn = -1;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private CancellationTokenSource? _renameDelayCts;
    private bool _collapseSelectionOnRelease;
    private FileSystemEntry? _pressedEntry;
    private Control? _dragOverVisual;
    private FileSystemEntry? _dragOverTargetEntry;
    private FileSystemEntry? _rightPressedEntry;
    private Control? _rightPressedAnchor;
    private bool _selectionSyncQueued;

    public FileListView()
    {
        InitializeComponent();
        GroupedListItems.ItemsSource = _groupedListRows;
        GridViewItems.ItemsSource = _gridRows;
        SizeChanged += (_, _) => RebuildGridRowsIfColumnCountChanged();
        AddHandler(PointerPressedEvent, OnDismissClick, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnGlobalPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private FileListViewModel? ViewModel => DataContext as FileListViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_subscribedViewModel != null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel.SelectedEntries.CollectionChanged -= OnSelectedEntriesChanged;
            _subscribedViewModel.RenameRequested -= OnRenameRequested;
            _subscribedViewModel.ScrollToSelectionRequested -= OnScrollToSelectionRequested;
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
            SubscribeEntriesCollection(_subscribedViewModel.Entries);
        }

        UpdateViewMode();
        UpdateEmptyState();
        RebuildPresentationRows();
        QueueSelectionSynchronization();
        UpdateCutStates();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileListViewModel.ViewMode)
            or nameof(FileListViewModel.GroupField)
            or nameof(FileListViewModel.Groups))
        {
            UpdateViewMode();
            RebuildPresentationRows();
            Dispatcher.UIThread.Post(() =>
            {
                ApplyListColumnWidths();
                QueueSelectionSynchronization();
            });
        }

        if (e.PropertyName is nameof(FileListViewModel.SortField)
            or nameof(FileListViewModel.SortAscending))
        {
            UpdateSortHeaders();
        }

        if (e.PropertyName == nameof(FileListViewModel.Entries))
        {
            SubscribeEntriesCollection(ViewModel?.Entries);
            var scrollMode = ViewModel?.ScrollBehaviorAfterLoad ?? FileListViewModel.ScrollMode.ResetToTop;
            var preservedOffset = GetActiveScrollViewer()?.Offset ?? default;
            UpdateEmptyState();
            RebuildPresentationRows();
            Dispatcher.UIThread.Post(() =>
            {
                ApplyListColumnWidths();
                QueueSelectionSynchronization();
                ApplyScrollBehavior(scrollMode, preservedOffset);
                UpdateCutStates();
            });
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
        UpdateCutStates();
        RebuildPresentationRows();
    }

    private void UpdateCutStates()
    {
        if (ViewModel == null) return;
        foreach (var entry in ViewModel.Entries)
            entry.IsCut = ViewModel.CutPaths.Contains(entry.FullPath);
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
        image.Tag = null;
        Dispatcher.UIThread.Post(() => _ = LoadEntryImageAsync(image), DispatcherPriority.Loaded);
    }

    private async System.Threading.Tasks.Task LoadEntryImageAsync(Image image)
    {
        if (image.DataContext is not FileSystemEntry entry) return;
        var entryPath = entry.FullPath;
        var source = !string.IsNullOrWhiteSpace(entry.IconUrl) ? entry.IconUrl : null;

        if (image.Tag is EntryImageLoadState activeState
            && !activeState.Cancellation.IsCancellationRequested
            && ReferenceEquals(image.DataContext, entry))
            return;

        source = !string.IsNullOrWhiteSpace(entry.ThumbnailUrl) ? entry.ThumbnailUrl : source;

        // If thumbnail not yet available for a virtual entry, listen for it
        if (string.IsNullOrWhiteSpace(source) && entry.IsVirtual)
        {
            PropertyChangedEventHandler? handler = null;
            handler = (_, args) =>
            {
                if (args.PropertyName == nameof(FileSystemEntry.ThumbnailUrl)
                    && !string.IsNullOrWhiteSpace(entry.ThumbnailUrl)
                    && ReferenceEquals(image.DataContext, entry))
                {
                    entry.PropertyChanged -= handler;
                    Dispatcher.UIThread.Post(() => _ = LoadEntryImageAsync(image), DispatcherPriority.Loaded);
                }
            };
            entry.PropertyChanged += handler;
            // Also clean up if the image's DataContext changes
            image.DataContextChanged += (_, _) => entry.PropertyChanged -= handler;
            return;
        }

        if (image.Tag is string currentSource && currentSource == source && image.Source != null) return;

        var cts = new CancellationTokenSource();
        var state = new EntryImageLoadState { Source = entryPath, Cancellation = cts };
        image.Tag = state;
        try
        {
            if (entry.IconKey == "file-image" && string.IsNullOrWhiteSpace(entry.ThumbnailUrl))
            {
                var thumbnailService = App.Services.GetService<IThumbnailService>();
                var bytes = thumbnailService == null
                    ? null
                    : await System.Threading.Tasks.Task.Run(
                        () => thumbnailService.GetThumbnailAsync(entry.FullPath, 256, cts.Token),
                        cts.Token);
                if (bytes is { Length: > 0 })
                {
                    var cachePath = await StoreEntryThumbnailAsync(entry.FullPath, entry.LastModified, bytes, cts.Token);
                    if (ReferenceEquals(image.DataContext, entry) && entry.FullPath == entryPath)
                        entry.ThumbnailUrl = cachePath;
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

    private static Task<string> StoreEntryThumbnailAsync(
        string entryPath,
        DateTime lastModified,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        return System.Threading.Tasks.Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MacExplorer", "thumbnail-cache");
            Directory.CreateDirectory(cacheDirectory);
            var cacheName = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{entryPath}|{lastModified.Ticks}|256")));
            var cachePath = Path.Combine(cacheDirectory, cacheName + ".png");
            if (!File.Exists(cachePath))
                await File.WriteAllBytesAsync(cachePath, bytes, cancellationToken);
            return cachePath;
        }, cancellationToken);
    }

    private static void CancelEntryImageLoad(Image image)
    {
        if (image.Tag is EntryImageLoadState state)
            state.Cancellation.Cancel();
    }

    private static async System.Threading.Tasks.Task<Bitmap?> GetEntryBitmapAsync(
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
        if (sender is not Border { Tag: string tag } handle || !int.TryParse(tag, out var column)) return;
        if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;

        _resizingColumn = column;
        _resizeStartX = e.GetPosition(InteractionSurface).X;
        _resizeStartWidth = ListHeaderGrid.ColumnDefinitions[column].ActualWidth;
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnColumnResizeMoved(object? sender, PointerEventArgs e)
    {
        if (_resizingColumn < 0 || sender is not Control handle || e.Pointer.Captured != handle) return;
        var delta = _resizeStartX - e.GetPosition(InteractionSurface).X;
        SetListColumnWidth(_resizingColumn, Math.Max(50, _resizeStartWidth + delta));
        e.Handled = true;
    }

    private void OnColumnResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizingColumn < 0) return;
        _resizingColumn = -1;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void SetListColumnWidth(int column, double width)
    {
        ListHeaderGrid.ColumnDefinitions[column].Width = new GridLength(width);
        ApplyListColumnWidths();
    }

    private void ApplyListColumnWidths()
    {
        var widths = ListHeaderGrid.ColumnDefinitions.Select(definition => definition.ActualWidth).ToArray();
        foreach (var grid in this.GetVisualDescendants().OfType<Grid>()
                     .Where(grid => grid.Classes.Contains("file-list-row-grid")))
        {
            for (var column = 2; column <= 4 && column < grid.ColumnDefinitions.Count; column++)
            {
                var width = ListHeaderGrid.ColumnDefinitions[column].Width;
                grid.ColumnDefinitions[column].Width = width.IsAbsolute
                    ? width
                    : new GridLength(widths[column]);
            }
        }
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
                BringSelectedEntryIntoView();
                if (ViewModel != null)
                    ViewModel.ScrollBehaviorAfterLoad = FileListViewModel.ScrollMode.PreservePosition;
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
            : GroupedListItems.IsVisible ? GroupedListItems
            : GroupedGridItems;
        return host.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
    }

    private void BringSelectedEntryIntoView()
    {
        if (ViewModel?.SelectedEntries.FirstOrDefault() is not { } selected) return;

        if (FileItemsList.IsVisible)
        {
            FileItemsList.ScrollIntoView(selected);
        }
        else if (GroupedListItems.IsVisible
                 && _groupedRowByPath.TryGetValue(selected.FullPath, out var groupedRow))
        {
            GroupedListItems.ScrollIntoView(groupedRow);
        }
        else if (GridViewItems.IsVisible
                 && _gridRowByPath.TryGetValue(selected.FullPath, out var gridRow))
        {
            GridViewItems.ScrollIntoView(gridRow);
        }
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
        CancelActiveRename();

        var label = this.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(text => IsEntryNameLabelFor(text, entry));
        if (label?.Parent is not Panel parent) return;

        var index = parent.Children.IndexOf(label);
        if (index < 0) return;

        var isGridIconLabel = label.FindAncestorOfType<Border>()?.Classes.Contains("file-grid-content") == true;
        var editor = new TextBox
        {
            Text = entry.Name,
            FontSize = label.FontSize,
            Margin = label.Margin,
            HorizontalAlignment = isGridIconLabel
                ? global::Avalonia.Layout.HorizontalAlignment.Stretch
                : label.HorizontalAlignment,
            VerticalAlignment = label.VerticalAlignment,
            VerticalContentAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            MinWidth = 60,
            MinHeight = 0,
            Height = 22,
            Width = isGridIconLabel && parent.Bounds.Width > 0 ? parent.Bounds.Width : double.NaN,
            Padding = new Thickness(4, 0),
            BorderThickness = new Thickness(1)
        };
        if (parent is Grid)
        {
            Grid.SetColumn(editor, Grid.GetColumn(label));
            Grid.SetColumnSpan(editor, Grid.GetColumnSpan(label));
            Grid.SetRow(editor, Grid.GetRow(label));
            Grid.SetRowSpan(editor, Grid.GetRowSpan(label));
        }

        parent.Children.RemoveAt(index);
        parent.Children.Insert(index, editor);
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
        RestoreRenameLabel();

        if (commit && newName.Length > 0 && !string.Equals(newName, entry.Name, StringComparison.Ordinal))
            await ViewModel!.RenameEntryAsync(entry, newName);

        _finishingRename = false;
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

        if (editor?.Parent is not Panel parent || label == null) return;
        var index = parent.Children.IndexOf(editor);
        if (index < 0) return;
        parent.Children.RemoveAt(index);
        parent.Children.Insert(index, label);
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
                    catch { btnContent.Children.Add(new TextBlock { Text = qa.Label, FontSize = 11, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center }); }
                }
                btnContent.Children.Add(new TextBlock
                {
                    Text = qa.Label,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.Parse("#636366")),
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center
                });

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
        try
        {
            var bitmap = await System.Threading.Tasks.Task.Run(() =>
            {
                var comma = iconBase64.IndexOf(',');
                var payload = iconBase64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0
                    ? iconBase64[(comma + 1)..]
                    : iconBase64;
                return MenuIconCache.GetOrAdd(payload, static base64 =>
                {
                    using var stream = new MemoryStream(Convert.FromBase64String(base64));
                    return new Bitmap(stream);
                });
            });

            if (requestVersion != _menuRequestVersion) return;
            item.Icon = new Image
            {
                Source = bitmap,
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform
            };
        }
        catch
        {
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
            FillMenu(menu, viewModel.ContextMenuActions, requestVersion);
        }
        catch
        {
            // Keep the already-open lightweight menu if dynamic menu loading fails.
        }
    }

    private async System.Threading.Tasks.Task ExecuteMenuActionAsync(ContextMenuAction action)
    {
        DismissContextMenu();
        if (action.Execute != null)
            await action.Execute();
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
    }

    private void CloseCurrentMenu()
    {
        var menu = _openMenu;
        _openMenu = null;
        if (menu == null) return;
        menu.Closing -= OnContextMenuClosing;
        menu.Close();
    }

    private void OnContextMenuClosing(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu menu || !ReferenceEquals(menu, _openMenu)) return;
        _openMenu = null;
        menu.Closing -= OnContextMenuClosing;
        ViewModel?.CloseContextMenu();
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
        var entry = FindDataContextInAncestors(sourceVisual) as FileSystemEntry;
        if (entry == null && !IsWithinVisual(sourceVisual, FileScroll))
            return;
        _rightPressedEntry = entry;
        _rightPressedAnchor = entry == null ? FileScroll : FindEntryAnchor(sourceVisual) ?? FileScroll;
        if (entry != null && !ViewModel.IsEntrySelected(entry))
        {
            ViewModel.SelectEntryForContextMenu(entry);
            QueueSelectionSynchronization();
        }
        else if (entry == null)
        {
            ViewModel.ClearSelection();
        }
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

        var owner = control.FindAncestorOfType<ListBox>();
        owner?.Focus();
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
        _rightPressedAnchor = null;
        _rightPressedEntry = null;
        e.Handled = true;
        ResetDragState();
        await ShowMenuAsync(contextAnchor, hasSelection);
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
            var sourceEntries = new List<FileSystemEntry>();
            foreach (var path in paths.Distinct(StringComparer.Ordinal))
            {
                var entry = await fileService.GetEntryAsync(path);
                if (entry != null)
                    sourceEntries.Add(entry);
            }
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

        var hit = FileScroll.InputHitTest(e.GetPosition(FileScroll));
        if (hit is Visual visual && FindDataContextInAncestors(visual) is FileSystemEntry)
            return;

        Focus();
        var point = e.GetCurrentPoint(FileScroll);
        if (point.Properties.IsLeftButtonPressed)
        {
            _rightPressedEntry = null;
            _rightPressedAnchor = null;
            ViewModel.ClearSelection();
            DismissContextMenu();
        }
        else if (point.Properties.IsRightButtonPressed)
        {
            _rightPressedEntry = null;
            _rightPressedAnchor = FileScroll;
            ViewModel.ClearSelection();
        }
    }

    private static object? FindDataContextInAncestors(Visual? visual)
    {
        for (; visual != null; visual = visual.GetVisualParent())
            if (visual is Control { DataContext: FileSystemEntry entry })
                return entry;
        return null;
    }

    private void OnControlSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || sender is not ListBox listBox || ViewModel == null)
            return;

        var selected = listBox.SelectedItems?.OfType<FileSystemEntry>().ToList() ?? [];
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
            foreach (var listBox in GetActiveSelectionLists())
            {
                if (listBox.SelectedItems == null) continue;
                listBox.SelectedItems.Clear();
                foreach (var entry in listBox.Items.OfType<FileSystemEntry>())
                    if (selectedPaths.Contains(entry.FullPath))
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
        if (GroupedGridItems.IsVisible)
        {
            foreach (var list in GetSelectionListsWithin(GroupedGridItems))
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
        GroupedGridItems.IsVisible = false;
        if (isGrid) RebuildPresentationRows();
        ListHeaderPanel.IsVisible = !isGrid;
        UpdateSortHeaders();
    }

    private void RebuildPresentationRows()
    {
        if (ViewModel == null) return;

        _groupedListRows.Clear();
        _groupedRowByPath.Clear();
        if (ViewModel.GroupField != GroupField.None)
        {
            foreach (var group in ViewModel.Groups)
            {
                _groupedListRows.Add(new FileListPresentationRow { GroupName = group.Name });
                foreach (var entry in group.Entries)
                {
                    var row = new FileListPresentationRow { Entry = entry };
                    _groupedListRows.Add(row);
                    _groupedRowByPath[entry.FullPath] = row;
                }
            }
        }

        RebuildGridRows(CalculateGridColumnCount());
    }

    private int CalculateGridColumnCount()
        => Math.Max(1, (int)Math.Floor(Math.Max(108, Bounds.Width - 16) / 108));

    private void RebuildGridRowsIfColumnCountChanged()
    {
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
                : ViewModel.Groups.Select(group => ((string?)group.Name, group.Entries));
        foreach (var (header, entries) in groups)
        {
            if (header != null)
                _gridRows.Add(new FileGridPresentationRow { GroupName = header });
            for (var i = 0; i < entries.Count; i += columns)
            {
                var row = new FileGridPresentationRow
                {
                    Entries = entries.Skip(i).Take(columns).ToArray()
                };
                _gridRows.Add(row);
                foreach (var entry in row.Entries)
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
        if (entry.IsDirectory) _ = ViewModel.NavigateToAsync(entry.FullPath);
        else _ = ViewModel.OpenEntryAsync(entry);
        e.Handled = true;
    }

    private void OnFileListKeyDown(object? sender, KeyEventArgs e)
    {
        TryHandleFileShortcut(e);
    }

    public bool TryHandleFileShortcut(KeyEventArgs e)
    {
        if (ViewModel == null) return false;
        if (e.Handled || IsTextInputSource(e.Source)) return false;

        var commandModifier = e.KeyModifiers.HasFlag(KeyModifiers.Meta)
                              || e.KeyModifiers.HasFlag(KeyModifiers.Control);

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
