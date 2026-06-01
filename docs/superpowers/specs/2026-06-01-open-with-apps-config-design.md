# 可配置"打开方式"应用列表 — 设计文档

## 概述

将右键菜单中"在...中打开"的编辑器应用（VS Code、Cursor、Kiro、Qoder）从硬编码改为用户可配置。用户可在设置中管理应用列表、控制一级/二级菜单层级，并新增应用。所有编辑器类应用的菜单图标使用系统真实图标。

## 决策记录

| 决策 | 选择 | 理由 |
|---|---|---|
| VS Code 是否纳入统一配置 | 是 | 去掉硬编码特殊处理，统一管理 |
| 启动方式 | 统一 `open -b {bundleId}` | 简单可靠，无需配置 CLI 名称 |
| 数据存储 | 新建 `open_with_apps` 专用表 | 结构化字段，查询高效，扩展性好 |
| 系统图标范围 | 仅编辑器应用 | Finder、终端保持现有 SVG 图标 |
| App 选择交互 | 弹窗列表 + 搜索 | 参考现有设置模态样式 |
| 一级/二级划分 | 每个应用独立标记 | Switch 开关，标签"显示在根目录" |
| 系统默认应用图标 | 也使用系统图标 | 统一视觉风格 |

## 数据模型

### 新增表 `open_with_apps`

SQLite migration v7，在 `SqliteSchema.cs` 中新增。

```sql
CREATE TABLE IF NOT EXISTS open_with_apps (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    bundle_id TEXT NOT NULL UNIQUE,
    label TEXT NOT NULL,
    is_top_level INTEGER NOT NULL DEFAULT 1,
    sort_order INTEGER NOT NULL DEFAULT 0,
    icon_base64 TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
```

默认插入记录：

| bundle_id | label | is_top_level |
|---|---|---|
| com.microsoft.VSCode | VS Code | 1 |
| com.todesktop.230313mzl4w4u92 | Cursor | 1 |
| dev.kiro.desktop | Kiro | 1 |
| com.qoder.ide | Qoder | 1 |

### ContextMenuAction 模型扩展

在 `ContextMenuAction` 中新增字段：

```csharp
public string? IconBase64 { get; init; }
```

UI 渲染层优先使用 `IconBase64`（渲染为 `<img src="data:image/png;base64,...">`），为空时回退到 `IconSvg`。

## 服务层

### 新增 `IOpenWithAppService`

文件：`src/MacExplorer/Services/IOpenWithAppService.cs`

```csharp
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

### 新增 `OpenWithAppService`

文件：`src/MacExplorer/Services/Impl/OpenWithAppService.cs`

- 注册为 Singleton
- 启动时预加载所有配置到内存缓存（类似 `SettingsService` 模式）
- 写操作同时更新数据库和缓存
- `GetInstalledAppsAsync()` 扫描 `/Applications`、`/System/Applications`、`~/Applications`，返回已安装应用列表（含名称、bundleId、图标 base64）
- 图标读取复用现有 JXA 方案（`NSWorkspace.sharedWorkspace.iconForFile:`）

### 数据模型

```csharp
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

### DI 注册

在 `MauiProgram.cs` 中新增：

```csharp
builder.Services.AddSingleton<IOpenWithAppService, OpenWithAppService>();
```

## 右键菜单集成

### 改造 `MacContextMenuService`

**移除**：
- `VscodeBasedEditors` 硬编码数组
- VS Code 的 `IsAppInstalled("com.microsoft.VSCode")` 单独判断逻辑

**新增依赖**：注入 `IOpenWithAppService`

**`GetFileContextMenuActionsAsync` 改造逻辑**：

```
1. 获取用户配置的应用列表
2. 一级应用（IsTopLevel=true）→ 直接添加到菜单，使用 IconBase64
3. 构建统一的"打开方式"子菜单：
   a. 用户配置的二级应用（IsTopLevel=false）→ 带系统图标
   b. 分隔线
   c. 系统注册的默认应用（GetApplicationsForFileAsync 结果）→ 带系统图标
4. 如果子菜单无任何项目，不显示"打开方式"
```

**`GetBackgroundContextMenuActionsAsync` 同理改造**。

### 系统默认应用图标获取

- `GetApplicationsForFileAsync` 返回的 `RegisteredApp` 已有 `BundleIdentifier`
- 在构建菜单时，通过 bundleId 查找已安装应用的 .app 路径
- 利用 `ScanInstalledApps` 阶段缓存的图标数据，或按需读取
- 图标以 `IconBase64` 传入 `ContextMenuAction`

### Finder 和终端

保持现状，不受影响：
- "在 Finder 中显示" → `Icons.Finder`（SVG）
- "在终端中打开" → `Icons.Terminal`（SVG）

## 设置页 UI

### 新增"打开方式"Tab

在 `SettingsDialog.razor` 中新增第五个 Tab。

**布局**：

```
┌─────────────────────────────────────────────────────┐
│  通用 │ 文件显示 │ 侧边栏 │ 外观 │ 打开方式 │          │
├─────────────────────────────────────────────────────┤
│                                                     │
│  [+ 添加应用]                                        │
│                                                     │
│  ┌───────────────────────────────────────────────┐  │
│  │ [icon] VS Code       显示在根目录  [⬤]    [×] │  │
│  │ [icon] Cursor        显示在根目录  [○]    [×] │  │
│  │ [icon] Kiro          显示在根目录  [⬤]    [×] │  │
│  │ [icon] Qoder         显示在根目录  [○]    [×] │  │
│  └───────────────────────────────────────────────┘  │
│                                                     │
│  开启"显示在根目录"的应用直接出现在右键菜单中，       │
│  关闭的应用收纳在"打开方式"子菜单中。                 │
│                                                     │
└─────────────────────────────────────────────────────┘
```

**交互**：
- 名称：只读显示
- 显示在根目录：Switch 开关，开=一级菜单，关=二级子菜单
- × 删除：移除该应用
- 图标：从数据库读取 base64 PNG 显示
- Switch 切换和删除操作立即保存到数据库

### 添加应用弹窗

复用 SettingsDialog 的模态样式（backdrop + 居中卡片）。

```
┌─────────────────────────────────────────┐
│  添加应用                          [×]  │
├─────────────────────────────────────────┤
│  🔍 搜索应用...                         │
│  ┌───────────────────────────────────┐  │
│  │ [icon] Safari              已添加 │  │
│  │ [icon] Google Chrome             │  │
│  │ [icon] Firefox                   │  │
│  │ [icon] Xcode                     │  │
│  │ [icon] iTerm2                    │  │
│  │ ...                              │  │
│  └───────────────────────────────────┘  │
└─────────────────────────────────────────┘
```

**交互**：
- 弹窗打开时异步扫描系统已安装应用，显示 loading 状态
- 已在配置中的应用显示"已添加"（灰色不可点）
- 搜索框按名称实时过滤
- 点击即添加（默认一级菜单），关闭弹窗，刷新列表
- 最大高度限制，超出内部滚动

## 文件变更清单

| 文件 | 变更类型 | 说明 |
|---|---|---|
| `Indexing/SqliteSchema.cs` | 修改 | 新增 migration v7，建 `open_with_apps` 表 |
| `Services/IOpenWithAppService.cs` | 新增 | 服务接口 |
| `Services/Impl/OpenWithAppService.cs` | 新增 | 服务实现 |
| `Models/OpenWithApp.cs` | 新增 | 数据模型 |
| `Models/ContextMenuAction.cs` | 修改 | 新增 `IconBase64` 字段 |
| `Platforms/MacCatalyst/Services/MacContextMenuService.cs` | 修改 | 移除硬编码，注入新服务，统一菜单构建 |
| `Components/Dialogs/SettingsDialog.razor` | 修改 | 新增"打开方式"Tab + 添加弹窗 |
| `Components/ContextMenu/ContextMenu.razor` | 修改 | 支持 `IconBase64` 渲染 |
| `MauiProgram.cs` | 修改 | 注册新服务 |
| `wwwroot/css/app.css` | 修改 | 新增设置页打开方式 Tab 样式 |
