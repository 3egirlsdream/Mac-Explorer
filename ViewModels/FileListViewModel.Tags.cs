using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.Services;
using Icons = MacExplorer.Assets.Icons;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    internal IFileTagService? FileTagService => _fileTagService;
    public event Action? NewTagRequested;
    public bool ShowToolbarTags
    {
        get => _settingsService?.Get("show_toolbar_tags", true) ?? _showToolbarTags;
        set
        {
            if (ShowToolbarTags == value) return;
            _showToolbarTags = value;
            _settingsService?.Set("show_toolbar_tags", value);
            OnPropertyChanged();
        }
    }
    private bool _showToolbarTags = true;

    public string TagDeleteConfirmation => MacExplorer.Services.Impl.LocalizationText.Get(
        "确定要删除标签“{0}”及其 {1} 个自定义子标签吗？这会移除文件上的这些标签，原始文件保留。系统颜色标签将移回顶层。",
        PendingDeleteTagName, FileTagTree.Descendants(SidebarTags, PendingDeleteTagName).Count(t => t.IsCustom));

    private IReadOnlyList<string> _newTagPaths = [];

    private void OnTagRenamed(object? sender, TagRenamedEventArgs e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (_disposed || IsBrowseOnly) return;
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
        if (IsBrowseOnly || _fileTagService == null) return;
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
        if (IsBrowseOnly || _fileTagService == null) return;
        await RunTagActionAsync(async () =>
        {
            var result = await _fileTagService.SetTagAsync(paths, tag, applied);
            StatusText = result.PendingFiles > 0
                ? MacExplorer.Services.Impl.LocalizationText.Get("标签已保存，{0} 个文件等待同步 Finder", result.PendingFiles)
                : applied ? MacExplorer.Services.Impl.LocalizationText.Get("已添加标签“{0}”", tag.DisplayName) : MacExplorer.Services.Impl.LocalizationText.Get("已移除标签“{0}”", tag.DisplayName);
        });
    }

    private async Task RunTagActionAsync(Func<Task> action)
    {
        if (IsBrowseOnly || _fileTagService == null) return;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = MacExplorer.Services.Impl.LocalizationText.Get("标签操作失败：{0}", ex.Message); }
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
                Label = tag.DisplayName,
                IconSvg = Icons.Tag,
                IconColor = tag.ColorHex,
                IsCheckable = true,
                IsChecked = all,
                IsIndeterminate = count > 0 && !all,
                Execute = () => SetFileTagAsync(paths, tag, !all)
            };
        }).ToList();
        actions.Insert(0, ContextMenuAction.Separator);
        actions.Insert(0, new ContextMenuAction
        {
            Label = MacExplorer.Services.Impl.LocalizationText.Get("新建标签…"), IconSvg = Icons.Plus,
            Execute = () =>
            {
                _newTagPaths = paths;
                NewTagRequested?.Invoke();
                return Task.CompletedTask;
            }
        });
        return new ContextMenuAction { Label = MacExplorer.Services.Impl.LocalizationText.Get("标签"), IconSvg = Icons.Tag, SubItems = actions, TagTreePaths = paths };
    }
}
