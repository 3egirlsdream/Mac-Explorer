# 设置系统方案设计

## 概述

Mac Explorer 的设置系统采用 **Tab 分页布局 + SQLite 持久化** 方案，通过 `ISettingsService` 统一管理所有配置项的读写，支持文件过滤、侧边栏可见性、窗口外观等多维度设置。

## 架构总览

```
┌──────────────────────────────────────────────────────┐
│              SettingsDialog.razor                     │
│  ┌──────────┬──────────┬──────────┬──────────┐       │
│  │   通用   │ 文件显示  │  侧边栏  │   外观   │ ← Tab │
│  └──────────┴──────────┴──────────┴──────────┘       │
│  │  读取/写入设置值                                   │
│  ▼                                                   │
│  ISettingsService (SQLite key-value store)            │
│  ▲              ▲                    ▲               │
│  │ 读取初始值    │ 持久化回调          │ 启动时读取     │
│  │              │                    │               │
│  SortFilter    FinderSidebar.razor   App.xaml.cs     │
│  ViewModel     (侧边栏可见性)        → VibrancyHelper│
│  (文件过滤)           ▲              (毛玻璃,重启生效)│
│        │              │                              │
│        ▼              │                              │
│  FileListViewModel ───┘                              │
│  (属性转发 + 重过滤触发 + 侧边栏通知)                 │
└──────────────────────────────────────────────────────┘
```

## 设置分类

### Tab 1: 通用

| 设置项 | 设置键 | 默认值 | 存储方式 |
|--------|--------|--------|----------|
| 设为默认文件管理器 | — | 系统查询 | `IDefaultAppService` 直接调用 |
| AI 智能分析 | `IsAiAnalysisEnabled` | `false` | ViewModel 属性 → Settings |

### Tab 2: 文件显示

| 设置项 | 设置键 | 默认值 | 所在 ViewModel |
|--------|--------|--------|----------------|
| 隐藏系统文件 | `HideSystemFiles` | `true` | `SortFilterViewModel` |
| 隐藏以 . 开头的文件 | `HideDotFiles` | `true` | `SortFilterViewModel` |
| 隐藏以 . 开头的文件夹 | `HideDotFolders` | `true` | `SortFilterViewModel` |

### Tab 3: 侧边栏

分三组显示，每项独立控制显示/隐藏：

**收藏组：**

| 设置项 | 设置键 | 默认值 |
|--------|--------|--------|
| 用户名目录 | `sidebar_show_username` | `true` |
| 桌面 | `sidebar_show_desktop` | `true` |
| 文稿 | `sidebar_show_documents` | `true` |
| 下载 | `sidebar_show_downloads` | `true` |
| 图片 | `sidebar_show_pictures` | `true` |
| 音乐 | `sidebar_show_music` | `true` |

**位置组：**

| 设置项 | 设置键 | 默认值 |
|--------|--------|--------|
| Macintosh HD | `sidebar_show_macintosh_hd` | `true` |
| 应用程序 | `sidebar_show_applications` | `true` |
| 废纸篓 | `sidebar_show_trash` | `true` |

**AI 智能组：**

| 设置项 | 设置键 | 默认值 |
|--------|--------|--------|
| 人物 | `sidebar_show_ai_people` | `true` |
| 分类 | `sidebar_show_ai_categories` | `true` |
| 地点 | `sidebar_show_ai_locations` | `true` |
| 日期 | `sidebar_show_ai_dates` | `true` |
| 文字搜索 | `sidebar_show_ai_text_search` | `true` |

### Tab 4: 外观

毛玻璃效果配置，修改后**重启生效**。

| 设置项 | 设置键 | 类型 | 默认值 | UI 控件 |
|--------|--------|------|--------|---------|
| 毛玻璃效果 | `vibrancy_enabled` | bool | `true` | 开关 |
| 透明度 | `vibrancy_alpha` | double | `0.85` | 滑块 (0.3~1.0) |

透明度滑块在毛玻璃关闭时为 disabled 状态。底部显示"修改后需重启应用生效"提示。

与其他 Tab 不同，外观设置不通过 ViewModel 生效，而是由 `App.xaml.cs` 在启动时读取并传递给 `VibrancyHelper` 静态属性。详见 `docs/mac-catalyst-transparent-window.md`。

## 涉及文件

| 文件 | 职责 |
|------|------|
| `Components/Dialogs/SettingsDialog.razor` | 设置 UI（4 个 Tab、开关/滑块控件、事件处理） |
| `ViewModels/SortFilterViewModel.cs` | 文件过滤属性定义、持久化回调、过滤执行 |
| `ViewModels/FileListViewModel.cs` | 属性转发、重过滤触发、侧边栏变更通知 |
| `Components/Sidebar/FinderSidebar.razor` | 侧边栏渲染、可见性守卫、设置重载 |
| `Components/Pages/Home.razor` | MDialog 容器（MaxWidth=480） |
| `wwwroot/css/app.css` | Tab 样式、分组标题、紧凑项、滑块样式 |
| `App.xaml.cs` | 注入 ISettingsService，启动时读取毛玻璃配置 |
| `Platforms/MacCatalyst/Handlers/VibrancyHelper.cs` | 毛玻璃 Enabled/Alpha 静态属性，ConfigureNSWindow 读取 |

## 关键实现细节

### 1. 文件过滤 — ObservableProperty + 持久化回调

`SortFilterViewModel` 使用 CommunityToolkit.Mvvm 的 `[ObservableProperty]` 自动生成属性变更通知，并通过 `partial void On<Property>Changed` 回调持久化到 `ISettingsService`：

```csharp
// 属性定义（源码生成器自动生成属性和 OnPropertyChanged 通知）
[ObservableProperty]
private bool _hideSystemFiles = true;

[ObservableProperty]
private bool _hideDotFiles = true;

[ObservableProperty]
private bool _hideDotFolders = true;

// 持久化回调
partial void OnHideSystemFilesChanged(bool value) => _settingsService?.Set("HideSystemFiles", value);
partial void OnHideDotFilesChanged(bool value) => _settingsService?.Set("HideDotFiles", value);
partial void OnHideDotFoldersChanged(bool value) => _settingsService?.Set("HideDotFolders", value);

// 构造函数读取初始值
HideSystemFiles = _settingsService.Get<bool>("HideSystemFiles", true);
HideDotFiles = _settingsService.Get<bool>("HideDotFiles", true);
HideDotFolders = _settingsService.Get<bool>("HideDotFolders", true);
```

过滤逻辑在 `ApplySortAndGroup` 中链式执行：

```csharp
var filtered = _rawEntries.Where(e => !e.Name.EndsWith(".fkfinder-tmp"));
if (HideSystemFiles)
    filtered = filtered.Where(e => !SystemFileNames.Contains(e.Name));
if (HideDotFiles)
    filtered = filtered.Where(e => !(!e.IsDirectory && e.Name.StartsWith('.')));
if (HideDotFolders)
    filtered = filtered.Where(e => !(e.IsDirectory && e.Name.StartsWith('.')));
```

其中 `SystemFileNames` 是硬编码的系统文件名集合：

```csharp
private static readonly HashSet<string> SystemFileNames = new(StringComparer.OrdinalIgnoreCase)
{
    ".DS_Store", "Thumbs.db", "desktop.ini", ".Spotlight-V100", ".Trashes", ".fseventsd", ".localized"
};
```

### 2. 属性转发 — FileListViewModel

`FileListViewModel` 作为协调层，将 `SortFilterViewModel` 的属性暴露给 Razor 组件：

```csharp
public bool HideDotFiles
{
    get => _sortFilter.HideDotFiles;
    set => _sortFilter.HideDotFiles = value;
}
public bool HideDotFolders
{
    get => _sortFilter.HideDotFolders;
    set => _sortFilter.HideDotFolders = value;
}
```

当隐藏设置变更时自动重新过滤文件列表：

```csharp
private void OnSortFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
{
    // 转发属性变更
    if (e.PropertyName is nameof(SortFilterViewModel.HideSystemFiles)
        or nameof(SortFilterViewModel.HideDotFiles)
        or nameof(SortFilterViewModel.HideDotFolders)
        /* ... 其他属性 */)
    {
        OnPropertyChanged(e.PropertyName);
    }

    // 重新应用过滤
    if (e.PropertyName is nameof(SortFilterViewModel.HideSystemFiles)
        or nameof(SortFilterViewModel.HideDotFiles)
        or nameof(SortFilterViewModel.HideDotFolders))
    {
        _sortFilter.ApplySortAndGroup(
            sortedEntries => Entries = sortedEntries,
            msg => StatusText = msg
        );
    }
}
```

### 3. 侧边栏可见性通知 — 虚拟事件模式

侧边栏可见性设置不走 ViewModel 属性，而是通过 `ISettingsService` 直接读写。变更通知使用轻量级虚拟 `PropertyChanged` 事件：

```
SettingsDialog                 FileListViewModel            FinderSidebar
     │                              │                           │
     │  OnSidebarToggle()           │                           │
     │  ├─ 更新本地字段              │                           │
     │  ├─ SettingsService.Set()    │                           │
     │  └─ ViewModel.Notify───────→ │                           │
     │     SidebarVisibilityChanged │  OnPropertyChanged ──────→│
     │                              │  ("SidebarVisibilityChanged")
     │                              │                           │
     │                              │                  ReloadSidebarVisibility()
     │                              │                  ├─ 从 SettingsService
     │                              │                  │  重读所有 14 项设置
     │                              │                  └─ StateHasChanged()
```

**SettingsDialog 端** — 通用开关处理器：

```csharp
private void OnSidebarToggle(ChangeEventArgs e, string key, Action<bool> setter)
{
    var value = (bool)(e.Value ?? false);
    setter(value);                              // 更新本地状态
    SettingsService.Set(key, value);            // 持久化
    ViewModel.NotifySidebarVisibilityChanged(); // 通知侧边栏
}
```

**FileListViewModel 端** — 发出通知：

```csharp
public void NotifySidebarVisibilityChanged() => OnPropertyChanged("SidebarVisibilityChanged");
```

**FinderSidebar 端** — 监听并重载：

```csharp
private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
{
    // ...
    else if (e.PropertyName == "SidebarVisibilityChanged")
    {
        ReloadSidebarVisibility();
        await InvokeAsync(StateHasChanged);
    }
}

private void ReloadSidebarVisibility()
{
    _showUsername = SettingsService.Get("sidebar_show_username", true);
    _showDesktop = SettingsService.Get("sidebar_show_desktop", true);
    // ... 共 14 项
}
```

### 4. 侧边栏条件渲染

每个固定侧边栏项使用 `@if` 守卫控制渲染：

```html
@if (_showUsername)
{
<div class="sidebar-item @(ViewModel.CurrentPath == ViewModel.HomeDirectory ? "active" : "")"
     @onclick="@(() => NavigateTo(ViewModel.HomeDirectory))">
    <svg ...>...</svg>
    <span class="sidebar-item-name">@_userName</span>
</div>
}
```

用户名从系统路径动态获取：

```csharp
_userName = Path.GetFileName(ViewModel.HomeDirectory);
```

### 5. 设置对话框 Tab 布局

Tab 通过 `_activeTab` 字符串字段切换，四个面板使用 `@if` 条件渲染：

```html
<div class="settings-tabs">
    <div class="settings-tab @(_activeTab == "general" ? "active" : "")"
         @onclick="@(() => _activeTab = "general")">通用</div>
    <div class="settings-tab @(_activeTab == "files" ? "active" : "")"
         @onclick="@(() => _activeTab = "files")">文件显示</div>
    <div class="settings-tab @(_activeTab == "sidebar" ? "active" : "")"
         @onclick="@(() => _activeTab = "sidebar")">侧边栏</div>
    <div class="settings-tab @(_activeTab == "appearance" ? "active" : "")"
         @onclick="@(() => _activeTab = "appearance")">外观</div>
</div>

<div class="settings-tab-content">
    @if (_activeTab == "general") { ... }
    else if (_activeTab == "files") { ... }
    else if (_activeTab == "sidebar") { ... }
    else if (_activeTab == "appearance") { ... }
</div>
```

## CSS 样式

Tab 和侧边栏设置项的样式类：

```css
/* Tab 栏 */
.settings-tabs          /* flex 容器，底部边框 */
.settings-tab           /* 单个 tab，hover/active 状态 */
.settings-tab.active    /* 激活态：accent 颜色 + 下划线 */
.settings-tab-content   /* 内容区，max-height: 50vh + overflow-y: auto */

/* 侧边栏设置分组 */
.settings-group-title       /* 分组标题：11px、大写、字间距 */
.settings-item-compact      /* 紧凑开关项：flex、6px 内边距 */
.settings-item-compact:hover /* 悬停背景 */

/* 外观 Tab — 滑块 */
.settings-slider-group  /* flex 容器，包含滑块 + 数值 */
.settings-slider        /* range input，120px 宽，accent 色滑块圆点 */
.settings-slider:disabled /* 禁用态，opacity: 0.35 */
.settings-slider-value  /* 百分比文字，12px，tabular-nums */

/* 重启提示 */
.settings-restart-hint  /* 11px，居中，tertiary 文字色 */
```

## 新增设置项指南

### 添加文件过滤设置

1. **`SortFilterViewModel.cs`** — 添加 `[ObservableProperty]` 字段和持久化回调：
   ```csharp
   [ObservableProperty]
   private bool _hideNewFilter = false;
   
   partial void OnHideNewFilterChanged(bool value) => _settingsService?.Set("HideNewFilter", value);
   ```

2. **`SortFilterViewModel.cs` 构造函数** — 读取初始值：
   ```csharp
   HideNewFilter = _settingsService.Get<bool>("HideNewFilter", false);
   ```

3. **`SortFilterViewModel.cs` `ApplySortAndGroup`** — 添加过滤条件：
   ```csharp
   if (HideNewFilter)
       filtered = filtered.Where(e => /* 过滤条件 */);
   ```

4. **`FileListViewModel.cs`** — 添加转发属性，并在 `OnSortFilterPropertyChanged` 中添加属性名。

5. **`SettingsDialog.razor`** — 在"文件显示"Tab 中添加 UI 开关。

### 添加侧边栏可见性设置

1. **`FinderSidebar.razor`** — 添加 `_showXxx` 字段、在 `ReloadSidebarVisibility()` 中读取、在 HTML 中添加 `@if` 守卫。

2. **`SettingsDialog.razor`** — 添加 `_showXxx` 字段、在 `OnInitialized` 中读取、在"侧边栏"Tab 对应分组中添加开关。

### Razor 中 Lambda 注意事项

当 `@onchange` 中的 C# lambda 含有双引号字符串时，必须用 `@(...)` 包裹以避免 Razor 解析器将内部双引号误识别为属性结束：

```html
<!-- 正确 -->
@onchange="@(e => OnSidebarToggle(e, "sidebar_show_xxx", v => _showXxx = v))"

<!-- 错误 — 编译报错 CS1525 -->
@onchange="e => OnSidebarToggle(e, "sidebar_show_xxx", v => _showXxx = v)"
```

### 添加外观/重启生效类设置

对于需要重启才能生效的原生层配置：

1. **`VibrancyHelper.cs`**（或相应原生 Helper）— 添加 `public static` 属性，`ConfigureNSWindow` 中读取。

2. **`App.xaml.cs`** — 在构造函数中从 `ISettingsService` 读取值并赋给静态属性（在 `Register()` 之前）。

3. **`SettingsDialog.razor`** — 在"外观"Tab 中添加 UI 控件（开关/滑块），处理器中调用 `SettingsService.Set()` 持久化。无需通知 ViewModel，因为是重启生效。

4. 底部添加 `<div class="settings-restart-hint">修改后需重启应用生效</div>` 提示。
