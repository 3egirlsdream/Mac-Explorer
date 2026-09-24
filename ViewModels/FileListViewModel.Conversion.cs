using Avalonia.Threading;
using MacExplorer.Copilot;
using MacExplorer.Models;
using MacExplorer.PluginSdk;
using MacExplorer.Services;
using MacExplorer.Services.Plugins;
using AppIcons = MacExplorer.Assets.Icons;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    private readonly PluginManager? _pluginManager;
    private readonly IBackgroundTaskManager? _conversionTaskManager;
    private readonly IAppCapabilityRegistry? _appCapabilities;
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
                Execute = async () =>
                {
                    if (_appCapabilities == null)
                        await ExecutePluginCommandAsync(plugin.Manifest.Id, command, files);
                    else
                        await _appCapabilities.ExecuteUiAsync(
                            AppCapabilityRegistry.PluginCommandId(plugin.Manifest.Id, command.Id),
                            System.Text.Json.JsonSerializer.Serialize(new { paths = files.Select(file => file.Path).ToArray() }), this);
                }
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

    // Context menus and Copilot both use the application plugin executor.
    public Task<PluginCommandExecutionResult> ExecutePluginCommandAsync(
        string pluginId, PluginCommand command, PluginFile[] files)
    {
        if (_pluginManager == null)
            return Task.FromResult(new PluginCommandExecutionResult(false, "插件服务不可用。", [], []));
        IsContextMenuVisible = false;
        var executor = new PluginCommandExecutor(_pluginManager, _conversionTaskManager, _directoryChangeNotifier);
        var host = new PluginExecutionHost(_topLevelWindow, this, () => _disposed,
            () => CurrentPath, RefreshAsync, outputs =>
            {
                var outputSet = new HashSet<string>(outputs, StringComparer.Ordinal);
                var output = Entries.FirstOrDefault(item => outputSet.Contains(item.FullPath));
                if (output != null) SelectEntry(output);
            }, message => StatusText = message, _pluginLifetime.Token);
        return executor.ExecuteAsync(pluginId, command, files, host);
    }
}
