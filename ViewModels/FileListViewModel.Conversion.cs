using System.Text.Json;
using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.PluginSdk;
using MacExplorer.Services;
using MacExplorer.Services.Plugins;
using MacExplorer.Views.Dialogs;
using AppIcons = MacExplorer.Assets.Icons;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    private readonly PluginManager? _pluginManager;
    private readonly IBackgroundTaskManager? _conversionTaskManager;
    private readonly CancellationTokenSource _pluginLifetime = new();

    private IReadOnlyList<ContextMenuAction> BuildPluginContextMenus(FileSystemEntry entry)
    {
        var entries = GetContextEntries(entry);
        if (_pluginManager == null || IsTrashActive || IsArchiveView || entry.IsDirectory ||
            entries.Any(e => !IsUsableLocalEntry(e) || e.IsDirectory || e.FullPath.StartsWith(TrashPath.TrimEnd('/') + "/", StringComparison.Ordinal))) return [];
        var files = entries.Select(e => new PluginFile(e.FullPath)).ToArray();
        return _pluginManager.Plugins.Where(p => p.Enabled && !p.Removed).Select(plugin =>
        {
            var commands = plugin.Manifest.Commands.Where(command => command.Match.Matches(files)).Select(command => new ContextMenuAction
            {
                Label = command.Title, IconSvg = PluginIcon(command.Icon), IsEnabled = !plugin.Running,
                Execute = () => ExecutePluginAsync(plugin.Manifest.Id, command, files)
            }).ToArray();
            return new ContextMenuAction { Label = plugin.Manifest.Name, IconSvg = PluginIcon(plugin.Manifest.Icon), SubItems = commands };
        }).Where(action => action.SubItems!.Count > 0).ToArray();
    }

    private static string PluginIcon(string name) => name switch
    {
        "image" => AppIcons.Image,
        "document" => Icons.NewFile,
        "apps" => AppIcons.Apps,
        _ => AppIcons.Convert
    };

    private void OnPluginsChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed) CloseContextMenu();
    });

    private async Task ExecutePluginAsync(string pluginId, PluginCommand command, PluginFile[] files)
    {
        if (_pluginManager == null) return;
        IsContextMenuVisible = false;
        BackgroundTaskInfo? task = null;
        try
        {
            await using var session = await _pluginManager.StartAsync(pluginId, command.Id, files, _pluginLifetime.Token);
            var request = new PluginInvocation(Guid.NewGuid().ToString("N"), command.Id, files, session.WorkDirectory);
            var manifest = session.Manifest;
            if (_topLevelWindow == null || !await PluginAuthorization.EnsureAsync(_pluginManager, manifest, session, request, _topLevelWindow, _pluginLifetime.Token)) return;
            var preparation = await session.CallAsync<PluginPreparation>("prepare", request, TimeSpan.FromSeconds(30), _pluginLifetime.Token);
            if (preparation.Configuration is { } configuration)
            {
                if (configuration.Kind != "image-size" || configuration.Width <= 0 || configuration.Height <= 0 || !Enum.TryParse<FileConversionFormat>(configuration.Format, out var format) ||
                    format is not (FileConversionFormat.Png or FileConversionFormat.Jpg))
                    throw new InvalidOperationException("此插件请求的配置窗口与当前应用不兼容。");
                if (_topLevelWindow == null) return;
                var dialog = new ImageConversionDialog(new(configuration.Width, configuration.Height), format);
                using var modalBlock = _topLevelWindow is Views.MainWindow mainWindow ? mainWindow.BlockModalParentInteraction() : null;
                using var cancelDialog = _pluginLifetime.Token.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(null)));
                var size = await dialog.ShowDialog<ConversionImageSize?>(_topLevelWindow);
                if (size == null) return;
                request = request with { Parameters = new()
                {
                    ["width"] = JsonSerializer.SerializeToElement(size.Width),
                    ["height"] = JsonSerializer.SerializeToElement(size.Height)
                } };
            }
            task = _conversionTaskManager?.AddTask(command.Title + "：" + Path.GetFileName(files[0].Path));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_pluginLifetime.Token, task?.Cts.Token ?? CancellationToken.None);
            StatusText = "正在处理 " + Path.GetFileName(files[0].Path) + "…";
            session.Progress += progress => Dispatcher.UIThread.Post(() =>
            {
                if (task is { State: BackgroundTaskState.Running } && !_disposed)
                {
                    if (progress.Percent is { } percent && double.IsFinite(percent))
                        _conversionTaskManager?.UpdateProgress(task.Id, Math.Clamp(percent, 0, 100), Path.GetFileName(files[0].Path), progress.Message);
                    StatusText = progress.Message;
                }
            });
            var result = await session.CallAsync<PluginResult>("execute", request, TimeSpan.FromMinutes(2), cancellation.Token);
            var outputs = await PluginOutputCommitter.CommitAsync(result.Outputs, files[0].Path, session.WorkDirectory, cancellation.Token);
            if (task != null) { task.CanCancel = false; _conversionTaskManager!.CompleteTask(task.Id); }
            var directory = Path.GetDirectoryName(files[0].Path)!;
            var isCurrentDirectory = string.Equals(CurrentPath, directory, StringComparison.Ordinal);
            _directoryChangeNotifier?.NotifyChanged([directory], isCurrentDirectory ? this : null);
            if (isCurrentDirectory && !_disposed)
            {
                await RefreshAsync();
                if (string.Equals(CurrentPath, directory, StringComparison.Ordinal))
                {
                    var output = Entries.FirstOrDefault(item => item.FullPath == outputs[0]);
                    if (output != null) SelectEntry(output);
                }
            }
            if (!_disposed) StatusText = "已生成 " + string.Join("、", outputs.Select(Path.GetFileName)) +
                (result.Warnings.Length == 0 ? "" : "。" + string.Join("；", result.Warnings));
        }
        catch (OperationCanceledException)
        {
            if (task != null) _conversionTaskManager!.CancelTask(task.Id);
            if (!_disposed) StatusText = "已取消处理";
        }
        catch (Exception ex)
        {
            if (task != null && task.State == BackgroundTaskState.Running) _conversionTaskManager!.FailTask(task.Id, ex.Message);
            await _pluginManager.RecordErrorAsync(pluginId, ex.Message);
            if (!_disposed) StatusText = "插件处理失败：" + ex.Message;
        }
    }
}
