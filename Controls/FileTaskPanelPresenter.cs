using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Shape = Avalonia.Controls.Shapes.Shape;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Icons = MacExplorer.Assets.Icons;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Controls;

/// <summary>Shared task list for the main panel and the standalone window.</summary>
internal sealed class FileTaskPanelPresenter
{
    private readonly IBackgroundTaskManager _tasks;
    private readonly IFileOperationHistoryService? _history;
    private readonly Dictionary<string, TaskRow> _rows = [];
    private readonly StackPanel _list = new();
    private string _order = "";
    internal Control View => _list;

    private sealed record TaskRow(Border View, TextBlock Title, TextBlock Metrics, TextBlock Percentage,
        ProgressBar Progress, Grid Grid, string Actions, Action Arrange);

    internal FileTaskPanelPresenter(IBackgroundTaskManager tasks, IFileOperationHistoryService? history)
    {
        _tasks = tasks;
        _history = history;
        Refresh();
    }

    internal void Refresh()
    {
        var tasks = _tasks.Tasks;
        var order = string.Join(",", tasks.Select(t => t.Id));
        foreach (var task in tasks)
        {
            var undoable = task.HistoryId is { } id && _history?.Records.Any(r => r.Id == id && r.HasUndoableItems) == true;
            var actions = $"{task.State}:{task.CanRetry}:{task.CanCancel}:{undoable}";
            if (!_rows.TryGetValue(task.Id, out var row) || row.Actions != actions)
            {
                var created = CreateTask(task, undoable, actions);
                if (row != null)
                {
                    var index = _list.Children.IndexOf(row.View);
                    if (index >= 0) _list.Children[index] = created.View;
                }
                _rows[task.Id] = row = created;
            }
            row.Title.Text = Title(task);
            ToolTip.SetTip(row.Title, row.Title.Text);
            row.Metrics.Text = Metrics(task);
            row.Metrics.IsVisible = ShowProgress(task) && !string.IsNullOrEmpty(row.Metrics.Text);
            var percentage = DisplayProgress(task);
            row.Percentage.Text = task.State == BackgroundTaskState.Waiting ? LocalizationText.Get("等待开始")
                : ShowProgress(task) ? $"{percentage:F0}%" : StateText(task);
            row.Progress.Value = percentage;
            row.Progress.IsVisible = ShowProgress(task);
            row.Percentage.IsVisible = true;
            row.Percentage.Classes.Set("state", !ShowProgress(task));
            row.Arrange();
            ToolTip.SetTip(row.View, task.ErrorDetail ?? task.ErrorMessage ?? task.CurrentFile);
            ToolTip.SetTip(row.Metrics, task.State == BackgroundTaskState.Running && task.Remaining is { } remaining
                ? LocalizationText.Get("剩余 {0} 秒", Math.Ceiling(remaining.TotalSeconds)) : task.CurrentFile);
        }
        AlignMetrics();
        if (_order != order || _list.Children.Count == 0)
        {
            _list.Children.Clear();
            foreach (var task in tasks) _list.Children.Add(_rows[task.Id].View);
            if (tasks.Count == 0)
                _list.Children.Add(new TextBlock { Text = LocalizationText.Get("没有任务"), Classes = { "task-meta" },
                    Margin = new Thickness(16, 24), HorizontalAlignment = HorizontalAlignment.Center });
            _order = order;
        }
        foreach (var key in _rows.Keys.Except(tasks.Select(t => t.Id)).ToArray()) _rows.Remove(key);
    }

    private void AlignMetrics()
    {
        var rows = _rows.Values.Where(row => row.View.Bounds.Width >= 540 && row.Progress.IsVisible).ToArray();
        if (rows.Length == 0) return;
        foreach (var row in rows) row.Metrics.Measure(Size.Infinity);
        var width = Math.Min(rows.Max(row => row.Metrics.DesiredSize.Width), rows.Min(row => row.View.Bounds.Width) * .43);
        foreach (var row in rows)
            if (Math.Abs(row.Grid.ColumnDefinitions[2].Width.Value - width) > .1 || row.Grid.ColumnDefinitions[2].Width.IsAuto)
                row.Grid.ColumnDefinitions[2].Width = new GridLength(width);
    }

    private TaskRow CreateTask(BackgroundTaskInfo task, bool undoable, string signature)
    {
        var grid = new Grid { ColumnSpacing = 12, VerticalAlignment = VerticalAlignment.Center };
        var active = task.State is BackgroundTaskState.Running or BackgroundTaskState.Pausing;
        var kind = DisplayKind(task);
        Control icon;
        if (kind is BackgroundTaskKind.Copy or BackgroundTaskKind.Move or BackgroundTaskKind.Compress
            or BackgroundTaskKind.RemoteDownload or BackgroundTaskKind.RemoteUpload)
        {
            var outline = FileTaskIcons.Create(TaskIcon(kind), 28);
            var path = (Avalonia.Controls.Shapes.Path)outline.Child!;
            path.Bind(Shape.StrokeProperty, path.GetResourceObservable(active ? "AccentBrush" : "TextMutedBrush"));
            icon = outline;
        }
        else
        {
            var filled = new PathIcon { Data = Geometry.Parse(TaskIcon(kind)), Width = 24, Height = 24 };
            filled.Bind(PathIcon.ForegroundProperty, filled.GetResourceObservable(active ? "AccentBrush" : "TextMutedBrush"));
            icon = filled;
        }
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetRowSpan(icon, 3); grid.Children.Add(icon);
        var title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Classes = { "task-title" } };
        Grid.SetColumn(title, 1); grid.Children.Add(title);
        var metrics = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Classes = { "task-meta" } };
        if (task.State is BackgroundTaskState.Failed or BackgroundTaskState.NeedsAttention)
            metrics.Bind(TextBlock.ForegroundProperty, metrics.GetResourceObservable("DangerBrush"));
        var percentage = new TextBlock { Classes = { "task-percentage" }, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };
        if (task.State is BackgroundTaskState.Failed or BackgroundTaskState.NeedsAttention && !ShowProgress(task))
            percentage.Bind(TextBlock.ForegroundProperty, percentage.GetResourceObservable("DangerBrush"));
        grid.Children.Add(metrics); grid.Children.Add(percentage);
        var actions = new Grid { ColumnDefinitions = new("28,28"), ColumnSpacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRowSpan(actions, 3); grid.Children.Add(actions);
        Button? primary = null;
        if (task.IsQueuedFileTask && task.State == BackgroundTaskState.Running)
            primary = ActionButton("暂停", FileTaskIcons.Pause, () => _tasks.PauseTask(task.Id));
        if (task.State is BackgroundTaskState.Paused or BackgroundTaskState.Pausing)
            primary = ActionButton("继续", FileTaskIcons.Play, () => _tasks.ResumeTask(task.Id));
        if (task.CanRetry && task.State is BackgroundTaskState.Failed or BackgroundTaskState.NeedsAttention)
            primary = ActionButton("重试", FileTaskIcons.Retry, () => _tasks.RetryTask(task.Id));
        if ((task.IsTerminal || task.State == BackgroundTaskState.NeedsAttention) && undoable && primary == null)
            primary = UndoButton(task.HistoryId!.Value);
        if (primary != null) actions.Children.Add(primary);
        Button? dismiss = null;
        if (task.IsActive && task.CanCancel && task.State != BackgroundTaskState.Cancelling)
            dismiss = ActionButton("取消任务", FileTaskIcons.Close, () => _tasks.CancelTask(task.Id));
        if (task.IsTerminal)
            dismiss = ActionButton("移除任务", FileTaskIcons.Close, () => _tasks.RemoveTask(task.Id));
        if (dismiss != null) { Grid.SetColumn(dismiss, 1); actions.Children.Add(dismiss); }
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Value = task.Progress,
            Classes = { "task-progress" }, Margin = new Thickness(0, 6, 0, 0) };
        if (!active) progress.Classes.Add("muted");
        grid.Children.Add(progress);
        var border = new Border { Child = grid, Classes = { "file-task-row" } };
        if (task.State == BackgroundTaskState.Waiting)
        {
            border.ContextMenu = new ContextMenu();
            border.ContextMenu.Items.Add(MenuAction("暂停", () => _tasks.PauseTask(task.Id)));
            border.ContextMenu.Items.Add(MenuAction("上移", () => _tasks.MoveTask(task.Id, -1)));
            border.ContextMenu.Items.Add(MenuAction("下移", () => _tasks.MoveTask(task.Id, 1)));
        }
        var layout = (Wide: false, Progress: false);
        var arranged = false;
        void ArrangeRow()
        {
            var wide = border.Bounds.Width >= 540;
            var hasProgress = ShowProgress(task);
            if (arranged && layout == (wide, hasProgress)) return;
            arranged = true; layout = (wide, hasProgress);
            grid.RowDefinitions = new(hasProgress ? "Auto,Auto,Auto" : "Auto");
            grid.ColumnSpacing = wide ? 16 : 10;
            grid.ColumnDefinitions = new(wide ? hasProgress ? "38,*,Auto,56,72" : "38,*,Auto,Auto,72" : "28,*,Auto,64");
            Grid.SetRowSpan(icon, hasProgress ? 3 : 1);
            Grid.SetRowSpan(actions, hasProgress ? 3 : 1);
            Grid.SetColumn(actions, wide ? 4 : 3);
            Grid.SetColumn(percentage, wide ? 3 : 2);
            Grid.SetRow(percentage, 0); Grid.SetColumnSpan(percentage, 1);
            percentage.HorizontalAlignment = HorizontalAlignment.Right;
            percentage.MaxWidth = wide ? double.PositiveInfinity : hasProgress ? 42 : 90;
            Grid.SetColumn(metrics, wide ? 2 : 1); Grid.SetRow(metrics, hasProgress && !wide ? 1 : 0);
            Grid.SetColumnSpan(metrics, wide || !hasProgress ? 1 : 2);
            metrics.Margin = wide ? new Thickness(0, 0, 8, 0) : new Thickness(0, 3, 0, 0);
            Grid.SetColumn(progress, 1); Grid.SetColumnSpan(progress, wide ? 3 : 2);
            Grid.SetRow(progress, hasProgress && !wide ? 2 : 1);
        }
        border.SizeChanged += (_, _) => { ArrangeRow(); AlignMetrics(); };
        ArrangeRow();
        return new(border, title, metrics, percentage, progress, grid, signature, ArrangeRow);
    }

    private Button UndoButton(Guid id)
    {
        var button = new Button { Content = new PathIcon { Data = Geometry.Parse(Icons.ArrowUndo), Width = 16, Height = 16 },
            Classes = { "icon-action" } };
        AutomationProperties.SetName(button, LocalizationText.Get("撤销"));
        ToolTip.SetTip(button, LocalizationText.Get("撤销"));
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                var result = await _history!.UndoAsync(id);
                if ((!result.Success || result.Warnings.Count > 0) && TopLevel.GetTopLevel(button) is Window window)
                    AppToast.ShowError(window, LocalizationText.Get(result.Message) + string.Join("\n", result.Warnings));
            }
            catch (Exception ex)
            { if (TopLevel.GetTopLevel(button) is Window window) AppToast.ShowError(window, ex.Message); }
            finally { button.IsEnabled = true; Refresh(); }
        };
        return button;
    }

    private static BackgroundTaskKind DisplayKind(BackgroundTaskInfo task)
    {
        if (task.Kind != BackgroundTaskKind.Copy || task.Request is not { } request) return task.Kind;
        if (request.Destination is { } destination && VirtualPath.IsRemotePath(destination)) return BackgroundTaskKind.RemoteUpload;
        return request.Sources.Any(VirtualPath.IsRemotePath) ? BackgroundTaskKind.RemoteDownload : task.Kind;
    }

    private static string TaskIcon(BackgroundTaskKind kind) => kind switch
    {
        BackgroundTaskKind.Copy => FileTaskIcons.Copy, BackgroundTaskKind.Move => FileTaskIcons.Move,
        BackgroundTaskKind.Delete => Icons.Delete, BackgroundTaskKind.Compress => FileTaskIcons.Archive,
        BackgroundTaskKind.Extract => Icons.Extract, BackgroundTaskKind.RemoteDownload => FileTaskIcons.Download,
        BackgroundTaskKind.RemoteUpload => FileTaskIcons.Upload, BackgroundTaskKind.Rename or BackgroundTaskKind.BatchRename => Icons.Rename,
        BackgroundTaskKind.Create => Icons.NewFile, _ => Icons.TaskListSquareLtr
    };

    private static string Title(BackgroundTaskInfo task)
    {
        if (task.Request is not { } request) return LocalizationText.Get(task.Label);
        var name = request.DisplayName ?? (request.Sources.Count == 1 ? Path.GetFileName(Path.TrimEndingDirectorySeparator(request.Sources[0]))
            : request.Sources.Count > 1 ? LocalizationText.Get("{0} 项", request.Sources.Count)
            : task.Items.LastOrDefault(i => i.Destination != null)?.Destination is { } destination ? Path.GetFileName(destination) : "");
        if (string.IsNullOrEmpty(name)) return LocalizationText.Get(task.Label);
        var operation = DisplayKind(task) switch {
            BackgroundTaskKind.Copy => "复制", BackgroundTaskKind.Move => "移动", BackgroundTaskKind.Delete => "删除",
            BackgroundTaskKind.Compress => "压缩", BackgroundTaskKind.Extract => "解压", BackgroundTaskKind.RemoteDownload => "tasks.action.download",
            BackgroundTaskKind.RemoteUpload => "上传", BackgroundTaskKind.Rename or BackgroundTaskKind.BatchRename => "重命名",
            BackgroundTaskKind.Create => "新建", _ => "" };
        return operation.Length == 0 ? LocalizationText.Get(task.Label) : LocalizationText.Get(operation) + " " + name;
    }

    private static double DisplayProgress(BackgroundTaskInfo task)
        => task.TotalBytes > 0 && task.Request?.Sources.Count == 1
            ? Math.Clamp(task.BytesTransferred * 100d / task.TotalBytes, 0, 100) : task.Progress;

    private static bool ShowProgress(BackgroundTaskInfo task) => task.State is BackgroundTaskState.Running
        or BackgroundTaskState.Pausing or BackgroundTaskState.Paused
        || task.State is BackgroundTaskState.Failed or BackgroundTaskState.NeedsAttention && task.Progress > 0;

    private static string Metrics(BackgroundTaskInfo task)
    {
        var amount = task.TotalBytes > 0 ? $"{FormatBytes(task.BytesTransferred)} / {FormatBytes(task.TotalBytes)}" : "";
        if (task.State == BackgroundTaskState.Running)
            return amount + (task.BytesPerSecond > 0 ? (amount.Length > 0 ? " · " : "") + FormatBytes(task.BytesPerSecond) + "/s" : "");
        if (task.State is BackgroundTaskState.Paused or BackgroundTaskState.Pausing)
            return StateText(task) + (amount.Length > 0 ? " · " + amount : "");
        if (task.State is BackgroundTaskState.Failed or BackgroundTaskState.NeedsAttention) return StateText(task);
        return "";
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var index = 0;
        while (bytes >= 1024 && index < units.Length - 1) { bytes /= 1024; index++; }
        return $"{bytes:0.#} {units[index]}";
    }

    private static string StateText(BackgroundTaskInfo task)
    {
        if (DisplayKind(task) is BackgroundTaskKind.RemoteDownload or BackgroundTaskKind.RemoteUpload
            && task.State == BackgroundTaskState.NeedsAttention
            && new[] { "connection", "disconnect", "socket", "连接", "断开" }.Any(word =>
                (task.ErrorDetail ?? task.ErrorMessage ?? "").Contains(word, StringComparison.OrdinalIgnoreCase)))
            return LocalizationText.Get("连接已断开");
        return LocalizationText.Get(task.State switch {
            BackgroundTaskState.Waiting => "等待开始", BackgroundTaskState.Pausing => "正在暂停",
            BackgroundTaskState.Paused => "已暂停", BackgroundTaskState.Cancelling => "正在取消",
            BackgroundTaskState.Cancelled => "已取消", BackgroundTaskState.NeedsAttention => "需要处理",
            BackgroundTaskState.Failed => "失败", _ => "已完成" });
    }

    private static Button IconButton(string label, string icon)
    {
        var glyph = FileTaskIcons.Create(icon, 22);
        var button = new Button { Content = glyph, Padding = default, Classes = { "icon-action" } };
        ((Avalonia.Controls.Shapes.Path)glyph.Child!).Bind(Shape.StrokeProperty, button.GetObservable(TemplatedControl.ForegroundProperty));
        AutomationProperties.SetName(button, LocalizationText.Get(label));
        ToolTip.SetTip(button, LocalizationText.Get(label));
        return button;
    }
    private static Button ActionButton(string label, string icon, Action action)
    {
        var button = IconButton(label, icon);
        button.Click += (_, _) => action();
        return button;
    }
    private static MenuItem MenuAction(string label, Action action)
    {
        var item = new MenuItem { Header = LocalizationText.Get(label) };
        item.Click += (_, _) => action();
        return item;
    }
}
