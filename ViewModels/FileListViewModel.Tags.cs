using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.Services;
using Icons = MacExplorer.Assets.Icons;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    public event Action? NewTagRequested;
    private IReadOnlyList<string> _newTagPaths = [];

    private void OnTagRenamed(object? sender, TagRenamedEventArgs e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (_disposed) return;
            _navigation.RenameTagHistory(e.OldName, e.NewName);
            if (CurrentTag is { } current && string.Equals(current.Name, e.OldName, StringComparison.OrdinalIgnoreCase))
            {
                if (e.NewName == null) GoHome();
                else await HandleTagNavigationAsync(TagPathHelper.Build(e.NewName, FileTagKind.Custom), false);
            }
        });
    }

    public async Task CreateTagAsync(string name)
    {
        var paths = _newTagPaths;
        _newTagPaths = [];
        if (_fileTagService == null) return;
        await RunTagActionAsync(async () =>
        {
            var tag = await _fileTagService.CreateTagAsync(name);
            if (paths.Count > 0) await SetFileTagAsync(paths, tag, true);
        });
    }

    public void CancelNewTag() => _newTagPaths = [];
    public Task RenameTagAsync(FileTag tag, string name) => RunTagActionAsync(() => _fileTagService!.RenameTagAsync(tag, name));
    public Task SetTagColorAsync(FileTag tag, int colorId) => RunTagActionAsync(() => _fileTagService!.SetTagColorAsync(tag, colorId));
    public Task SetTagPinnedAsync(FileTag tag, bool pinned) => RunTagActionAsync(() => _fileTagService!.SetTagPinnedAsync(tag, pinned));

    public async Task SetFileTagAsync(IReadOnlyList<string> paths, FileTag tag, bool applied)
    {
        if (_fileTagService == null) return;
        await RunTagActionAsync(async () =>
        {
            var result = await _fileTagService.SetTagAsync(paths, tag, applied);
            StatusText = result.PendingFiles > 0
                ? $"标签已保存，{result.PendingFiles} 个文件等待同步 Finder"
                : applied ? $"已添加标签“{tag.Name}”" : $"已移除标签“{tag.Name}”";
        });
    }

    private async Task RunTagActionAsync(Func<Task> action)
    {
        if (_fileTagService == null) return;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = $"标签操作失败：{ex.Message}"; }
    }

    private async Task<ContextMenuAction> BuildTagsContextMenuAsync(FileSystemEntry entry)
    {
        var paths = SelectedEntries.Any(e => e.FullPath == entry.FullPath)
            ? SelectedEntries.Select(e => e.FullPath).Distinct().ToArray() : [entry.FullPath];
        var memberships = new List<HashSet<string>>();
        if (_fileTagService != null)
            foreach (var path in paths)
                memberships.Add((await _fileTagService.GetFileTagsAsync(path)).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var tags = _fileTagService == null ? FileTagCatalog.FinderColors : await _fileTagService.GetSidebarTagsAsync();
        var actions = tags.Select(tag =>
        {
            var count = memberships.Count(set => set.Contains(tag.Name));
            var all = count > 0 && count == paths.Length;
            return new ContextMenuAction
            {
                Label = tag.Name,
                IsCheckable = true,
                IsChecked = all,
                IsIndeterminate = count > 0 && !all,
                Execute = () => SetFileTagAsync(paths, tag, !all)
            };
        }).ToList();
        actions.Add(ContextMenuAction.Separator);
        actions.Add(new ContextMenuAction
        {
            Label = "新建标签…", IconSvg = Icons.Plus,
            Execute = () =>
            {
                _newTagPaths = paths;
                NewTagRequested?.Invoke();
                return Task.CompletedTask;
            }
        });
        return new ContextMenuAction { Label = "标签", IconSvg = Icons.Tag, SubItems = actions };
    }
}
