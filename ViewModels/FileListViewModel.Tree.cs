using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    private sealed class TreeBranch
    {
        public IReadOnlyList<FileSystemEntry> RawEntries = [];
        public IReadOnlyList<FileSystemEntry> Entries = [];
        public CancellationTokenSource? Load;
        public bool IsLoading;
        public bool HasError;
    }

    private readonly Dictionary<string, TreeBranch> _treeBranches = new(StringComparer.Ordinal);
    private readonly HashSet<string> _treeSubscriptions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _treeRefreshGate = new(2);
    private string? _treeRootPath;
    private int _treeProjectionVersion;
    private bool _treeProjectionPending;
    private int _treeRestoreVersion;
    private bool _treeRestoringBranches;

    public IReadOnlyList<FileTreeRow> TreeRows { get; private set; } = [];
    public IReadOnlyList<FileTreeGroup> TreeGroups { get; private set; } = [];
    private IReadOnlyList<FileSystemEntry> _treeVisibleEntries = [];
    public bool IsTreeExpansionEnabled => CanShowTreeChildren;

    private bool CanShowTreeChildren => ViewMode == ViewMode.Tree && !IsHomePage && !IsSearchMode
        && !IsTagView && !IsAiView && !IsArchiveView && !IsRemoteView
        && CurrentPath != _fileService.TrashDirectory && Path.IsPathRooted(CurrentPath);

    private bool CanExpandTreeEntry(FileSystemEntry entry) => CanShowTreeChildren
        && entry.IsFolder && !entry.IsSymbolicLink && entry.IconKey != "app-bundle"
        && Path.IsPathRooted(entry.FullPath);

    public async Task ToggleTreeDirectoryAsync(FileSystemEntry entry)
    {
        if (!CanExpandTreeEntry(entry) || !TreeRows.Any(row => row.Entry.FullPath == entry.FullPath)) return;
        if (_treeBranches.ContainsKey(entry.FullPath))
        {
            foreach (var path in _treeBranches.Keys.Where(path => path == entry.FullPath
                         || path.StartsWith(entry.FullPath.TrimEnd('/') + '/', StringComparison.Ordinal)).ToArray())
                RemoveTreeBranch(path);
            RebuildTreeRows(restoreSelection: true, collapsedPath: entry.FullPath);
            return;
        }

        var branch = new TreeBranch();
        _treeBranches.Add(entry.FullPath, branch);
        await LoadTreeBranchAsync(entry.FullPath, branch);
    }

    public async Task ExpandTreeDirectoryAsync(FileSystemEntry entry)
    {
        if (CanExpandTreeEntry(entry) && !_treeBranches.ContainsKey(entry.FullPath))
            await ToggleTreeDirectoryAsync(entry);
    }

    public void CollapseTreeDirectory(FileSystemEntry entry)
    {
        if (!_treeBranches.ContainsKey(entry.FullPath)) return;
        foreach (var path in _treeBranches.Keys.Where(path => path == entry.FullPath
                     || path.StartsWith(entry.FullPath.TrimEnd('/') + '/', StringComparison.Ordinal)).ToArray())
            RemoveTreeBranch(path);
        RebuildTreeRows(restoreSelection: true, collapsedPath: entry.FullPath);
    }

    private void RemoveTreeBranch(string path)
    {
        if (!_treeBranches.Remove(path, out var branch)) return;
        branch.Load?.Cancel();
        branch.Load?.Dispose();
        branch.Load = null;
        if (_treeSubscriptions.Remove(path)) _directoryChangeNotifier?.UnregisterExpandedDirectory(this, path);
    }

    private async Task LoadTreeBranchAsync(string path, TreeBranch branch, bool publishRows = true)
    {
        branch.Load?.Cancel();
        branch.Load?.Dispose();
        var load = new CancellationTokenSource();
        branch.Load = load;
        branch.IsLoading = true;
        branch.HasError = false;
        var root = CurrentPath;
        var wasReadable = TreeRows.FirstOrDefault(row => row.Entry.FullPath == path).Entry?.IsReadable ?? true;
        var query = _sortFilter.CaptureQuery() with { GroupField = GroupField.None };
        if (publishRows) RebuildTreeRows();
        try
        {
            var pipeline = new FileListDataPipeline(publishIntermediateSnapshots: false);
            FileListSnapshot? snapshot = null;
            await foreach (var received in pipeline.LoadAsync(_fileService, path, query, load.Token))
                snapshot = received;
            if (snapshot == null) return;
            if (!wasReadable && snapshot.RawEntries.Count == 0)
                throw new UnauthorizedAccessException("无权读取此文件夹");
            var currentQuery = _sortFilter.CaptureQuery() with { GroupField = GroupField.None };
            if (snapshot.Query != currentQuery)
                snapshot = await Task.Run(() => FileListDataPipeline.Reproject(snapshot, currentQuery, load.Token), load.Token);
            if (load.IsCancellationRequested || _disposed || root != CurrentPath
                || !_treeBranches.TryGetValue(path, out var current) || !ReferenceEquals(current, branch)
                || !ReferenceEquals(branch.Load, load)) return;
            branch.RawEntries = snapshot.RawEntries;
            branch.Entries = snapshot.Entries;
            branch.IsLoading = false;
            if (StatusText.StartsWith("无法展开文件夹:", StringComparison.Ordinal)) StatusText = string.Empty;
            if (publishRows) RebuildTreeRows(restoreSelection: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!load.IsCancellationRequested && !_disposed && root == CurrentPath
                && _treeBranches.TryGetValue(path, out var current) && ReferenceEquals(current, branch))
            {
                branch.IsLoading = false;
                branch.HasError = true;
                StatusText = $"无法展开文件夹: {ex.Message}";
                if (publishRows) RebuildTreeRows();
            }
        }
        finally
        {
            if (ReferenceEquals(branch.Load, load)) branch.Load = null;
            load.Dispose();
        }
    }

    public Task RetryTreeDirectoryAsync(FileSystemEntry entry)
        => _treeBranches.TryGetValue(entry.FullPath, out var branch) && branch.HasError
            ? LoadTreeBranchAsync(entry.FullPath, branch) : Task.CompletedTask;

    internal async Task RefreshExpandedDirectoryFromNotificationAsync(string path)
    {
        if (_directoryNotificationsPaused || !CanShowTreeChildren
            || !_treeBranches.TryGetValue(path, out var branch)) return;
        await LoadTreeBranchAsync(path, branch);
    }

    private async Task RefreshOpenTreeBranchesAsync()
    {
        if (_directoryNotificationsPaused || !CanShowTreeChildren || _treeBranches.Count == 0) return;
        var root = CurrentPath;
        var paths = _treeBranches.Keys.ToArray();
        await Task.WhenAll(paths.Select(async path =>
        {
            await _treeRefreshGate.WaitAsync();
            try
            {
                if (root == CurrentPath && _treeBranches.TryGetValue(path, out var branch))
                    await LoadTreeBranchAsync(path, branch, publishRows: false);
            }
            finally { _treeRefreshGate.Release(); }
        }));
        if (!_disposed && root == CurrentPath && !_treeRestoringBranches)
            RebuildTreeRows(restoreSelection: true);
    }

    private void OnTreeLocationChanged()
    {
        if (_treeRootPath == CurrentPath) return;
        _treeRootPath = CurrentPath;
        _treeRestoreVersion++;
        _treeRestoringBranches = false;
        _treeProjectionVersion++;
        _treeProjectionPending = false;
        foreach (var path in _treeBranches.Keys.ToArray()) RemoveTreeBranch(path);
        TreeRows = [];
        TreeGroups = [];
        _treeVisibleEntries = [];
        OnPropertyChanged(nameof(TreeRows));
    }

    private void OnTreeModeChanged()
    {
        if (ViewMode != ViewMode.Tree)
        {
            _treeProjectionVersion++;
            _treeProjectionPending = false;
            _treeRestoreVersion++;
            _treeRestoringBranches = false;
            foreach (var (path, branch) in _treeBranches)
            {
                branch.Load?.Cancel();
                branch.Load?.Dispose();
                branch.Load = null;
                branch.IsLoading = false;
                branch.RawEntries = [];
                branch.Entries = [];
                if (_treeSubscriptions.Remove(path)) _directoryChangeNotifier?.UnregisterExpandedDirectory(this, path);
            }
            TreeRows = [];
            TreeGroups = [];
            _treeVisibleEntries = [];
            if (SelectedEntries.Count > 0)
            {
                var visible = Entries.Select(entry => entry.FullPath).ToHashSet(StringComparer.Ordinal);
                var selected = SelectedEntries.Where(entry => visible.Contains(entry.FullPath)).ToArray();
                ReplaceSelection(selected, selected.FirstOrDefault());
            }
            return;
        }
        _treeRestoringBranches = _treeBranches.Count > 0;
        RebuildTreeRows();
        _ = RestoreTreeBranchesAsync();
    }

    private async Task RestoreTreeBranchesAsync()
    {
        _treeRestoringBranches = _treeBranches.Count > 0;
        var version = ++_treeRestoreVersion;
        try { await RefreshOpenTreeBranchesAsync(); }
        finally
        {
            if (version == _treeRestoreVersion && ViewMode == ViewMode.Tree)
            {
                _treeRestoringBranches = false;
                RebuildTreeRows(restoreSelection: true);
            }
        }
    }

    private void BeginTreeQueryChange()
    {
        if (ViewMode != ViewMode.Tree) return;
        _treeProjectionPending = true;
        _treeProjectionVersion++;
    }

    private async Task ReprojectTreeBranchesAsync()
    {
        if (ViewMode != ViewMode.Tree) return;
        var version = _treeProjectionVersion;
        var root = CurrentPath;
        var query = _sortFilter.CaptureQuery() with { GroupField = GroupField.None };
        var inputs = _treeBranches.Select(pair => (pair.Key, pair.Value, pair.Value.RawEntries)).ToArray();
        var results = await Task.Run(() =>
        {
            var processor = SortFilterViewModel.CreateSnapshotProcessor(query);
            var comparer = processor.BuildEntriesComparer();
            return inputs.Select(input => (input.Key, input.Value,
                Entries: (IReadOnlyList<FileSystemEntry>)input.RawEntries.Where(processor.PassesSnapshotFilter)
                    .OrderBy(entry => entry, comparer).ToArray())).ToArray();
        });
        if (_disposed || version != _treeProjectionVersion || root != CurrentPath) return;
        foreach (var (path, branch, entries) in results)
            if (_treeBranches.TryGetValue(path, out var current) && ReferenceEquals(current, branch))
                branch.Entries = entries;
        _treeProjectionPending = false;
        RebuildTreeRows(restoreSelection: true);
    }

    private void RebuildTreeRows(bool restoreSelection = false, string? collapsedPath = null)
    {
        if (ViewMode != ViewMode.Tree || _treeProjectionPending) return;
        if (_treeRootPath != CurrentPath) OnTreeLocationChanged();
        if (!CanShowTreeChildren)
        {
            foreach (var path in _treeBranches.Keys.ToArray()) RemoveTreeBranch(path);
            TreeRows = [];
            TreeGroups = [];
            _treeVisibleEntries = [];
            OnPropertyChanged(nameof(TreeRows));
            return;
        }
        var rows = new List<FileTreeRow>(Entries.Count);
        var visibleEntries = new List<FileSystemEntry>(Entries.Count);
        var groups = new List<FileTreeGroup>();
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        if (GroupField == GroupField.None)
            AddEntries(Entries, 0);
        else
            foreach (var group in Groups)
            {
                var start = rows.Count;
                AddEntries(group.Entries, 0);
                groups.Add(new FileTreeGroup(group.Name, group.Entries.Count, rows.Count - start));
            }

        if (!_treeRestoringBranches)
            foreach (var path in _treeBranches.Keys.Where(path => !reachable.Contains(path)).ToArray())
                RemoveTreeBranch(path);
        TreeRows = rows;
        TreeGroups = groups;
        _treeVisibleEntries = visibleEntries;
        UpdateTreeSubscriptions();
        OnPropertyChanged(nameof(TreeRows));
        if (restoreSelection && SelectedEntries.Count > 0)
        {
            var selectedPaths = SelectedEntries.Select(entry => entry.FullPath).ToHashSet(StringComparer.Ordinal);
            var selected = rows.Select(row => row.Entry).Where(entry => selectedPaths.Contains(entry.FullPath)).ToArray();
            if (selected.Length == 0 && collapsedPath != null)
                selected = rows.Select(row => row.Entry).Where(entry => entry.FullPath == collapsedPath).ToArray();
            ReplaceSelection(selected, selected.FirstOrDefault());
        }

        void AddEntries(IEnumerable<FileSystemEntry> entries, int depth)
        {
            foreach (var entry in entries)
            {
                var canExpand = CanExpandTreeEntry(entry);
                TreeBranch? branch = null;
                var expanded = canExpand && _treeBranches.TryGetValue(entry.FullPath, out branch);
                rows.Add(new FileTreeRow(entry, depth, canExpand, expanded,
                    expanded && branch!.IsLoading, expanded && branch!.HasError));
                visibleEntries.Add(entry);
                if (!expanded) continue;
                reachable.Add(entry.FullPath);
                AddEntries(branch!.Entries, depth + 1);
            }
        }
    }

    private void UpdateTreeSubscriptions()
    {
        if (_directoryChangeNotifier == null) return;
        var wanted = CanShowTreeChildren && !_directoryNotificationsPaused
            ? _treeBranches.Keys.ToHashSet(StringComparer.Ordinal) : [];
        foreach (var path in _treeSubscriptions.Where(path => !wanted.Contains(path)).ToArray())
        {
            _treeSubscriptions.Remove(path);
            _directoryChangeNotifier.UnregisterExpandedDirectory(this, path);
        }
        foreach (var path in wanted)
            if (_treeSubscriptions.Add(path)) _directoryChangeNotifier.RegisterExpandedDirectory(this, path);
    }

    private void StopTreeWork()
    {
        _treeProjectionVersion++;
        _treeRestoreVersion++;
        _treeRestoringBranches = false;
        foreach (var path in _treeBranches.Keys.ToArray()) RemoveTreeBranch(path);
        TreeRows = [];
        TreeGroups = [];
        _treeVisibleEntries = [];
    }

    private IReadOnlyList<FileSystemEntry> NormalizeTreeOperationEntries(IEnumerable<FileSystemEntry> entries)
    {
        var selected = entries.ToArray();
        if (ViewMode != ViewMode.Tree || selected.Length < 2) return selected;
        var selectedFolders = selected.Where(entry => entry.IsFolder)
            .Select(entry => entry.FullPath.TrimEnd('/') + '/')
            .ToArray();
        return selected.Where(entry => !selectedFolders.Any(folder =>
            entry.FullPath.StartsWith(folder, StringComparison.Ordinal))).ToArray();
    }

    private void PauseTreeWork()
    {
        _treeRestoreVersion++;
        _treeRestoringBranches = _treeBranches.Count > 0;
        foreach (var (path, branch) in _treeBranches)
        {
            branch.Load?.Cancel();
            branch.Load?.Dispose();
            branch.Load = null;
            branch.IsLoading = false;
            branch.RawEntries = [];
            branch.Entries = [];
            if (_treeSubscriptions.Remove(path)) _directoryChangeNotifier?.UnregisterExpandedDirectory(this, path);
        }
        RebuildTreeRows();
    }
}
