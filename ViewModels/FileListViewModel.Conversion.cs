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

    private static TimeSpan ExecuteTimeout(int fileCount) => TimeSpan.FromSeconds(120 + 30 * (fileCount - 1));

    private async Task ExecutePluginAsync(string pluginId, PluginCommand command, PluginFile[] files)
    {
        if (_pluginManager == null) return;
        IsContextMenuVisible = false;
        BackgroundTaskInfo? task = null;
        CancellationTokenRegistration taskCancel = default;
        var committedOutputs = new List<string>();
        var outputsNotified = false;
        using var run = CancellationTokenSource.CreateLinkedTokenSource(_pluginLifetime.Token);
        try
        {
            await using var session = await _pluginManager.StartAsync(pluginId, command.Id, files, run.Token);
            var request = new PluginInvocation(Guid.NewGuid().ToString("N"), command.Id, files, session.WorkDirectory);
            var manifest = session.Manifest;
            if (_topLevelWindow == null || !await PluginAuthorization.EnsureAsync(_pluginManager, manifest, session, request, _topLevelWindow, run.Token)) return;
            var preparation = await session.CallAsync<PluginPreparation>("prepare", request, TimeSpan.FromSeconds(30), run.Token);
            if (preparation.Configuration is { } configuration)
            {
                if (configuration.Kind != "image-size" || configuration.Width <= 0 || configuration.Height <= 0 || !Enum.TryParse<FileConversionFormat>(configuration.Format, out var format) ||
                    format is not (FileConversionFormat.Png or FileConversionFormat.Jpg))
                    throw new InvalidOperationException("此插件请求的配置窗口与当前应用不兼容。");
                if (_topLevelWindow == null) return;
                var dialog = new ImageConversionDialog(new(configuration.Width, configuration.Height), format);
                using var modalBlock = _topLevelWindow is Views.MainWindow mainWindow ? mainWindow.BlockModalParentInteraction() : null;
                using var cancelDialog = run.Token.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(null)));
                var size = await dialog.ShowDialog<ConversionImageSize?>(_topLevelWindow);
                if (size == null) return;
                request = request with { Parameters = new()
                {
                    ["width"] = JsonSerializer.SerializeToElement(size.Width),
                    ["height"] = JsonSerializer.SerializeToElement(size.Height)
                } };
            }
            var title = files.Length == 1
                ? command.Title + "：" + Path.GetFileName(files[0].Path)
                : command.Title + "：" + files.Length + " 个文件";
            var progressPump = new PluginProgressPump(
                callback => Dispatcher.UIThread.Post(callback, DispatcherPriority.Background), progress =>
            {
                if (_disposed) return;
                if (progress.ShowInTaskPanel && task == null && _conversionTaskManager != null)
                {
                    task = _conversionTaskManager.AddTask(string.IsNullOrWhiteSpace(progress.TaskTitle) ? title : progress.TaskTitle);
                    taskCancel = task.Cts.Token.Register(run.Cancel);
                }
                if (task is { State: BackgroundTaskState.Running })
                    _conversionTaskManager!.UpdateProgress(task.Id, progress.Percent is { } percent && double.IsFinite(percent)
                        ? Math.Clamp(percent, 0, 100) : task.Progress, progress.Message);
                StatusText = progress.Message;
            });
            session.Progress += progressPump.Report;
            PluginResult result;
            try
            {
                StatusText = files.Length == 1 ? "正在处理 " + Path.GetFileName(files[0].Path) + "…" : "正在处理 " + files.Length + " 个文件…";
                result = await session.CallAsync<PluginResult>("execute", request, ExecuteTimeout(files.Length), run.Token);
                progressPump.Complete();
            }
            finally
            {
                session.Progress -= progressPump.Report;
                progressPump.Dispose();
            }
            var outputs = await PluginOutputCommitter.CommitAsync(result.Outputs, files, session.WorkDirectory, run.Token, committedOutputs.Add);
            if (task != null) { task.CanCancel = false; _conversionTaskManager!.CompleteTask(task.Id); }
            var directories = files.Select(file => Path.GetDirectoryName(file.Path)!).Distinct(StringComparer.Ordinal).ToArray();
            var isCurrentDirectory = directories.Contains(CurrentPath, StringComparer.Ordinal);
            _directoryChangeNotifier?.NotifyChanged(directories, isCurrentDirectory ? this : null);
            outputsNotified = !isCurrentDirectory;
            if (isCurrentDirectory && !_disposed)
            {
                var refreshPath = CurrentPath;
                await RefreshAsync();
                outputsNotified = true;
                if (!_disposed && CurrentPath == refreshPath)
                {
                    var outputSet = new HashSet<string>(outputs, StringComparer.Ordinal);
                    var output = Entries.FirstOrDefault(item => outputSet.Contains(item.FullPath));
                    if (output != null) SelectEntry(output);
                }
            }
            if (!_disposed) StatusText = (files.Length == 1 ? "已生成 " + string.Join("、", outputs.Select(Path.GetFileName)) : "已生成 " + outputs.Length + " 个文件") +
                (result.Warnings.Length == 0 ? "" : "。" + string.Join("；", result.Warnings));
        }
        catch (OperationCanceledException)
        {
            if (task != null) _conversionTaskManager!.CancelTask(task.Id);
            if (!_disposed) StatusText = committedOutputs.Count == 0 ? "已取消处理"
                : $"已取消处理，保留已生成的 {committedOutputs.Count} 个文件。";
        }
        catch (Exception ex)
        {
            if (task is { State: BackgroundTaskState.Running }) _conversionTaskManager!.FailTask(task.Id, ex.Message);
            try { await _pluginManager.RecordErrorAsync(pluginId, ex.Message); }
            catch (Exception metadataError) { System.Diagnostics.Debug.WriteLine(metadataError); }
            if (!_disposed) StatusText = committedOutputs.Count == 0 ? "插件处理失败：" + ex.Message
                : $"已生成 {committedOutputs.Count} 个文件，其余处理失败：{ex.Message}";
        }
        finally
        {
            taskCancel.Dispose();
            // Failed/cancelled batches may still have durable complete outputs.
            // Include this view too when the success path did not finish refreshing.
            if (!outputsNotified && committedOutputs.Count > 0)
                _directoryChangeNotifier?.NotifyChanged(committedOutputs.Select(Path.GetDirectoryName)
                    .OfType<string>().Distinct(StringComparer.Ordinal).ToArray(), null);
        }
    }
}
