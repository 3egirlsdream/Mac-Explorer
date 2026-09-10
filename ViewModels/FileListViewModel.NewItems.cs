using Avalonia.Media;
using MacExplorer.Assets;
using MacExplorer.Models;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    private sealed record NewItemDefinition(string Label, string? Extension, IImage Icon, params string[] Apps);

    // Both menus use this catalog, including application availability and creation actions.
    private static readonly Lazy<NewItemDefinition[]> NewItemDefinitions = new(() =>
    [
        new("新建文件夹", null, NewItemIcons.Folder),
        new("新建文本文件", ".txt", NewItemIcons.Text),
        new("新建 Markdown", ".md", NewItemIcons.Markdown),
        new("新建 JSON 文件", ".json", NewItemIcons.Json),
        new("新建 Word 文稿", ".docx", NewItemIcons.Word, "com.microsoft.Word", "com.kingsoft.wpsoffice.mac"),
        new("新建 Excel 表格", ".xlsx", NewItemIcons.Excel, "com.microsoft.Excel", "com.kingsoft.wpsoffice.mac"),
        new("新建 PowerPoint 演示文稿", ".pptx", NewItemIcons.PowerPoint, "com.microsoft.Powerpoint", "com.kingsoft.wpsoffice.mac"),
        new("新建 Pages 文稿", ".pages", NewItemIcons.Pages, "com.apple.iWork.Pages"),
        new("新建 Numbers 表格", ".numbers", NewItemIcons.Numbers, "com.apple.iWork.Numbers"),
        new("新建 Keynote 演示文稿", ".key", NewItemIcons.Keynote, "com.apple.iWork.Keynote")
    ]);

    private IReadOnlyList<ContextMenuAction>? _newItemActions;
    private Task? _loadNewItemActionsTask;

    public IReadOnlyList<ContextMenuAction> NewItemActions =>
        _newItemActions ??= BuildNewItemActions(NewItemDefinitions.Value.Where(item => item.Apps.Length == 0));

    public Task LoadNewItemActionsAsync() => _loadNewItemActionsTask ??= LoadAvailableNewItemActionsAsync();

    private async Task LoadAvailableNewItemActionsAsync()
    {
        if (_contextMenuService == null)
            return;

        var available = await Task.Run(() => NewItemDefinitions.Value
            .Where(item => item.Apps.Length == 0 || item.Apps.Any(_contextMenuService.IsAppInstalled))
            .ToArray());
        _newItemActions = BuildNewItemActions(available);
        OnPropertyChanged(nameof(NewItemActions));
    }

    private IReadOnlyList<ContextMenuAction> BuildNewItemActions(IEnumerable<NewItemDefinition> definitions)
    {
        var actions = new List<ContextMenuAction>();
        var hasAppItems = false;
        foreach (var item in definitions)
        {
            if (item.Apps.Length > 0 && !hasAppItems)
            {
                actions.Add(ContextMenuAction.Separator);
                hasAppItems = true;
            }
            actions.Add(new ContextMenuAction
            {
                Label = item.Label,
                IconImage = item.Icon,
                Tag = item.Extension,
                ShortcutText = item.Extension == null ? "⇧⌘N" : "",
                Execute = item.Extension == null
                    ? CreateNewFolderAsync
                    : () => CreateNewFileAsync(item.Extension)
            });
        }
        return actions;
    }
}
