using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views.Dialogs;

namespace MacExplorer.Views;

/// <summary>Shared item behavior for homepage previews and expanded folders. Capture paths, never mutable selection.</summary>
public sealed class HomeItemActions : IDisposable
{
    private readonly MacExplorer.Controls.HomeThumbnailCache _thumbnails;
    private readonly bool _ownsThumbnails;
    private readonly Control _owner;
    private readonly FileListViewModel _navigation;
    private readonly IFileService _files;
    private readonly IFileTagService _tags;
    private readonly HomeWorkspaceService _workspace;
    private readonly HomeScriptRunner _runner;
    private readonly Action<string> _status;

    public HomeItemActions(Control owner, FileListViewModel navigation, IFileService files, IFileTagService tags,
        HomeWorkspaceService workspace, HomeScriptRunner runner, Action<string> status, MacExplorer.Controls.HomeThumbnailCache? thumbnails = null)
    {
        _thumbnails = thumbnails ?? new();
        _ownsThumbnails = thumbnails == null;
        (_owner, _navigation, _files, _tags, _workspace, _runner, _status) =
            (owner, navigation, files, tags, workspace, runner, status);
    }

    public void Dispose() { if (_ownsThumbnails) _thumbnails.Dispose(); }

    public HomeItemActions ForOwner(Control owner, Action<string> status)
        => new(owner, _navigation, _files, _tags, _workspace, _runner, status, _thumbnails);

    public async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status(ex.Message); _navigation.StatusText = ex.Message; }
    }

    public Button CreateItemButton(FileSystemEntry entry, FileTag? tag = null, bool compact = false,
        Action? beforeOpen = null, DateTime? addedUtc = null)
    {
        var button = new Button { DataContext = entry, Classes = { "ghost", compact ? "home-recent-item" : "home-grid-item" },
            Opacity = entry.IsReadable ? 1 : 0.5 };
        var image = new MacExplorer.Controls.HomeFileImage(entry, compact ? 30 : 48, _navigation.GetListThumbnailAsync, _thumbnails)
        { HorizontalAlignment = HorizontalAlignment.Center };
        var name = new TextBlock { Text = entry.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        if (compact)
        {
            button.Width = 192;
            button.Margin = new Thickness(0, 0, 6, 2);
            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("32,*") };
            content.Children.Add(image);
            var labels = new StackPanel { Spacing = 2, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(name);
            var timestamp = addedUtc ?? entry.Created.ToUniversalTime();
            var time = new TextBlock { Text = FormatAddedTime(timestamp, DateTime.UtcNow), Classes = { "home-secondary" } };
            ToolTip.SetTip(time, $"添加于 {timestamp.ToLocalTime():yyyy-MM-dd HH:mm}");
            labels.Children.Add(time);
            Grid.SetColumn(labels, 1); content.Children.Add(labels);
            button.Content = content;
        }
        else
        {
            name.TextAlignment = TextAlignment.Center;
            button.Content = CreateGridContent(image, name);
        }
        AutomationProperties.SetName(button, entry.Name + (entry.IsReadable ? "" : "，路径不可用"));
        ToolTip.SetTip(button, entry.FullPath + (entry.IsReadable ? "" : "\n项目已移动、删除或暂时不可访问"));
        button.Click += async (_, _) => await GuardAsync(() => OpenAsync(entry, beforeOpen));
        var menu = new ContextMenu();
        menu.Opened += (_, _) =>
        {
            try { PopulateItemMenu(menu, entry, tag, beforeOpen); }
            catch (Exception ex) { _status(ex.Message); }
        };
        button.ContextMenu = menu;
        return button;
    }

    internal static string FormatAddedTime(DateTime addedUtc, DateTime nowUtc)
    {
        var elapsed = nowUtc - addedUtc;
        if (elapsed.TotalMinutes < 1) return "最近添加";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes} 分钟前";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours} 小时前";
        if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays} 天前";
        return addedUtc.ToLocalTime().ToString("yyyy-MM-dd");
    }

    internal static Grid CreateGridContent(Control image, TextBlock name)
    {
        // Reserve the same square even when a thumbnail's natural aspect ratio is wide or tall.
        var content = new Grid { RowDefinitions = new RowDefinitions("48,16"), RowSpacing = 3,
            VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new Border { Width = 48, Height = 48, Child = image,
            HorizontalAlignment = HorizontalAlignment.Center });
        name.TextAlignment = TextAlignment.Center;
        name.VerticalAlignment = VerticalAlignment.Center;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetRow(name, 1);
        content.Children.Add(name);
        return content;
    }

    private async Task OpenAsync(FileSystemEntry entry, Action? beforeOpen)
    {
        if (!await Task.Run(() => _files.ExistsAsync(entry.FullPath)))
            throw new FileNotFoundException("项目已移动、删除或暂时不可访问。", entry.FullPath);
        beforeOpen?.Invoke();
        // Preserve the app's bundle/archive/normal-file activation behavior instead of invoking a shell here.
        await _navigation.OpenEntryAsync(entry);
    }

    private MenuItem ActionItem(string title, Func<Task> action, bool enabled = true)
    {
        var item = new MenuItem { Header = title, IsEnabled = enabled };
        item.Click += async (_, _) => await GuardAsync(action);
        return item;
    }

    private void PopulateItemMenu(ContextMenu menu, FileSystemEntry entry, FileTag? tag, Action? beforeOpen)
    {
        menu.Items.Clear();
        menu.Items.Add(ActionItem("打开", () => OpenAsync(entry, beforeOpen), entry.IsReadable));
        menu.Items.Add(ActionItem("在文件列表中显示", async () =>
        {
            beforeOpen?.Invoke();
            await _navigation.RevealFileAsync(entry);
        }, entry.IsReadable));

        if (!entry.IsDirectory && Path.IsPathFullyQualified(entry.FullPath))
        {
            var commands = _workspace.GetCommands(entry.FullPath);
            if (HomeScriptCommand.IsScript(entry.FullPath) || commands.Count > 0)
            {
                menu.Items.Add(new Separator());
                if (commands.Count > 0)
                {
                    var run = new MenuItem { Header = "运行命令", IsEnabled = entry.IsReadable };
                    foreach (var command in commands)
                    {
                        var captured = command;
                        var item = ActionItem(command.Name, () => RunCommandAsync(entry.FullPath, captured));
                        item.Icon = HomeScriptMenuIcons.Create(command.MenuIcon);
                        ToolTip.SetTip(item, command.Command);
                        run.Items.Add(item);
                    }
                    ContextMenuPopupStyler.Attach(run);
                    menu.Items.Add(run);
                }
                menu.Items.Add(ActionItem("编辑脚本命令…", () => EditCommandsAsync(entry.FullPath)));
            }
        }
        if (tag != null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(ActionItem("从此收藏夹移除", async () =>
            {
                var result = await Task.Run(() => _tags.SetTagAsync([entry.FullPath], tag, false));
                _status(result.PendingFiles > 0 ? "已移除收藏，等待同步 Finder。" : "已移除收藏，原文件保持不变。");
            }));
        }
    }

    public ContextMenu CreateFolderMenu(FileTag tag, Action? beforeNavigate = null)
    {
        var menu = new ContextMenu();
        menu.Items.Add(ActionItem("添加文件…", () => AddAsync(tag, folders: false)));
        menu.Items.Add(ActionItem("添加文件夹…", () => AddAsync(tag, folders: true)));
        menu.Items.Add(ActionItem("在文件列表中打开", async () =>
        {
            beforeNavigate?.Invoke();
            await _navigation.NavigateToAsync(tag.VirtualPath);
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(ActionItem("重置收藏夹大小", () =>
        {
            _workspace.SaveLayout(tag, new HomeFolderLayout());
            return Task.CompletedTask;
        }));
        if (tag.IsCustom)
            menu.Items.Add(ActionItem("重命名标签…", async () =>
            {
                if (TopLevel.GetTopLevel(_owner) is not Window owner) return;
                var name = await new HomeNameDialog("重命名标签", tag.Name).ShowDialog<string?>(owner);
                if (name != null) await Task.Run(() => _tags.RenameTagAsync(tag, name));
            }));
        return menu;
    }

    public async Task AddAsync(FileTag tag, bool folders)
    {
        if (TopLevel.GetTopLevel(_owner) is not { } top) return;
        IReadOnlyList<IStorageItem> items;
        if (folders)
            items = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                { Title = $"收藏文件夹到“{tag.Name}”", AllowMultiple = true });
        else
            items = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                { Title = $"收藏文件到“{tag.Name}”", AllowMultiple = true });
        try { await AddPathsAsync(tag, items.Where(i => i.Path.IsFile).Select(i => i.Path.LocalPath).ToArray()); }
        finally { foreach (var item in items) item.Dispose(); }
    }

    public async Task AddPathsAsync(FileTag tag, IReadOnlyList<string> paths)
    {
        var local = paths.Where(Path.IsPathFullyQualified).Distinct(StringComparer.Ordinal).ToArray();
        if (local.Length == 0) return;
        var result = await Task.Run(() => _tags.SetTagAsync(local, tag, true));
        _status(result.PendingFiles > 0 ? "已收藏，部分文件等待同步 Finder。" : "已收藏，原文件位置保持不变。");
    }

    public async Task EditCommandsAsync(string path)
    {
        if (TopLevel.GetTopLevel(_owner) is Window owner)
            await new HomeScriptCommandsDialog(path, _workspace).ShowDialog(owner);
    }

    private async Task RunCommandAsync(string path, HomeScriptCommand command)
    {
        await _runner.RunAsync(path, command);
        _ = _workspace.RecordUseAsync(path);
        _status($"已在默认终端中打开：{command.Name}");
    }

    public Task<IReadOnlyList<FileSystemEntry>> LoadEntriesAsync(IReadOnlyList<string> paths, CancellationToken ct)
        => Task.Run<IReadOnlyList<FileSystemEntry>>(async () =>
        {
            var entries = new List<FileSystemEntry>(paths.Count);
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                FileSystemEntry? entry;
                try { entry = await _files.GetEntryAsync(path).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { entry = null; }
                ct.ThrowIfCancellationRequested();
                entries.Add(entry ?? new FileSystemEntry
                {
                    FullPath = path, Name = Path.GetFileName(path), Extension = Path.GetExtension(path),
                    IsReadable = false, IconKey = "file-generic"
                });
            }
            return entries;
        }, ct);
}
