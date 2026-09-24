using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.PluginSdk;
using MacExplorer.ViewModels;
using MacExplorer.Views.Dialogs;

namespace MacExplorer.Services.Plugins;

public sealed record PluginCommandExecutionResult(bool Success, string Message,
    IReadOnlyList<string> Outputs, IReadOnlyList<string> Warnings);

public sealed record PluginExecutionHost(
    Window? Owner, FileListViewModel View, Func<bool> IsDisposed,
    Func<string> CurrentPath, Func<Task> Refresh,
    Action<IReadOnlyList<string>> SelectOutputs, Action<string> SetStatus,
    CancellationToken Lifetime);

/// <summary>The one authorization, configuration, execution and output-commit path for plugin commands.</summary>
public sealed class PluginCommandExecutor(
    PluginManager manager, IBackgroundTaskManager? tasks,
    IDirectoryChangeNotifier? changes)
{
    private static TimeSpan ExecuteTimeout(int count) => TimeSpan.FromSeconds(120 + 30 * (count - 1));

    public async Task<PluginCommandExecutionResult> ExecuteAsync(
        string pluginId, PluginCommand command, PluginFile[] files, PluginExecutionHost host)
    {
        BackgroundTaskInfo? task = null;
        CancellationTokenRegistration taskCancel = default;
        var committedOutputs = new List<string>();
        var outputsNotified = false;
        using var run = CancellationTokenSource.CreateLinkedTokenSource(host.Lifetime);
        try
        {
            await using var session = await manager.StartAsync(pluginId, command.Id, files, run.Token);
            var request = new PluginInvocation(Guid.NewGuid().ToString("N"), command.Id, files, session.WorkDirectory);
            if (host.Owner == null || !await PluginAuthorization.EnsureAsync(
                    manager, session.Manifest, session, request, host.Owner, run.Token))
                return new(false, "插件授权未完成。", [], []);

            var preparation = await session.CallAsync<PluginPreparation>("prepare", request,
                TimeSpan.FromSeconds(30), run.Token);
            if (preparation.Configuration is { } configuration)
            {
                if (configuration.Kind != "image-size" || configuration.Width <= 0 || configuration.Height <= 0
                    || !Enum.TryParse<FileConversionFormat>(configuration.Format, out var format)
                    || format is not (FileConversionFormat.Png or FileConversionFormat.Jpg))
                    throw new InvalidOperationException("此插件请求的配置窗口与当前应用不兼容。");
                var dialog = new ImageConversionDialog(new(configuration.Width, configuration.Height), format);
                using var modalBlock = host.Owner is Views.MainWindow window ? window.BlockModalParentInteraction() : null;
                using var cancelDialog = run.Token.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(null)));
                var size = await dialog.ShowDialog<ConversionImageSize?>(host.Owner);
                if (size == null) return new(false, "用户取消了插件配置。", [], []);
                request = request with { Parameters = new()
                {
                    ["width"] = JsonSerializer.SerializeToElement(size.Width),
                    ["height"] = JsonSerializer.SerializeToElement(size.Height)
                } };
            }

            var title = files.Length == 1
                ? command.Title + "：" + Path.GetFileName(files[0].Path)
                : command.Title + "：" + files.Length + " 个文件";
            var pump = new PluginProgressPump(
                callback => Dispatcher.UIThread.Post(callback, DispatcherPriority.Background), progress =>
            {
                if (host.IsDisposed()) return;
                if (progress.ShowInTaskPanel && task == null && tasks != null)
                {
                    task = tasks.AddTask(string.IsNullOrWhiteSpace(progress.TaskTitle) ? title : progress.TaskTitle);
                    taskCancel = task.Cts.Token.Register(run.Cancel);
                }
                if (task is { State: BackgroundTaskState.Running })
                    tasks!.UpdateProgress(task.Id, progress.Percent is { } percent && double.IsFinite(percent)
                        ? Math.Clamp(percent, 0, 100) : task.Progress, progress.Message);
                host.SetStatus(progress.Message);
            });
            session.Progress += pump.Report;
            PluginResult result;
            try
            {
                host.SetStatus(files.Length == 1 ? "正在处理 " + Path.GetFileName(files[0].Path) + "…"
                    : "正在处理 " + files.Length + " 个文件…");
                result = await session.CallAsync<PluginResult>("execute", request,
                    ExecuteTimeout(files.Length), run.Token);
                pump.Complete();
            }
            finally
            {
                session.Progress -= pump.Report;
                pump.Dispose();
            }

            var outputs = await PluginOutputCommitter.CommitAsync(result.Outputs, files,
                session.WorkDirectory, run.Token, committedOutputs.Add);
            if (task != null) { task.CanCancel = false; tasks!.CompleteTask(task.Id); }
            var directories = files.Select(file => Path.GetDirectoryName(file.Path)!)
                .Distinct(StringComparer.Ordinal).ToArray();
            var isCurrentDirectory = directories.Contains(host.CurrentPath(), StringComparer.Ordinal);
            changes?.NotifyChanged(directories, isCurrentDirectory ? host.View : null);
            outputsNotified = !isCurrentDirectory;
            if (isCurrentDirectory && !host.IsDisposed())
            {
                var refreshPath = host.CurrentPath();
                await host.Refresh();
                outputsNotified = true;
                if (!host.IsDisposed() && host.CurrentPath() == refreshPath)
                    host.SelectOutputs(outputs);
            }
            var message = (files.Length == 1
                    ? "已生成 " + string.Join("、", outputs.Select(Path.GetFileName))
                    : "已生成 " + outputs.Length + " 个文件") +
                (result.Warnings.Length == 0 ? "" : "。" + string.Join("；", result.Warnings));
            if (!host.IsDisposed()) host.SetStatus(message);
            return new(true, message, outputs, result.Warnings);
        }
        catch (OperationCanceledException)
        {
            if (task != null) tasks!.CancelTask(task.Id);
            var message = committedOutputs.Count == 0 ? "已取消处理"
                : $"已取消处理，保留已生成的 {committedOutputs.Count} 个文件。";
            if (!host.IsDisposed()) host.SetStatus(message);
            return new(false, message, committedOutputs, []);
        }
        catch (Exception ex)
        {
            if (task is { State: BackgroundTaskState.Running }) tasks!.FailTask(task.Id, ex.Message);
            try { await manager.RecordErrorAsync(pluginId, ex.Message); }
            catch (Exception metadataError) { System.Diagnostics.Debug.WriteLine(metadataError); }
            var message = committedOutputs.Count == 0 ? "插件处理失败：" + ex.Message
                : $"已生成 {committedOutputs.Count} 个文件，其余处理失败：{ex.Message}";
            if (!host.IsDisposed()) host.SetStatus(message);
            return new(false, message, committedOutputs, []);
        }
        finally
        {
            taskCancel.Dispose();
            if (!outputsNotified && committedOutputs.Count > 0)
                changes?.NotifyChanged(committedOutputs.Select(Path.GetDirectoryName)
                    .OfType<string>().Distinct(StringComparer.Ordinal).ToArray(), null);
        }
    }
}
