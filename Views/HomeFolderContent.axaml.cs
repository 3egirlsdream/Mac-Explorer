using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.Views;

public partial class HomeFolderContent : UserControl
{
    public event Action? CloseRequested;
    public void Close() => CloseRequested?.Invoke();
    private const int PageSize = 72;
    private readonly Dictionary<string, FileSystemEntry> _knownEntries;
    private FileTag _tag;
    private readonly IFileTagService _tags;
    private readonly HomeItemActions _actions;
    private IReadOnlyList<string> _paths;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _pageCancellation;
    private int _page;
    private bool _closed;
    private bool _opened;

    public HomeFolderContent(FileTag tag, IReadOnlyList<string> paths, IFileTagService tags, HomeItemActions actions, IReadOnlyList<FileSystemEntry>? previewEntries = null)
    {
        InitializeComponent();
        _tag = tag; _paths = paths; _tags = tags;
        _knownEntries = (previewEntries ?? []).ToDictionary(entry => entry.FullPath, StringComparer.Ordinal);
        _actions = actions.ForOwner(this, ShowError);
        UpdateTitle();
        RenderEntries(_knownEntries.Values);
        AttachedToVisualTree += (_, _) =>
        {
            _opened = true;
            _tags.TagsChanged += OnTagsChanged; _tags.TagRenamed += OnTagRenamed;
            _ = RenderPageAsync();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _closed = true;
            _tags.TagsChanged -= OnTagsChanged; _tags.TagRenamed -= OnTagRenamed;
            _loadCancellation?.Cancel(); _loadCancellation?.Dispose();
            _pageCancellation?.Cancel(); _pageCancellation?.Dispose();
        };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.DataTransfer.Formats.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            var local = e.DataTransfer.TryGetFiles()?.Where(item => item.Path.IsFile).Select(item => item.Path.LocalPath).ToArray() ?? [];
            e.DragEffects = local.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            await _actions.GuardAsync(() => _actions.AddPathsAsync(_tag, local));
        });
    }

    private void UpdateTitle()
    {
        Avalonia.Automation.AutomationProperties.SetName(this, _tag.Name);
        ContextMenu = _actions.CreateFolderMenu(_tag, Close);
    }

    private void ShowError(string text) => ToolTip.SetTip(this, text);

    private void OnTagsChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() => { if (!_closed) _ = ReloadAsync(); });
    private void OnTagRenamed(object? sender, TagRenamedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (_closed || !string.Equals(_tag.Name, e.OldName, StringComparison.OrdinalIgnoreCase)) return;
            if (e.NewName == null) { Close(); return; }
            _tag = _tag with { Name = e.NewName }; UpdateTitle(); _ = ReloadAsync();
        });

    private async Task ReloadAsync()
    {
        _loadCancellation?.Cancel(); _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token;
        var tag = _tag;
        try
        {
            await Task.Delay(100, token);
            var paths = await Task.Run(async () => (await _tags.FindFilePathsAsync(tag, token))
                .Distinct(StringComparer.Ordinal).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal).ToArray(), token);
            token.ThrowIfCancellationRequested();
            _paths = paths;
            _knownEntries.Clear();
            await RenderPageAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) ShowError("读取失败：" + ex.Message); }
    }

    private async Task RenderPageAsync()
    {
        if (_closed || !_opened) return;
        _pageCancellation?.Cancel(); _pageCancellation?.Dispose();
        _pageCancellation = new CancellationTokenSource();
        var token = _pageCancellation.Token;
        var paths = _paths;
        try
        {
            await Task.Delay(100, token);
            var filtered = paths.ToArray();
            token.ThrowIfCancellationRequested();
            var pageCount = Math.Max(1, (filtered.Length + PageSize - 1) / PageSize);
            _page = Math.Clamp(_page, 0, pageCount - 1);
            var pagePaths = filtered.Skip(_page * PageSize).Take(PageSize).ToArray();
            var missing = pagePaths.Where(path => !_knownEntries.ContainsKey(path)).ToArray();
            if (missing.Length > 0)
            {
                var loaded = await _actions.LoadEntriesAsync(missing, token);
                token.ThrowIfCancellationRequested();
                foreach (var entry in loaded) _knownEntries[entry.FullPath] = entry;
            }
            var entries = pagePaths.Select(path => _knownEntries[path]).ToArray();
            RenderEntries(entries);
            EmptyMessage.IsVisible = entries.Length == 0;
            ToolTip.SetTip(PreviousButton, $"上一页 · 第 {_page + 1}/{pageCount} 页");
            ToolTip.SetTip(NextButton, $"下一页 · 第 {_page + 1}/{pageCount} 页");
            PreviousButton.IsEnabled = _page > 0; NextButton.IsEnabled = _page < pageCount - 1;
            ItemsScroll.Offset = default;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) ShowError("读取文件失败：" + ex.Message); }
    }
    private void RenderEntries(IEnumerable<FileSystemEntry> entries)
    {
        // Keep existing controls (and their decoded images) while the sheet finishes loading.
        var existing = FolderEntries.Children.OfType<Button>().ToDictionary(
            button => ((FileSystemEntry)button.DataContext!).FullPath, StringComparer.Ordinal);
        var desired = entries.ToArray();
        var paths = desired.Select(entry => entry.FullPath).ToHashSet(StringComparer.Ordinal);
        foreach (var button in existing.Values.Where(button => !paths.Contains(((FileSystemEntry)button.DataContext!).FullPath)))
            FolderEntries.Children.Remove(button);
        for (var i = 0; i < desired.Length; i++)
        {
            existing.TryGetValue(desired[i].FullPath, out var button);
            if (button != null && !ReferenceEquals(button.DataContext, desired[i]))
            {
                FolderEntries.Children.Remove(button); button = null;
            }
            if (button == null)
            {
                button = _actions.CreateItemButton(desired[i], _tag, beforeOpen: Close);
                button.Width = 116; button.Height = 116; button.Margin = new Thickness(4, 6);
                FolderEntries.Children.Insert(i, button);
            }
            else if (FolderEntries.Children.IndexOf(button) != i)
            {
                FolderEntries.Children.Remove(button); FolderEntries.Children.Insert(i, button);
            }
        }
    }

    private void OnPrevious(object? sender, RoutedEventArgs e) { _page--; _ = RenderPageAsync(); }
    private void OnNext(object? sender, RoutedEventArgs e) { _page++; _ = RenderPageAsync(); }
}
