using MacExplorer.Models;
using MacExplorer.Services;
using UIKit;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacContextMenuService : IContextMenuService
{
    private readonly IApplicationLauncherService _launcher;
    private readonly IFileService _fileService;
    private HashSet<string>? _installedApps;
    private readonly Task<HashSet<string>> _installedAppsTask;

    private static readonly (string Label, string BundleId, string CliName, string IconSvg)[] VscodeBasedEditors =
    [
        ("Cursor", "com.todesktop.230313mzl4w4u92", "cursor", Icons.CodeEditor),
        ("Kiro", "dev.kiro.desktop", "kiro", Icons.Kiro),
        ("Qoder", "com.qoder.ide", "qoder", Icons.Qoder),
    ];

    public MacContextMenuService(IApplicationLauncherService launcher, IFileService fileService)
    {
        _launcher = launcher;
        _fileService = fileService;
        _installedAppsTask = Task.Run(ScanInstalledApps);
    }

    public async Task<IReadOnlyList<ContextMenuAction>> GetFileContextMenuActionsAsync(FileSystemEntry entry)
    {
        var actions = new List<ContextMenuAction>
        {
            new() { Label = "打开", IconSvg = Icons.Open, ShortcutText = "⌘O", Execute = () => _launcher.OpenFileAsync(entry.FullPath) },
        };

        // Add "Show Package Contents" for .app bundles
        if (entry.IconKey == "app-bundle")
        {
            actions.Add(new() { Label = "显示包内容", IconSvg = Icons.Folder, Execute = null });
        }

        var apps = await GetApplicationsForFileAsync(entry.FullPath);
        if (apps.Count > 0)
        {
            actions.Add(new ContextMenuAction
            {
                Label = "打开方式",
                IconSvg = Icons.Open,
                SubItems = apps.Select(app => new ContextMenuAction
                {
                    Label = app.Name,
                    IconSvg = Icons.Finder,
                    Execute = () => _launcher.OpenFileWithAppAsync(entry.FullPath, app.BundleIdentifier)
                }).ToList()
            });
        }

        // Quick actions: Cut, Copy, Rename, Delete (shown as icon bar at top of menu)
        actions.Add(new() { Label = "剪切", IconSvg = Icons.Cut, ShortcutText = "⌘X", IsQuickAction = true });
        actions.Add(new() { Label = "拷贝", IconSvg = Icons.Copy, ShortcutText = "⌘C", IsQuickAction = true });
        actions.Add(new() { Label = "重命名", IconSvg = Icons.Rename, ShortcutText = "↵", IsQuickAction = true });
        actions.Add(new() { Label = "删除", IconSvg = Icons.Delete, ShortcutText = "⌘⌫", IsQuickAction = true });

        actions.Add(ContextMenuAction.Separator);

        // Archive: extract for archive files, compress for others
        if (entry.IconKey == "file-archive")
        {
            actions.Add(new() { Label = "解压到此处", IconSvg = Icons.Extract });
        }
        else
        {
            actions.Add(new() { Label = "压缩", IconSvg = Icons.Compress });
        }

        actions.Add(ContextMenuAction.Separator);

        actions.Add(new() { Label = "复制路径", IconSvg = Icons.CopyPath, ShortcutText = "⌥⌘C", Execute = () => CopyToClipboard(entry.FullPath) });

        actions.Add(ContextMenuAction.Separator);

        actions.Add(new() { Label = "在 Finder 中显示", IconSvg = Icons.Finder, Execute = () => _launcher.RevealInFinderAsync(entry.FullPath) });

        // Terminal: for directories use the path directly, for files use parent directory
        var terminalPath = entry.IsDirectory ? entry.FullPath : Path.GetDirectoryName(entry.FullPath) ?? entry.FullPath;
        actions.Add(new() { Label = "在终端中打开", IconSvg = Icons.Terminal, Execute = () => _launcher.OpenInTerminalAsync(terminalPath) });
        if (IsAppInstalled("com.microsoft.VSCode"))
            actions.Add(new() { Label = "在 VS Code 中打开", IconSvg = Icons.VSCode, Execute = () => _launcher.OpenInVsCodeAsync(entry.FullPath) });
        foreach (var editor in VscodeBasedEditors)
        {
            if (IsAppInstalled(editor.BundleId))
            {
                var e = editor;
                actions.Add(new() { Label = $"在 {e.Label} 中打开", IconSvg = e.IconSvg, Execute = () => _launcher.OpenInEditorAsync(entry.FullPath, e.CliName, e.BundleId) });
            }
        }

        // Pin到收藏（仅文件夹）
        if (entry.IsDirectory)
        {
            actions.Add(new() { Label = "Pin到收藏", IconSvg = Icons.Pin });
        }

        actions.Add(ContextMenuAction.Separator);
        actions.Add(new() { Label = "查看文件信息", IconSvg = Icons.Info, ShortcutText = "⌘I" });

        return actions;
    }

    public async Task<IReadOnlyList<ContextMenuAction>> GetBackgroundContextMenuActionsAsync(string currentDirectory)
    {
        var actions = new List<ContextMenuAction>();

        actions.Add(new() { Label = "粘贴", IconSvg = Icons.Paste, ShortcutText = "⌘V", IsQuickAction = true });

        actions.Add(new() { Label = "新建文件夹", IconSvg = Icons.NewFolder, ShortcutText = "⇧⌘N" });
        actions.Add(new() { Label = "新建文件", IconSvg = Icons.NewFile });

        actions.Add(ContextMenuAction.Separator);

        actions.Add(new() { Label = "在终端中打开", IconSvg = Icons.Terminal, Execute = () => _launcher.OpenInTerminalAsync(currentDirectory) });
        if (IsAppInstalled("com.microsoft.VSCode"))
            actions.Add(new() { Label = "在 VS Code 中打开", IconSvg = Icons.VSCode, Execute = () => _launcher.OpenInVsCodeAsync(currentDirectory) });
        foreach (var editor in VscodeBasedEditors)
        {
            if (IsAppInstalled(editor.BundleId))
            {
                var e = editor;
                actions.Add(new() { Label = $"在 {e.Label} 中打开", IconSvg = e.IconSvg, Execute = () => _launcher.OpenInEditorAsync(currentDirectory, e.CliName, e.BundleId) });
            }
        }

        actions.Add(ContextMenuAction.Separator);

        actions.Add(new() { Label = "复制路径", IconSvg = Icons.CopyPath, Execute = () => CopyToClipboard(currentDirectory) });

        return await Task.FromResult(actions.AsReadOnly());
    }

    public Task<IReadOnlyList<ContextMenuAction>> GetTrashFileContextMenuActionsAsync(FileSystemEntry entry)
    {
        var actions = new List<ContextMenuAction>
        {
            new() { Label = "永久删除", IconSvg = Icons.Delete, Execute = null },
        };
        return Task.FromResult<IReadOnlyList<ContextMenuAction>>(actions.AsReadOnly());
    }

    public Task<IReadOnlyList<ContextMenuAction>> GetTrashBackgroundContextMenuActionsAsync()
    {
        var actions = new List<ContextMenuAction>
        {
            new() { Label = "清倒废纸篓", IconSvg = Icons.Delete, Execute = null },
        };
        return Task.FromResult<IReadOnlyList<ContextMenuAction>>(actions.AsReadOnly());
    }

    public async Task<IReadOnlyList<RegisteredApp>> GetApplicationsForFileAsync(string filePath)
    {
        var apps = new List<RegisteredApp>();
        try
        {
            var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) return apps;
            var knownApps = GetKnownAppsForExtension(ext);
            foreach (var app in knownApps)
                if (IsAppInstalled(app.BundleIdentifier))
                    apps.Add(app);
        }
        catch { }
        return await Task.FromResult(apps.AsReadOnly());
    }

    public bool IsAppInstalled(string bundleIdentifier)
    {
        if (_installedApps != null)
            return _installedApps.Contains(bundleIdentifier);

        if (_installedAppsTask.IsCompletedSuccessfully)
        {
            _installedApps = _installedAppsTask.Result;
            return _installedApps.Contains(bundleIdentifier);
        }

        return false;
    }

    private static HashSet<string> ScanInstalledApps()
    {
        var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var appPath in new[] { "/Applications", "/System/Applications", "/Applications/Utilities" })
        {
            try
            {
                if (!Directory.Exists(appPath)) continue;
                foreach (var dir in Directory.EnumerateDirectories(appPath, "*.app"))
                {
                    try
                    {
                        var plistPath = Path.Combine(dir, "Contents", "Info.plist");
                        if (!File.Exists(plistPath)) continue;
                        var bundleId = ExtractBundleIdentifier(plistPath);
                        if (!string.IsNullOrEmpty(bundleId)) apps.Add(bundleId);
                    }
                    catch { }
                }
            }
            catch { }
        }
        return apps;
    }

    private static string? ExtractBundleIdentifier(string plistPath)
    {
        try
        {
            var dict = Foundation.NSMutableDictionary.FromFile(plistPath);
            if (dict == null) return null;
            var value = dict["CFBundleIdentifier"];
            return value?.ToString();
        }
        catch { return null; }
    }

    private static Task CopyToClipboard(string text)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UIPasteboard.General.String = text;
        });
        return Task.CompletedTask;
    }

    private static List<RegisteredApp> GetKnownAppsForExtension(string ext) => ext switch
    {
        ".txt" or ".md" or ".log" or ".csv" => [
            new() { Name = "TextEdit", BundleIdentifier = "com.apple.TextEdit", IsDefault = true },
            new() { Name = "VS Code", BundleIdentifier = "com.microsoft.VSCode" },
        ],
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".tiff" or ".webp" or ".svg" => [
            new() { Name = "Preview", BundleIdentifier = "com.apple.Preview", IsDefault = true },
            new() { Name = "VS Code", BundleIdentifier = "com.microsoft.VSCode" },
        ],
        ".pdf" => [
            new() { Name = "Preview", BundleIdentifier = "com.apple.Preview", IsDefault = true },
        ],
        ".mp4" or ".mov" or ".avi" or ".mkv" => [
            new() { Name = "QuickTime Player", BundleIdentifier = "com.apple.QuickTimePlayerX", IsDefault = true },
            new() { Name = "VLC", BundleIdentifier = "org.videolan.vlc" },
        ],
        ".html" or ".css" or ".js" or ".ts" or ".py" or ".java" or ".cs" or ".go" or ".rs" or ".swift" => [
            new() { Name = "VS Code", BundleIdentifier = "com.microsoft.VSCode", IsDefault = true },
            new() { Name = "Cursor", BundleIdentifier = "com.todesktop.230313mzl4w4u92" },
            new() { Name = "Kiro", BundleIdentifier = "dev.kiro.desktop" },
            new() { Name = "Qoder", BundleIdentifier = "com.qoder.ide" },
            new() { Name = "Xcode", BundleIdentifier = "com.apple.dt.Xcode" },
        ],
        _ => []
    };
}
