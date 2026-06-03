using System.Collections.Concurrent;
using MacExplorer.Models;
using MacExplorer.Services;
using UIKit;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacContextMenuService : IContextMenuService
{
    private readonly IApplicationLauncherService _launcher;
    private readonly IFileService _fileService;
    private readonly IOpenWithAppService _openWithService;
    private HashSet<string>? _installedApps;
    private readonly Task<HashSet<string>> _installedAppsTask;

    // System app icon cache: bundleId → base64 PNG
    private readonly ConcurrentDictionary<string, string?> _iconCache = new();
    private readonly Task _iconCacheTask;

    public MacContextMenuService(IApplicationLauncherService launcher, IFileService fileService, IOpenWithAppService openWithService)
    {
        _launcher = launcher;
        _fileService = fileService;
        _openWithService = openWithService;
        _installedAppsTask = Task.Run(ScanInstalledApps);
        _iconCacheTask = Task.Run(WarmupIconCache);
    }

    public async Task<IReadOnlyList<ContextMenuAction>> GetFileContextMenuActionsAsync(FileSystemEntry entry)
    {
        var actions = new List<ContextMenuAction>
        {
            new() { Label = "打开", IconSvg = Icons.Open, ShortcutText = "⌘O", Execute = () => _launcher.OpenFileAsync(entry.FullPath) },
        };

        // "打开方式" submenu — right after "打开"
        await AddOpenWithSubmenuAsync(actions, entry.FullPath);

        // Add "Show Package Contents" for .app bundles
        if (entry.IconKey == "app-bundle")
        {
            actions.Add(new() { Label = "显示包内容", IconSvg = Icons.Folder, Execute = null });
        }

        // Quick actions: Cut, Copy, Rename, Delete
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

        // Top-level configured apps (e.g. "在 VS Code 中打开")
        await AddTopLevelOpenWithActionsAsync(actions, entry.FullPath);

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

        // Top-level configured apps
        await AddTopLevelOpenWithActionsAsync(actions, currentDirectory);

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

    /// <summary>
    /// Adds the "打开方式" submenu (non-top-level apps + system default apps).
    /// Placed right after "打开" in the menu.
    /// </summary>
    private async Task AddOpenWithSubmenuAsync(List<ContextMenuAction> actions, string path)
    {
        var allApps = await _openWithService.GetAllAsync();
        var submenuApps = allApps.Where(a => !a.IsTopLevel).ToList();

        var subItems = new List<ContextMenuAction>();

        // User-configured submenu apps
        foreach (var app in submenuApps)
        {
            if (!IsAppInstalled(app.BundleId)) continue;
            var a = app;
            subItems.Add(new()
            {
                Label = $"在 {a.Label} 中打开",
                IconBase64 = a.IconBase64,
                IconSvg = Icons.CodeEditor,
                Execute = () => _launcher.OpenInEditorAsync(path, "", a.BundleId),
            });
        }

        // System-registered default apps
        var systemApps = await GetApplicationsForFileAsync(path);
        if (systemApps.Count > 0)
        {
            if (subItems.Count > 0)
                subItems.Add(ContextMenuAction.Separator);

            foreach (var sysApp in systemApps)
            {
                var iconBase64 = GetAppBundleIconBase64(sysApp.BundleIdentifier);
                subItems.Add(new()
                {
                    Label = sysApp.Name,
                    IconBase64 = iconBase64,
                    IconSvg = Icons.Finder, // fallback
                    Execute = () => _launcher.OpenFileWithAppAsync(path, sysApp.BundleIdentifier),
                });
            }
        }

        if (subItems.Count > 0)
        {
            actions.Add(new ContextMenuAction
            {
                Label = "打开方式",
                IconSvg = Icons.Open,
                SubItems = subItems,
            });
        }
    }

    /// <summary>
    /// Adds top-level configured apps directly to the menu.
    /// Placed after "在终端中打开".
    /// </summary>
    private async Task AddTopLevelOpenWithActionsAsync(List<ContextMenuAction> actions, string path)
    {
        var allApps = await _openWithService.GetAllAsync();
        var topLevel = allApps.Where(a => a.IsTopLevel).ToList();

        foreach (var app in topLevel)
        {
            if (!IsAppInstalled(app.BundleId)) continue;
            var a = app;
            actions.Add(new()
            {
                Label = $"在 {a.Label} 中打开",
                IconBase64 = a.IconBase64,
                IconSvg = Icons.CodeEditor, // fallback
                Execute = () => _launcher.OpenInEditorAsync(path, "", a.BundleId),
            });
        }
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

    /// <summary>
    /// Gets the base64 icon for an installed app by its bundle identifier.
    /// Returns null if the app is not found or icon extraction fails.
    /// </summary>
    private string? GetAppBundleIconBase64(string bundleIdentifier) =>
        _iconCache.TryGetValue(bundleIdentifier, out var icon) ? icon : null;

    /// <summary>
    /// Batch-warm the icon cache at startup by scanning installed apps
    /// and extracting icons via a single JXA process.
    /// </summary>
    private void WarmupIconCache()
    {
        try
        {
            var searchPaths = new[] { "/Applications", "/System/Applications", "/Applications/Utilities" };
            var apps = new List<(string bundleId, string appPath)>();

            foreach (var searchPath in searchPaths)
            {
                if (!Directory.Exists(searchPath)) continue;
                foreach (var dir in Directory.EnumerateDirectories(searchPath, "*.app", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var plistPath = Path.Combine(dir, "Contents", "Info.plist");
                        if (!File.Exists(plistPath)) continue;
                        var dict = Foundation.NSMutableDictionary.FromFile(plistPath);
                        var bundleId = dict?["CFBundleIdentifier"]?.ToString();
                        if (!string.IsNullOrEmpty(bundleId))
                            apps.Add((bundleId, dir));
                    }
                    catch { }
                }
            }

            // Extract icons in batches of 20
            const int batchSize = 20;
            for (int i = 0; i < apps.Count; i += batchSize)
            {
                var batch = apps.Skip(i).Take(batchSize).ToList();
                ExtractIconsBatch(batch);
            }
        }
        catch { }
    }

    private void ExtractIconsBatch(List<(string bundleId, string appPath)> batch)
    {
        try
        {
            var iconDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MacExplorer", "icon-cache");
            Directory.CreateDirectory(iconDir);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("ObjC.import('AppKit');");
            sb.AppendLine("ObjC.import('Foundation');");
            sb.AppendLine("var ws = $.NSWorkspace.sharedWorkspace;");
            sb.AppendLine("function extractIcon(appPath, outPath) {");
            sb.AppendLine("  try {");
            sb.AppendLine("    var icon = ws.iconForFile(appPath);");
            sb.AppendLine("    var sz = $.NSMakeSize(128, 128);");
            sb.AppendLine("    var newImg = $.NSImage.alloc.initWithSize(sz);");
            sb.AppendLine("    newImg.lockFocus;");
            sb.AppendLine("    icon.drawInRectFromRectOperationFraction($.NSMakeRect(0,0,128,128), $.NSZeroRect, $.NSCompositingOperationSourceOver, 1.0);");
            sb.AppendLine("    newImg.unlockFocus;");
            sb.AppendLine("    var tiff = newImg.TIFFRepresentation;");
            sb.AppendLine("    var rep = $.NSBitmapImageRep.imageRepWithData(tiff);");
            sb.AppendLine("    var png = rep.representationUsingTypeProperties($.NSBitmapImageFileTypePNG, $({}));");
            sb.AppendLine("    png.writeToFileAtomically(outPath, true);");
            sb.AppendLine("  } catch(e) {}");
            sb.AppendLine("}");

            var outPaths = new List<(string bundleId, string outPath)>();
            foreach (var (bundleId, appPath) in batch)
            {
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(appPath))).Substring(0, 16).ToLowerInvariant();
                var outPath = Path.Combine(iconDir, $"{hash}.png");
                outPaths.Add((bundleId, outPath));

                var escapedApp = appPath.Replace("\\", "\\\\").Replace("'", "\\'");
                var escapedOut = outPath.Replace("\\", "\\\\").Replace("'", "\\'");
                sb.AppendLine($"extractIcon('{escapedApp}', '{escapedOut}');");
            }
            sb.AppendLine("'done'");

            var scriptPath = Path.Combine(Path.GetTempPath(), $"fkfinder_warmup_{Guid.NewGuid():N}.js");
            File.WriteAllText(scriptPath, sb.ToString());

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/usr/bin/osascript",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-l");
                psi.ArgumentList.Add("JavaScript");
                psi.ArgumentList.Add(scriptPath);

                var process = System.Diagnostics.Process.Start(psi);
                process?.WaitForExit(30000);
            }
            finally
            {
                try { File.Delete(scriptPath); } catch { }
            }

            // Read extracted PNGs as base64 into cache
            foreach (var (bundleId, outPath) in outPaths)
            {
                try
                {
                    if (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
                    {
                        var bytes = File.ReadAllBytes(outPath);
                        _iconCache[bundleId] = Convert.ToBase64String(bytes);
                    }
                    else
                    {
                        _iconCache[bundleId] = null;
                    }
                }
                catch { _iconCache[bundleId] = null; }
            }
        }
        catch { }
    }

    private static string? ExtractIconBase64(string appPath)
    {
        try
        {
            var escapedPath = appPath.Replace("\\", "\\\\").Replace("'", "\\'");
            var script = "ObjC.import('AppKit');\n" +
                "ObjC.import('Foundation');\n" +
                "var ws = $.NSWorkspace.sharedWorkspace;\n" +
                "var icon = ws.iconForFile('" + escapedPath + "');\n" +
                "var sz = $.NSMakeSize(128, 128);\n" +
                "var newImg = $.NSImage.alloc.initWithSize(sz);\n" +
                "newImg.lockFocus;\n" +
                "icon.drawInRectFromRectOperationFraction($.NSMakeRect(0,0,128,128), $.NSZeroRect, $.NSCompositingOperationSourceOver, 1.0);\n" +
                "newImg.unlockFocus;\n" +
                "var tiff = newImg.TIFFRepresentation;\n" +
                "var rep = $.NSBitmapImageRep.imageRepWithData(tiff);\n" +
                "var png = rep.representationUsingTypeProperties($.NSBitmapImageFileTypePNG, $({}));\n" +
                "var base64 = png.base64EncodedStringWithOptions(0);\n" +
                "ObjC.unwrap(base64);\n";

            var scriptPath = Path.Combine(Path.GetTempPath(), $"fkfinder_icon_{Guid.NewGuid():N}.js");
            File.WriteAllText(scriptPath, script);

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/usr/bin/osascript",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-l");
                psi.ArgumentList.Add("JavaScript");
                psi.ArgumentList.Add(scriptPath);

                var process = System.Diagnostics.Process.Start(psi);
                if (process == null) return null;
                var output = process.StandardOutput.ReadToEnd().Trim();
                var error = process.StandardError.ReadToEnd().Trim();
                process.WaitForExit(5000);

                if (!string.IsNullOrEmpty(error))
                    System.Diagnostics.Debug.WriteLine($"[ExtractIconBase64] Error for {appPath}: {error}");

                return string.IsNullOrEmpty(output) ? null : output;
            }
            finally
            {
                try { File.Delete(scriptPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ExtractIconBase64] Exception for {appPath}: {ex.Message}");
            return null;
        }
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
