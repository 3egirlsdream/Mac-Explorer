using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Views;

public partial class SuperPreviewView
{
    private sealed class PreviewNode(FileSystemEntry entry, PreviewNode? parent, bool isArchive)
    {
        public FileSystemEntry Entry { get; } = entry;
        public PreviewNode? Parent { get; } = parent;
        public bool IsArchive { get; } = isArchive;
        public bool CanExpand => IsArchive || Entry.IsFolder && !Entry.IsSymbolicLink && !Entry.IsVirtual;
        public List<PreviewNode>? Children { get; set; }
        public CancellationTokenSource? Load { get; set; }
        public string? Error { get; set; }
        public string? TemporaryArchive { get; set; }
        public bool IsExpanded => Load != null || Children != null;

        public string? ContainingArchive => ArchivePathHelper.IsArchivePath(Entry.FullPath)
            ? Parent?.IsArchive == true ? Parent.DisplayPath : Parent?.ContainingArchive ?? ArchivePathHelper.Parse(Entry.FullPath).archivePath
            : null;

        public string DisplayPath => ContainingArchive is { } archive
            ? $"{archive}!/{ArchivePathHelper.Parse(Entry.FullPath).internalPath.TrimEnd('/')}"
            : Entry.FullPath;

        public string RevealPath => ArchivePathHelper.IsArchivePath(Entry.FullPath)
            ? Parent?.RevealPath ?? ArchivePathHelper.Parse(Entry.FullPath).archivePath
            : Entry.FullPath;
    }

    private readonly List<PreviewNode> _visiblePreviewNodes = [];
    private readonly Dictionary<string, string> _archivePasswords = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _archivePasswordGate = new(1, 1);
    private PreviewNode? _previewRoot;
    private PreviewNode? _selectedNode;
    private string? _selectedTemporaryFile;
    private double _previewTreeWidth = 240;
    private IPointer? _previewResizePointer;
    private double _previewResizeStartX;
    private double _previewResizeStartWidth;

    private async Task OpenFullPreviewAsync(FileSystemEntry entry, IReadOnlyList<FileSystemEntry>? fileOrder)
    {
        var isArchive = IsPreviewArchive(entry);
        var isMember = ArchivePathHelper.IsArchivePath(entry.FullPath);
        var browse = isArchive || entry.IsFolder && !entry.IsVirtual || isMember;
        PreviewListCard.IsVisible = browse;
        ContentDivider.IsVisible = browse;
        PreviewTreeResizeHandle.IsVisible = browse;
        UpdatePreviewColumns();

        if (!browse)
        {
            _previewFiles = fileOrder?.Where(file => !file.IsFolder && !IsPreviewArchive(file)
                && !ArchivePathHelper.IsArchivePath(file.FullPath)).ToArray() ?? [];
            await SelectStandaloneFileAsync(entry);
            return;
        }

        FileSystemEntry? preferred = null;
        var rootEntry = entry;
        if (isMember && !entry.IsFolder)
        {
            var (archivePath, key) = ArchivePathHelper.Parse(entry.FullPath);
            var parentKey = Path.GetDirectoryName(key.TrimEnd('/')) ?? string.Empty;
            rootEntry = parentKey.Length == 0
                ? new FileSystemEntry { FullPath = archivePath, Name = Path.GetFileName(archivePath) }
                : new FileSystemEntry
                {
                    FullPath = ArchivePathHelper.Build(archivePath, parentKey + "/"),
                    Name = Path.GetFileName(parentKey),
                    IsDirectory = true,
                    IconKey = "folder"
                };
            preferred = entry;
        }
        var root = _previewRoot = CreatePreviewNode(rootEntry, null);
        PublishPreviewTree();
        await SelectTreeNodeAsync(root);
        PreviewTree.Focus();
        if (!root.CanExpand) return;

        await ExpandPreviewNodeAsync(root, preferred?.FullPath);
        if (!ReferenceEquals(_previewRoot, root) || !ReferenceEquals(_selectedNode, root)) return;
        var initial = isMember
            ? root.Children?.FirstOrDefault(node => node.Entry.FullPath == preferred?.FullPath)
            : root.Children?.FirstOrDefault();
        if (initial != null) await SelectTreeNodeAsync(initial);
    }

    private bool IsPreviewArchive(FileSystemEntry entry)
        => !entry.IsDirectory && !entry.IsVirtual && _archiveService?.IsArchiveFile(entry.Name) == true;

    private PreviewNode CreatePreviewNode(FileSystemEntry entry, PreviewNode? parent)
        => new(entry, parent, IsPreviewArchive(entry));

    private async Task ExpandPreviewNodeAsync(PreviewNode node, string? preferredPath = null)
    {
        if (!node.CanExpand || node.IsExpanded) return;
        using var load = new CancellationTokenSource();
        var token = load.Token;
        node.Load = load;
        node.Error = null;
        PublishPreviewTree();
        if (node == _selectedNode) ShowNodeSummary(node);

        string? temporaryArchive = null;
        try
        {
            var entry = node.Entry;
            var archiveEntry = ArchivePathHelper.IsArchivePath(entry.FullPath);
            IReadOnlyList<FileSystemEntry>? entries;
            if (node.IsArchive || archiveEntry)
            {
                var (archivePath, internalPath) = ArchivePathHelper.Parse(entry.FullPath);
                if (node.IsArchive && archiveEntry)
                {
                    temporaryArchive = await ExtractArchiveEntryAsync(entry, token);
                    archivePath = temporaryArchive ?? string.Empty;
                    internalPath = string.Empty;
                }
                entries = archivePath.Length == 0 || _archiveService == null ? null : await ReadArchiveAsync(archivePath,
                    password => _archiveService.GetArchiveContentsAsync(archivePath, internalPath.Trim('/'), password), token, browse: true);
            }
            else
            {
                entries = await ReadTreeDirectoryAsync(entry, token);
            }

            token.ThrowIfCancellationRequested();
            if (entries == null)
            {
                node.Error = LocalizationText.Get("preview.tree.retry");
                return;
            }
            node.Children = SortPreviewEntries(entries, preferredPath).Select(entry => CreatePreviewNode(entry, node)).ToList();
            node.TemporaryArchive = temporaryArchive;
            temporaryArchive = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                node.Error = LocalizationText.Get("preview.tree.read-error", ex.Message);
        }
        finally
        {
            if (temporaryArchive != null) DeletePreviewArchive(temporaryArchive);
            if (ReferenceEquals(node.Load, load))
            {
                node.Load = null;
                PublishPreviewTree();
                if (node == _selectedNode) ShowNodeSummary(node);
            }
        }
    }

    private async Task<IReadOnlyList<FileSystemEntry>> ReadTreeDirectoryAsync(FileSystemEntry entry, CancellationToken token)
    {
        if (_fileService == null) return [];
        var entries = new List<FileSystemEntry>();
        await foreach (var batch in _fileService.EnumerateDirectoryBatchesAsync(entry.FullPath, cancellationToken: token))
            entries.AddRange(batch);
        if (entries.Count == 0 && (Path.IsPathFullyQualified(entry.FullPath) && !await _fileService.ExistsAsync(entry.FullPath)
            || !entry.IsReadable && !Path.IsPathFullyQualified(entry.FullPath)))
            throw new IOException(LocalizationText.Get("preview.unreadable"));
        return entries;
    }

    private async Task<T?> ReadArchiveAsync<T>(string path, Func<string?, Task<T>> read, CancellationToken token,
        bool browse = false) where T : class
    {
        if (_archiveService == null) return null;
        await _archivePasswordGate.WaitAsync(token);
        try
        {
            return await ArchivePasswordAccess.ReadAsync(_archiveService, path, read, PasswordPrompt, token, browse,
                _archivePasswords.GetValueOrDefault(path), password => _archivePasswords[path] = password,
                checkEncryption: browse && PasswordPrompt != null);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        finally { _archivePasswordGate.Release(); }
    }

    private void PublishPreviewTree()
    {
        _visiblePreviewNodes.Clear();
        var rows = new List<FileTreeRow>();
        if (_previewRoot != null) AddNode(_previewRoot, 0);
        PreviewTree.SetTreeRows(rows, []);
        UpdatePreviewSteps();
        UpdatePreviewToolbar();

        void AddNode(PreviewNode node, int depth)
        {
            _visiblePreviewNodes.Add(node);
            rows.Add(new FileTreeRow(node.Entry, depth, node.CanExpand, node.IsExpanded, node.Load != null, node.Error != null));
            if (node.Children != null)
                foreach (var child in node.Children) AddNode(child, depth + 1);
        }
    }

    private async Task SelectTreeNodeAsync(PreviewNode node)
    {
        if (node == _selectedNode) return;
        _selectedNode = node;
        _selectedEntry = node.Entry;
        PreviewTree.SelectedPath = PreviewTree.KeyboardFocusPath = node.Entry.FullPath;
        PreviewTree.InvalidateVisual();
        PreviewTree.ScrollToEntry(node.Entry);
        UpdatePreviewHeader(node.Entry.DisplayName);
        UpdatePreviewSteps();
        if (node.Entry.IsFolder || node.IsArchive)
        {
            ResetEntryPreview();
            ShowNodeSummary(node);
            return;
        }

        await ShowEntryPreviewAsync(node.Entry);
    }

    private void ShowNodeSummary(PreviewNode node)
    {
        if (node.Error != null || node.Load != null)
            ShowPlaceholder(node.Error ?? LocalizationText.Get("preview.tree.loading"));
        else
            ShowFolderSummary(node.Entry.DisplayName, LocalizationText.Get(node.IsArchive
                ? "preview.tree.archive" : "preview.tree.folder"), node.DisplayPath, node.Children?.Count);
        UpdatePreviewToolbar();
    }

    private void UpdatePreviewHeader(string title)
    {
        TitleText.Text = title;
        BreadcrumbText.IsVisible = _selectedNode?.Parent != null;
        BreadcrumbText.Text = _selectedNode?.Parent?.DisplayPath;
        BackButton.IsVisible = _selectedNode?.Parent != null;
    }

    private async Task TogglePreviewNodeAsync(PreviewNode node)
    {
        if (!node.IsExpanded)
        {
            await ExpandPreviewNodeAsync(node);
            return;
        }

        for (var ancestor = _selectedNode?.Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            if (ancestor != node) continue;
            await SelectTreeNodeAsync(node);
            break;
        }
        ClosePreviewBranch(node);
        PublishPreviewTree();
        if (node == _selectedNode) ShowNodeSummary(node);
    }

    private void ClosePreviewBranch(PreviewNode node)
    {
        node.Load?.Cancel();
        node.Load = null;
        if (node.Children != null)
            foreach (var child in node.Children) ClosePreviewBranch(child);
        node.Children = null;
        if (node.TemporaryArchive == null) return;
        DeletePreviewArchive(node.TemporaryArchive);
        node.TemporaryArchive = null;
    }

    private void DeletePreviewArchive(string path)
    {
        _archivePasswords.Remove(path);
        DeleteTemporaryFile(path);
    }

    private void ClearPreviewTree()
    {
        if (_previewResizePointer is { } pointer) pointer.Capture(null);
        _previewResizePointer = null;
        if (_previewRoot != null) ClosePreviewBranch(_previewRoot);
        _previewRoot = _selectedNode = null;
        PreviewTree.SelectedPath = PreviewTree.KeyboardFocusPath = null;
        _visiblePreviewNodes.Clear();
        PreviewTree.SetTreeRows([], []);
        PreviewTree.ScrollToOffset(0);
        PreviewToolbar.IsVisible = false;
        UpdatePreviewToolbarPlacement();
    }

    private async void OnPreviewTreePressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetPosition(PreviewTree);
        var index = PreviewTree.RowIndexAt(point);
        if (!e.GetCurrentPoint(PreviewTree).Properties.IsLeftButtonPressed || index < 0) return;
        var node = _visiblePreviewNodes[index];
        PreviewTree.Focus();
        e.Handled = true;
        if (PreviewTree.TreeDisclosureIndexAt(point) == index)
        {
            await TogglePreviewNodeAsync(node);
            return;
        }
        await SelectTreeNodeAsync(node);
        if (e.ClickCount == 2 && node.CanExpand) await TogglePreviewNodeAsync(node);
    }

    private async void OnPreviewTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || _selectedNode is not { } node) return;
        switch (e.Key)
        {
            case Key.Up:
            case Key.Down:
                e.Handled = true;
                var index = _visiblePreviewNodes.IndexOf(node);
                var next = index + (e.Key == Key.Up ? -1 : 1);
                if (next >= 0 && next < _visiblePreviewNodes.Count) await SelectTreeNodeAsync(_visiblePreviewNodes[next]);
                break;
            case Key.Left:
                e.Handled = true;
                if (node.IsExpanded) await TogglePreviewNodeAsync(node);
                else if (node.Parent is { } parent) await SelectTreeNodeAsync(parent);
                break;
            case Key.Right:
                e.Handled = true;
                if (node.Children?.FirstOrDefault() is { } child) await SelectTreeNodeAsync(child);
                else await ExpandPreviewNodeAsync(node);
                break;
            case Key.Enter when node.CanExpand:
                e.Handled = true;
                await TogglePreviewNodeAsync(node);
                break;
        }
    }

    private double ClampPreviewTreeWidth(double width)
    {
        var available = PreviewContentGrid.Bounds.Width;
        var maximum = available > 0 ? Math.Min(420, Math.Max(0, available - 321)) : 420;
        return Math.Clamp(width, Math.Min(160, maximum), maximum);
    }

    private void OnPreviewResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_previewResizePointer != null
            || e.GetCurrentPoint(PreviewTreeResizeHandle).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
        _previewResizePointer = e.Pointer;
        _previewResizeStartX = e.GetPosition(PreviewContentGrid).X;
        _previewResizeStartWidth = PreviewContentGrid.ColumnDefinitions[0].ActualWidth;
        e.Pointer.Capture(PreviewTreeResizeHandle);
        e.Handled = true;
    }

    private void OnPreviewResizeMoved(object? sender, PointerEventArgs e)
    {
        if (e.Pointer != _previewResizePointer) return;
        _previewTreeWidth = ClampPreviewTreeWidth(_previewResizeStartWidth + e.GetPosition(PreviewContentGrid).X - _previewResizeStartX);
        UpdatePreviewColumns();
        e.Handled = true;
    }

    private void OnPreviewResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer != _previewResizePointer) return;
        OnPreviewResizeMoved(sender, e);
        e.Pointer.Capture(null);
    }

    private void OnPreviewResizeCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => _previewResizePointer = null;
}
