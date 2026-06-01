# 可配置"打开方式"应用列表 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将右键菜单中"在...中打开"的编辑器应用从硬编码改为用户可配置，支持设置页管理、系统图标读取、一级/二级菜单层级控制。

**Architecture:** 新增 `IOpenWithAppService` 服务 + `open_with_apps` SQLite 表管理可配置应用列表。`MacContextMenuService` 改为从该服务读取配置，替代硬编码数组。设置对话框新增"打开方式"Tab 提供管理 UI。应用图标通过 JXA 脚本读取系统真实图标并缓存到数据库。

**Tech Stack:** .NET MAUI Blazor (Mac Catalyst), SQLite (Microsoft.Data.Sqlite), JXA (JavaScript for Automation) via osascript

---

### Task 1: 数据模型 — 新增 OpenWithApp 和 AppListItem 类

**Files:**
- Create: `src/MacExplorer/Models/OpenWithApp.cs`

- [ ] **Step 1: 创建 OpenWithApp 模型类**

```csharp
namespace MacExplorer.Models;

public class OpenWithApp
{
    public int Id { get; set; }
    public string BundleId { get; set; } = "";
    public string Label { get; set; } = "";
    public bool IsTopLevel { get; set; } = true;
    public int SortOrder { get; set; }
    public string? IconBase64 { get; set; }
}

public class AppListItem
{
    public string Name { get; set; } = "";
    public string BundleId { get; set; } = "";
    public string AppPath { get; set; } = "";
    public string? IconBase64 { get; set; }
}
```

- [ ] **Step 2: 提交**

```bash
git add src/MacExplorer/Models/OpenWithApp.cs
git commit -m "feat: add OpenWithApp and AppListItem models"
```

---

### Task 2: ContextMenuAction 扩展 IconBase64 字段

**Files:**
- Modify: `src/MacExplorer/Models/ContextMenuAction.cs:3-16`

- [ ] **Step 1: 在 ContextMenuAction 中添加 IconBase64 属性**

在 `IsQuickAction` 属性之后添加：

```csharp
public string? IconBase64 { get; init; }
```

完整类变为：

```csharp
public class ContextMenuAction
{
    public string Label { get; init; } = string.Empty;
    public string IconSvg { get; init; } = string.Empty;
    public string ShortcutText { get; init; } = string.Empty;
    public bool IsEnabled { get; init; } = true;
    public bool IsSeparator { get; init; }
    public Func<Task>? Execute { get; init; }
    public IReadOnlyList<ContextMenuAction>? SubItems { get; init; }
    public string? Tag { get; init; }
    public bool IsQuickAction { get; init; }
    public string? IconBase64 { get; init; }

    public static ContextMenuAction Separator => new() { IsSeparator = true };
}
```

- [ ] **Step 2: 提交**

```bash
git add src/MacExplorer/Models/ContextMenuAction.cs
git commit -m "feat: add IconBase64 property to ContextMenuAction"
```

---

### Task 3: 数据库迁移 — 创建 open_with_apps 表

**Files:**
- Modify: `src/MacExplorer/Indexing/SqliteSchema.cs`

- [ ] **Step 1: 更新 CurrentVersion 为 7**

将第 7 行的 `CurrentVersion` 从 6 改为 7：

```csharp
public const int CurrentVersion = 7;
```

- [ ] **Step 2: 在 Initialize 方法中添加 v7 迁移调用**

在第 112 行 `if (storedVersion < 6)` 块之后添加：

```csharp
if (storedVersion < 7)
    MigrateToV7(connection, transaction);
```

- [ ] **Step 3: 添加 MigrateToV7 方法**

在 `MigrateToV6` 方法之后添加：

```csharp
private static void MigrateToV7(SqliteConnection connection, SqliteTransaction transaction)
{
    ExecuteNonQuery(connection, """
        CREATE TABLE IF NOT EXISTS open_with_apps (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            bundle_id TEXT NOT NULL UNIQUE,
            label TEXT NOT NULL,
            is_top_level INTEGER NOT NULL DEFAULT 1,
            sort_order INTEGER NOT NULL DEFAULT 0,
            icon_base64 TEXT,
            created_at TEXT NOT NULL DEFAULT (datetime('now'))
        )
        """, transaction);

    // Insert default editors
    var defaults = new[]
    {
        ("com.microsoft.VSCode", "VS Code", 1, 0),
        ("com.todesktop.230313mzl4w4u92", "Cursor", 1, 1),
        ("dev.kiro.desktop", "Kiro", 1, 2),
        ("com.qoder.ide", "Qoder", 1, 3),
    };

    foreach (var (bundleId, label, isTopLevel, sortOrder) in defaults)
    {
        ExecuteNonQuery(connection, """
            INSERT OR IGNORE INTO open_with_apps (bundle_id, label, is_top_level, sort_order)
            VALUES (@bundleId, @label, @isTopLevel, @sortOrder)
            """, transaction,
            ("@bundleId", bundleId),
            ("@label", label),
            ("@isTopLevel", isTopLevel),
            ("@sortOrder", sortOrder));
    }

    System.Diagnostics.Debug.WriteLine("Schema migrated to v7: open_with_apps");
}
```

- [ ] **Step 4: 提交**

```bash
git add src/MacExplorer/Indexing/SqliteSchema.cs
git commit -m "feat: add open_with_apps table migration v7"
```

---

### Task 4: IOpenWithAppService 接口

**Files:**
- Create: `src/MacExplorer/Services/IOpenWithAppService.cs`

- [ ] **Step 1: 创建接口**

```csharp
using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IOpenWithAppService
{
    Task<List<OpenWithApp>> GetAllAsync();
    Task<List<OpenWithApp>> GetTopLevelAppsAsync();
    Task<List<OpenWithApp>> GetSubmenuAppsAsync();
    Task AddAsync(string bundleId, string label, bool isTopLevel);
    Task UpdateAsync(int id, string? label, bool? isTopLevel, int? sortOrder);
    Task RemoveAsync(int id);
    Task<List<AppListItem>> GetInstalledAppsAsync();
}
```

- [ ] **Step 2: 提交**

```bash
git add src/MacExplorer/Services/IOpenWithAppService.cs
git commit -m "feat: add IOpenWithAppService interface"
```

---

### Task 5: OpenWithAppService 实现

**Files:**
- Create: `src/MacExplorer/Services/Impl/OpenWithAppService.cs`

- [ ] **Step 1: 创建服务实现**

```csharp
using System.Diagnostics;
using MacExplorer.Models;
using MacExplorer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public class OpenWithAppService : IOpenWithAppService, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<OpenWithAppService>? _logger;
    private List<OpenWithApp> _cache = new();
    private readonly object _lock = new();

    public OpenWithAppService(DatabaseConnectionFactory connectionFactory, ILogger<OpenWithAppService>? logger = null)
    {
        _connection = connectionFactory.GetConnection();
        _logger = logger;
        LoadAll();
    }

    private void LoadAll()
    {
        try
        {
            var list = new List<OpenWithApp>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, bundle_id, label, is_top_level, sort_order, icon_base64 FROM open_with_apps ORDER BY sort_order";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new OpenWithApp
                {
                    Id = reader.GetInt32(0),
                    BundleId = reader.GetString(1),
                    Label = reader.GetString(2),
                    IsTopLevel = reader.GetInt32(3) != 0,
                    SortOrder = reader.GetInt32(4),
                    IconBase64 = reader.IsDBNull(5) ? null : reader.GetString(5),
                });
            }
            lock (_lock) { _cache = list; }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load open_with_apps in {Method}", nameof(LoadAll));
        }
    }

    public Task<List<OpenWithApp>> GetAllAsync()
    {
        lock (_lock) { return Task.FromResult(new List<OpenWithApp>(_cache)); }
    }

    public Task<List<OpenWithApp>> GetTopLevelAppsAsync()
    {
        lock (_lock) { return Task.FromResult(_cache.Where(a => a.IsTopLevel).ToList()); }
    }

    public Task<List<OpenWithApp>> GetSubmenuAppsAsync()
    {
        lock (_lock) { return Task.FromResult(_cache.Where(a => !a.IsTopLevel).ToList()); }
    }

    public Task AddAsync(string bundleId, string label, bool isTopLevel)
    {
        try
        {
            // Read system icon for the app
            var iconBase64 = ReadAppIconBase64(bundleId);

            int maxOrder;
            lock (_lock) { maxOrder = _cache.Count > 0 ? _cache.Max(a => a.SortOrder) + 1 : 0; }

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO open_with_apps (bundle_id, label, is_top_level, sort_order, icon_base64)
                VALUES (@bundleId, @label, @isTopLevel, @sortOrder, @iconBase64)
                """;
            cmd.Parameters.AddWithValue("@bundleId", bundleId);
            cmd.Parameters.AddWithValue("@label", label);
            cmd.Parameters.AddWithValue("@isTopLevel", isTopLevel ? 1 : 0);
            cmd.Parameters.AddWithValue("@sortOrder", maxOrder);
            cmd.Parameters.AddWithValue("@iconBase64", (object?)iconBase64 ?? DBNull.Value);
            cmd.ExecuteNonQuery();

            LoadAll(); // Refresh cache
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to add open_with_app {BundleId}", bundleId);
        }
        return Task.CompletedTask;
    }

    public Task UpdateAsync(int id, string? label, bool? isTopLevel, int? sortOrder)
    {
        try
        {
            var existing = _cache.FirstOrDefault(a => a.Id == id);
            if (existing == null) return Task.CompletedTask;

            var newLabel = label ?? existing.Label;
            var newIsTopLevel = isTopLevel ?? existing.IsTopLevel;
            var newSortOrder = sortOrder ?? existing.SortOrder;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE open_with_apps SET label = @label, is_top_level = @isTopLevel, sort_order = @sortOrder WHERE id = @id
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@label", newLabel);
            cmd.Parameters.AddWithValue("@isTopLevel", newIsTopLevel ? 1 : 0);
            cmd.Parameters.AddWithValue("@sortOrder", newSortOrder);
            cmd.ExecuteNonQuery();

            LoadAll();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to update open_with_app {Id}", id);
        }
        return Task.CompletedTask;
    }

    public Task RemoveAsync(int id)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM open_with_apps WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();

            LoadAll();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to remove open_with_app {Id}", id);
        }
        return Task.CompletedTask;
    }

    public Task<List<AppListItem>> GetInstalledAppsAsync()
    {
        return Task.Run(() =>
        {
            var apps = new List<AppListItem>();
            var searchPaths = new[] { "/Applications", "/System/Applications", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications") };

            foreach (var searchPath in searchPaths)
            {
                try
                {
                    if (!Directory.Exists(searchPath)) continue;
                    foreach (var dir in Directory.EnumerateDirectories(searchPath, "*.app", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            var plistPath = Path.Combine(dir, "Contents", "Info.plist");
                            if (!File.Exists(plistPath)) continue;

                            var dict = Foundation.NSMutableDictionary.FromFile(plistPath);
                            if (dict == null) continue;

                            var bundleId = dict["CFBundleIdentifier"]?.ToString();
                            var name = dict["CFBundleName"]?.ToString() ?? Path.GetFileNameWithoutExtension(dir);
                            if (string.IsNullOrEmpty(bundleId)) continue;

                            var iconBase64 = ExtractIconBase64(dir);

                            apps.Add(new AppListItem
                            {
                                Name = name,
                                BundleId = bundleId,
                                AppPath = dir,
                                IconBase64 = iconBase64,
                            });
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return apps.OrderBy(a => a.Name).ToList();
        });
    }

    private static string? ExtractIconBase64(string appPath)
    {
        try
        {
            // Use JXA to get app icon as base64 PNG
            var escapedPath = appPath.Replace("\\", "\\\\").Replace("'", "\\'");
            var script = $$"""
                ObjC.import('AppKit');
                ObjC.import('Foundation');
                var ws = $.NSWorkspace.sharedWorkspace;
                var icon = ws.iconForFile('{{escapedPath}}');
                var sz = $.NSMakeSize(32, 32);
                var newImg = $.NSImage.alloc.initWithSize(sz);
                newImg.lockFocus;
                icon.drawInRectFromRectOperationFraction($.NSMakeRect(0,0,32,32), $.NSZeroRect, $.NSCompositingOperationSourceOver, 1.0);
                newImg.unlockFocus;
                var tiff = newImg.TIFFRepresentation;
                var rep = $.NSBitmapImageRep.imageRepWithData(tiff);
                var png = rep.representationUsingTypeProperties($.NSBitmapImageFileTypePNG, $({}));
                var base64 = png.base64EncodedStringWithOptions(0);
                ObjC.unwrap(base64);
                """;

            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add("JavaScript");
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);

            var process = Process.Start(psi);
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);

            return string.IsNullOrEmpty(output) ? null : output;
        }
        catch { return null; }
    }

    private string? ReadAppIconBase64(string bundleId)
    {
        // Find the .app path for this bundleId, then extract icon
        var searchPaths = new[] { "/Applications", "/System/Applications", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications") };
        foreach (var searchPath in searchPaths)
        {
            try
            {
                if (!Directory.Exists(searchPath)) continue;
                foreach (var dir in Directory.EnumerateDirectories(searchPath, "*.app", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var plistPath = Path.Combine(dir, "Contents", "Info.plist");
                        if (!File.Exists(plistPath)) continue;
                        var dict = Foundation.NSMutableDictionary.FromFile(plistPath);
                        if (dict?["CFBundleIdentifier"]?.ToString() == bundleId)
                            return ExtractIconBase64(dir);
                    }
                    catch { }
                }
            }
            catch { }
        }
        return null;
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }
}
```

- [ ] **Step 2: 提交**

```bash
git add src/MacExplorer/Services/Impl/OpenWithAppService.cs
git commit -m "feat: implement OpenWithAppService with icon reading"
```

---

### Task 6: DI 注册 + IApplicationLauncherService 简化

**Files:**
- Modify: `src/MacExplorer/MauiProgram.cs:120-121`
- Modify: `src/MacExplorer/Services/IApplicationLauncherService.cs`
- Modify: `src/MacExplorer/Platforms/MacCatalyst/Services/MacApplicationLauncherService.cs`

- [ ] **Step 1: 在 MauiProgram.cs 中注册 OpenWithAppService**

在第 121 行（`IContextMenuService` 注册）之后添加：

```csharp
builder.Services.AddSingleton<IOpenWithAppService>(sp =>
    new OpenWithAppService(
        sp.GetRequiredService<DatabaseConnectionFactory>(),
        sp.GetService<ILoggerFactory>()?.CreateLogger<OpenWithAppService>()));
```

同时需要添加 using：

```csharp
using MacExplorer.Services.Impl;
```

- [ ] **Step 2: 简化 IApplicationLauncherService — 移除 OpenInVsCodeAsync**

将 `IApplicationLauncherService.cs` 改为：

```csharp
namespace MacExplorer.Services;

public interface IApplicationLauncherService
{
    Task OpenFileAsync(string filePath);
    Task OpenFileWithAppAsync(string filePath, string bundleIdentifier);
    Task OpenInTerminalAsync(string directoryPath);
    Task OpenInEditorAsync(string path, string cliName, string bundleId);
    Task RevealInFinderAsync(string path);
}
```

- [ ] **Step 3: 从 MacApplicationLauncherService 移除 OpenInVsCodeAsync**

删除 `OpenInVsCodeAsync` 方法（第 124-127 行）。

- [ ] **Step 4: 提交**

```bash
git add src/MacExplorer/MauiProgram.cs src/MacExplorer/Services/IApplicationLauncherService.cs src/MacExplorer/Platforms/MacCatalyst/Services/MacApplicationLauncherService.cs
git commit -m "feat: register OpenWithAppService, remove OpenInVsCodeAsync"
```

---

### Task 7: 改造 MacContextMenuService — 使用可配置应用列表

**Files:**
- Modify: `src/MacExplorer/Platforms/MacCatalyst/Services/MacContextMenuService.cs`

- [ ] **Step 1: 重构 MacContextMenuService**

完整替换文件内容：

```csharp
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

    public MacContextMenuService(IApplicationLauncherService launcher, IFileService fileService, IOpenWithAppService openWithService)
    {
        _launcher = launcher;
        _fileService = fileService;
        _openWithService = openWithService;
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

        // Configured "Open With" apps
        await AddOpenWithActionsAsync(actions, entry.FullPath);

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

        // Configured "Open With" apps
        await AddOpenWithActionsAsync(actions, currentDirectory);

        actions.Add(ContextMenuAction.Separator);

        actions.Add(new() { Label = "复制路径", IconSvg = Icons.CopyPath, Execute = () => CopyToClipboard(currentDirectory) });

        return await Task.FromResult(actions.AsReadOnly());
    }

    /// <summary>
    /// Adds configured "Open With" apps to the menu.
    /// Top-level apps are added directly; others go into a unified "打开方式" submenu
    /// along with system-registered default apps.
    /// </summary>
    private async Task AddOpenWithActionsAsync(List<ContextMenuAction> actions, string path)
    {
        var allApps = await _openWithService.GetAllAsync();
        var topLevel = allApps.Where(a => a.IsTopLevel).ToList();
        var submenuApps = allApps.Where(a => !a.IsTopLevel).ToList();

        // Add top-level apps directly to menu
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

        // Build unified "打开方式" submenu
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
    private string? GetAppBundleIconBase64(string bundleIdentifier)
    {
        try
        {
            var searchPaths = new[] { "/Applications", "/System/Applications", "/Applications/Utilities" };
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
                        if (dict?["CFBundleIdentifier"]?.ToString() == bundleIdentifier)
                            return ExtractIconBase64(dir);
                    }
                    catch { }
                }
            }
        }
        catch { }
        return null;
    }

    private static string? ExtractIconBase64(string appPath)
    {
        try
        {
            var escapedPath = appPath.Replace("\\", "\\\\").Replace("'", "\\'");
            var script = $"ObjC.import('AppKit');ObjC.import('Foundation');var ws=$.NSWorkspace.sharedWorkspace;var icon=ws.iconForFile('{escapedPath}');var sz=$.NSMakeSize(32,32);var newImg=$.NSImage.alloc.initWithSize(sz);newImg.lockFocus;icon.drawInRectFromRectOperationFraction($.NSMakeRect(0,0,32,32),$.NSZeroRect,$.NSCompositingOperationSourceOver,1.0);newImg.unlockFocus;var tiff=newImg.TIFFRepresentation;var rep=$.NSBitmapImageRep.imageRepWithData(tiff);var png=rep.representationUsingTypeProperties($.NSBitmapImageFileTypePNG,$({}));var base64=png.base64EncodedStringWithOptions(0);ObjC.unwrap(base64);";

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
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);

            var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);

            return string.IsNullOrEmpty(output) ? null : output;
        }
        catch { return null; }
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
```

- [ ] **Step 2: 提交**

```bash
git add src/MacExplorer/Platforms/MacCatalyst/Services/MacContextMenuService.cs
git commit -m "feat: refactor MacContextMenuService to use configurable open-with apps"
```

---

### Task 8: ContextMenu.razor 支持 IconBase64 渲染

**Files:**
- Modify: `src/MacExplorer/Components/ContextMenu/ContextMenu.razor`

- [ ] **Step 1: 修改图标渲染逻辑，优先使用 IconBase64**

在 ContextMenu.razor 中，有 3 处图标渲染需要修改。每处的模式相同：将 `<svg>` 标签改为优先检查 `IconBase64`。

**第 1 处：Quick action bar 图标（第 13 行）**

将：
```razor
<svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor"><path d="@qa.IconSvg"/></svg>
```

改为：
```razor
@if (!string.IsNullOrEmpty(qa.IconBase64))
{
    <img src="data:image/png;base64,@qa.IconBase64" width="16" height="16" style="object-fit: contain;" />
}
else
{
    <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor"><path d="@qa.IconSvg"/></svg>
}
```

**第 2 处：一级菜单项图标（第 35-38 行 和 第 81-84 行）**

在两处 `<span class="context-menu-icon">` 块中，将：
```razor
@if (!string.IsNullOrEmpty(action.IconSvg))
{
    <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><path d="@action.IconSvg"/></svg>
}
```

改为：
```razor
@if (!string.IsNullOrEmpty(action.IconBase64))
{
    <img src="data:image/png;base64,@action.IconBase64" width="14" height="14" style="object-fit: contain;" />
}
else if (!string.IsNullOrEmpty(action.IconSvg))
{
    <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><path d="@action.IconSvg"/></svg>
}
```

**第 3 处：子菜单项图标（第 58-61 行）**

将：
```razor
@if (!string.IsNullOrEmpty(sub.IconSvg))
{
    <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><path d="@sub.IconSvg"/></svg>
}
```

改为：
```razor
@if (!string.IsNullOrEmpty(sub.IconBase64))
{
    <img src="data:image/png;base64,@sub.IconBase64" width="14" height="14" style="object-fit: contain;" />
}
else if (!string.IsNullOrEmpty(sub.IconSvg))
{
    <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><path d="@sub.IconSvg"/></svg>
}
```

- [ ] **Step 2: 提交**

```bash
git add src/MacExplorer/Components/ContextMenu/ContextMenu.razor
git commit -m "feat: support IconBase64 rendering in ContextMenu component"
```

---

### Task 9: 设置页 — 新增"打开方式"Tab

**Files:**
- Modify: `src/MacExplorer/Components/Dialogs/SettingsDialog.razor`

- [ ] **Step 1: 添加 Tab 按钮**

在第 14 行（外观 tab）之后添加：

```razor
<div class="settings-tab @(_activeTab == "openwith" ? "active" : "")" @onclick="@(() => _activeTab = "openwith")">打开方式</div>
```

- [ ] **Step 2: 添加注入和状态变量**

在 `@inject` 指令区域添加：

```razor
@inject IOpenWithAppService OpenWithService
```

在 `@code` 块中添加状态变量（在 `_vibrancyAlpha` 之后）：

```csharp
// Open With apps state
private List<OpenWithApp> _openWithApps = new();
private bool _showAddAppModal;
private List<AppListItem> _installedApps = new();
private string _appSearchText = "";
private bool _isLoadingApps;
```

- [ ] **Step 3: 在 OnInitialized 中加载数据**

在 `OnInitialized()` 方法末尾添加：

```csharp
// Load open-with apps
_ = LoadOpenWithAppsAsync();
```

添加方法：

```csharp
private async Task LoadOpenWithAppsAsync()
{
    _openWithApps = await OpenWithService.GetAllAsync();
    StateHasChanged();
}
```

- [ ] **Step 4: 添加"打开方式"Tab 内容**

在 `else if (_activeTab == "appearance")` 块的结束 `}` 之后、`</div>` (settings-tab-content) 之前添加：

```razor
else if (_activeTab == "openwith")
{
    <div class="settings-list">
        <div class="settings-item" style="justify-content: flex-start; gap: 8px;">
            <button class="settings-dialog-btn" @onclick="OpenAddAppModal" style="margin: 0;">+ 添加应用</button>
        </div>

        @foreach (var app in _openWithApps)
        {
            <div class="settings-item">
                <div class="settings-item-info" style="flex-direction: row; align-items: center; gap: 10px;">
                    @if (!string.IsNullOrEmpty(app.IconBase64))
                    {
                        <img src="data:image/png;base64,@app.IconBase64" width="24" height="24" style="object-fit: contain; border-radius: 4px;" />
                    }
                    else
                    {
                        <svg width="24" height="24" viewBox="0 0 24 24" fill="currentColor"><path d="@Icons.CodeEditor"/></svg>
                    }
                    <div class="settings-item-label" style="margin: 0;">@app.Label</div>
                </div>
                <div style="display: flex; align-items: center; gap: 12px;">
                    <span style="font-size: 0.85em; opacity: 0.7;">显示在根目录</span>
                    <label class="settings-toggle" @onclick:stopPropagation>
                        <input type="checkbox" checked="@app.IsTopLevel"
                               @onchange="@(e => OnTopLevelToggle(app, e))" />
                        <span class="settings-toggle-slider"></span>
                    </label>
                    <button class="settings-dialog-btn" style="padding: 2px 8px; margin: 0; opacity: 0.6;"
                            @onclick="@(() => OnRemoveApp(app))" @onclick:stopPropagation>×</button>
                </div>
            </div>
        }

        @if (_openWithApps.Count == 0)
        {
            <div style="text-align: center; padding: 20px; opacity: 0.5; font-size: 0.9em;">
                尚未配置任何应用，点击上方按钮添加
            </div>
        }
    </div>
    <div class="settings-restart-hint">开启"显示在根目录"的应用直接出现在右键菜单中，关闭的应用收纳在"打开方式"子菜单中。</div>
}
```

- [ ] **Step 5: 添加交互方法**

在 `@code` 块末尾添加：

```csharp
// Open With tab handlers
private async Task OnTopLevelToggle(OpenWithApp app, ChangeEventArgs e)
{
    var isTopLevel = (bool)(e.Value ?? false);
    await OpenWithService.UpdateAsync(app.Id, null, isTopLevel, null);
    await LoadOpenWithAppsAsync();
}

private async Task OnRemoveApp(OpenWithApp app)
{
    await OpenWithService.RemoveAsync(app.Id);
    await LoadOpenWithAppsAsync();
}

private async Task OpenAddAppModal()
{
    _showAddAppModal = true;
    _isLoadingApps = true;
    _appSearchText = "";
    StateHasChanged();

    _installedApps = await OpenWithService.GetInstalledAppsAsync();
    _isLoadingApps = false;
    StateHasChanged();
}

private void CloseAddAppModal()
{
    _showAddAppModal = false;
    StateHasChanged();
}

private async Task AddApp(AppListItem app)
{
    await OpenWithService.AddAsync(app.BundleId, app.Name, isTopLevel: true);
    _showAddAppModal = false;
    await LoadOpenWithAppsAsync();
}

private IEnumerable<AppListItem> FilteredInstalledApps
{
    get
    {
        var existing = new HashSet<string>(_openWithApps.Select(a => a.BundleId));
        var query = _installedApps.Where(a => !existing.Contains(a.BundleId));
        if (!string.IsNullOrWhiteSpace(_appSearchText))
            query = query.Where(a => a.Name.Contains(_appSearchText, StringComparison.OrdinalIgnoreCase));
        return query;
    }
}
```

- [ ] **Step 6: 添加添加应用弹窗 HTML**

在 `</div>` (settings-dialog 的闭合标签) 之前添加弹窗：

```razor
@if (_showAddAppModal)
{
    <div class="settings-dropdown-backdrop" @onclick="CloseAddAppModal"></div>
    <div class="settings-dialog" style="position: fixed; top: 50%; left: 50%; transform: translate(-50%, -50%); width: 420px; max-height: 500px; z-index: 1001; display: flex; flex-direction: column;">
        <div class="settings-dialog-title" style="display: flex; justify-content: space-between; align-items: center;">
            添加应用
            <button class="settings-dialog-btn" style="padding: 2px 8px; margin: 0;" @onclick="CloseAddAppModal">×</button>
        </div>

        <div style="padding: 8px 16px;">
            <input type="text" placeholder="搜索应用..."
                   style="width: 100%; padding: 8px 12px; border: 1px solid rgba(128,128,128,0.3); border-radius: 6px; background: rgba(255,255,255,0.05); color: inherit; font-size: 0.9em; outline: none; box-sizing: border-box;"
                   @bind="_appSearchText" @bind:event="oninput" />
        </div>

        <div style="flex: 1; overflow-y: auto; padding: 0 16px 16px;">
            @if (_isLoadingApps)
            {
                <div style="text-align: center; padding: 30px; opacity: 0.5;">正在扫描应用...</div>
            }
            else
            {
                @foreach (var app in FilteredInstalledApps)
                {
                    <div class="settings-item" style="cursor: pointer;" @onclick="@(() => AddApp(app))">
                        <div class="settings-item-info" style="flex-direction: row; align-items: center; gap: 10px;">
                            @if (!string.IsNullOrEmpty(app.IconBase64))
                            {
                                <img src="data:image/png;base64,@app.IconBase64" width="24" height="24" style="object-fit: contain; border-radius: 4px;" />
                            }
                            else
                            {
                                <svg width="24" height="24" viewBox="0 0 24 24" fill="currentColor" style="opacity: 0.3;"><path d="@Icons.Apps"/></svg>
                            }
                            <div>
                                <div class="settings-item-label" style="margin: 0;">@app.Name</div>
                                <div style="font-size: 0.75em; opacity: 0.5;">@app.BundleId</div>
                            </div>
                        </div>
                    </div>
                }

                @if (!FilteredInstalledApps.Any())
                {
                    <div style="text-align: center; padding: 20px; opacity: 0.5; font-size: 0.9em;">
                        @(_installedApps.Count == 0 ? "未找到已安装的应用" : "没有匹配的应用")
                    </div>
                }
            }
        </div>
    </div>
}
```

- [ ] **Step 7: 提交**

```bash
git add src/MacExplorer/Components/Dialogs/SettingsDialog.razor
git commit -m "feat: add Open With tab to Settings dialog"
```

---

### Task 10: 验证和收尾

- [ ] **Step 1: 构建验证**

```bash
cd /Users/jiangxinji/Documents/FKFinder && dotnet build
```

确认无编译错误。

- [ ] **Step 2: 检查 ContextMenuHelper.cs 中的 SF Symbol 映射**

检查 `src/MacExplorer/Platforms/MacCatalyst/Handlers/ContextMenuHelper.cs` 中的 `MapMenuLabelToSFSymbol` 方法，确认动态生成的菜单标签（如 "在 VS Code 中打开"）不会因不匹配而丢失 SF Symbol。由于原生菜单当前被注释掉（使用 Web 实现），这一步仅确认即可，无需修改。

- [ ] **Step 3: 最终提交**

```bash
git add -A
git commit -m "feat: configurable open-with apps — complete implementation"
```
