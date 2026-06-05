# 多页签（Multi-Tab）功能设计方案

> **日期**：2026-06-05
> **状态**：方案对比阶段
> **上下文**：Mac Explorer (FKFinder) — .NET 10 MAUI Blazor Hybrid macOS 应用

---

## 一、当前架构分析

### 1.1 运行时模型

```
┌── .NET 进程 ──────────────────────────────────────┐
│  Singleton Services（文件、索引、剪贴板、搜索...）    │
│  ├─ IServiceScope (Window A)                       │
│  │  └─ FileListViewModel (Scoped, 2398 行协调器)    │
│  │     ├─ NavigationViewModel (单个历史栈)           │
│  │     ├─ FileOpsViewModel                         │
│  │     ├─ SearchViewModel                          │
│  │     └─ ...                                      │
│  │         ↕ JS Interop                            │
│  │  ┌── WKWebView ────────────────────────────┐    │
│  │  │  Blazor 组件树（Home.razor 渲染一切）     │    │
│  │  └──────────────────────────────────────────┘    │
│  ├─ IServiceScope (Window B)                       │
│  │  └─ ... (另一个独立的 FileListViewModel 树)       │
└─────────────────────────────────────────────────────┘
```

- 单页面 Blazor Hybrid 应用，只有 `@page "/"` 一个路由
- `FileListViewModel` 是 Facade 协调器（2398 行），管理 7 个子 ViewModel
- `NavigationViewModel` 仅维护单个 `_historyStack`，无法同时浏览多个目录
- 已支持 UIScene 多窗口，通过 `RequestSceneSessionActivation` 创建
- 大量使用 AppKit P/Invoke（`VibrancyHelper`、`DropOverlayHelper`、`ContextMenuHelper` 等）

### 1.2 关键文件和依赖注入

| 文件 | 作用 |
|------|------|
| `MauiProgram.cs:192-230` | DI 注册：Singleton 服务 + Scoped ViewModel |
| `MainPage.xaml` | 单一 ContentPage，内含单个 BlazorWebView |
| `App.xaml.cs` | `CreateWindow()` 创建新窗口 |
| `SceneDelegate.cs` | 处理 macOS open-folder 事件 |
| `Home.razor` | 整应用入口，注入单个 `FileListViewModel` |
| `NavigationBridge.cs` | Singleton，跨窗口导航协调 |

---

## 二、需求摘要

| 需求类别 | 具体功能 |
|----------|---------|
| **页签栏位置** | 导航栏上方（Safari/Finder 风格） |
| **会话持久化** | 重启后恢复所有页签和历史 |
| **基础交互** | Cmd+T 新建、Cmd+W 关闭、点击切换、Cmd+Shift+[ ] 切换 |
| **进阶交互** | 拖拽排序、拖出页签创建新窗口、右键菜单（关闭其他、关闭右侧、复制页签） |
| **高级交互** | 固定页签、拖入文件到页签触发导航 |

---

## 三、方案详细分析

### 3.1 方案 A：状态快照模型（轻量重构，不推荐）

#### 思路

保持单个 `FileListViewModel` 实例，新增 `TabViewModel` 管理 `List<TabSession>`。切换页签时序列化当前状态 → 加载目标状态 → 重新读取目录。

```csharp
public class TabSession
{
    public Guid TabId { get; init; }
    public string Title { get; set; }
    public string Path { get; set; }
    public bool IsPinned { get; set; }

    // 导航历史
    public List<string> HistoryStack { get; init; } = [];
    public int HistoryIndex { get; set; }

    // 选中项
    public HashSet<string> SelectedPaths { get; init; } = [];

    // 排序/视图偏好
    public SortField SortField { get; set; }
    public bool SortAscending { get; set; }
    public ViewMode ViewMode { get; set; }

    // 特殊视图状态
    public bool IsArchiveView { get; set; }
    public string? ArchivePath { get; set; }
    public int? CollectionId { get; set; }
    public int? FaceClusterId { get; set; }
}
```

#### 优缺点

| 优点 | 缺点 |
|------|------|
| 改动量最小 | 切换页签必须重新加载目录 → 闪烁 |
| 不改变 DI 结构 | 异步运行时状态（缩略图、git 状态）无法完整序列化 |
| 快速落地 | 滚动位置恢复不可靠（JS 端状态丢失） |
| | `FileListViewModel` 会更臃肿 |

**结论：不推荐。** 状态序列化的边界模糊且不可靠，用户体验差。

---

### 3.2 方案 B：IServiceScope 隔离（推荐，纯 Blazor 实现）

#### 思路

利用 MAUI 已有的 Scoped 服务机制，通过 `IServiceScopeFactory` 为每个页签创建独立的 ServiceScope，每个页签拥有完全隔离的 ViewModel 实例树。所有页签共享一个 BlazorWebView，切换页签仅切换 Blazor 渲染目标。

#### 架构图

```
┌── NSWindow (MAUI Window) ─────────────────────────────────────────┐
│                                                                    │
│  ┌── BlazorWebView (唯一) ───────────────────────────────────────┐ │
│  │  <FinderTabBar />                   ← 页签栏 (Blazor 渲染)     │ │
│  │  <TabContentContainer>              ← 根据 ActiveTab 切换内容   │ │
│  │    @key="_activeTab.Id"                                         │ │
│  │    <CascadingValue Value="_activeTab.Scope">                    │ │
│  │      <FinderNavBar />                                           │ │
│  │      <FinderToolbar />                                          │ │
│  │      <FinderSidebar />                                          │ │
│  │      <FileGridView />               ← 从 Scope 解析 VM          │ │
│  │    </CascadingValue>                                            │ │
│  │  </TabContentContainer>                                         │ │
│  └────────────────────────────────────────────────────────────────┘ │
│                                                                    │
│  ┌── .NET 端 ────────────────────────────────────────────────────┐ │
│  │  TabViewModel (Scoped, 每窗口一个)                             │ │
│  │  ├─ ObservableCollection<TabItem> Tabs                         │ │
│  │  └─ TabItem? ActiveTab                                         │ │
│  │                                                                │ │
│  │  TabItem A ── IServiceScope ── FileListViewModel              │ │
│  │             ├─ NavigationViewModel (独立历史栈)                 │ │
│  │             ├─ FileOpsViewModel                                │ │
│  │             ├─ SortFilterViewModel                             │ │
│  │             └─ ...                                             │ │
│  │                                                                │ │
│  │  TabItem B ── IServiceScope ── FileListViewModel              │ │
│  │             └─ (完全独立的状态树)                               │ │
│  │                                                                │ │
│  │  TabItem C ── IServiceScope ── FileListViewModel              │ │
│  │             └─ (完全独立的状态树)                               │ │
│  └────────────────────────────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────────┘
```

#### 核心代码示例

```csharp
// TabItem.cs - 页签数据模型
public class TabItem : IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; set; } = "新建页签";
    public bool IsPinned { get; set; }
    public IServiceScope Scope { get; init; }
    public FileListViewModel ViewModel { get; init; }

    // 会话持久化所需的关键数据
    public string? CurrentPath => ViewModel.CurrentPath;

    public void Dispose()
    {
        ViewModel?.Dispose();
        Scope?.Dispose();
    }
}

// TabViewModel.cs - 页签管理器（Scoped，每窗口一个）
public partial class TabViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NavigationBridge _navigationBridge;
    private readonly IDragDropBridge _dragDropBridge;
    private readonly IDirectoryChangeNotifier _directoryChangeNotifier;

    [ObservableProperty]
    private ObservableCollection<TabItem> _tabs = [];

    [ObservableProperty]
    private TabItem? _activeTab;

    public TabItem CreateTab(string? initialPath = null)
    {
        var scope = _scopeFactory.CreateScope();
        var vm = scope.ServiceProvider.GetRequiredService<FileListViewModel>();

        // 注册到跨窗口桥接
        _navigationBridge.Register(vm);
        _dragDropBridge.Register(vm);
        _directoryChangeNotifier.Subscribe(vm);

        var tab = new TabItem { Scope = scope, ViewModel = vm };
        Tabs.Add(tab);
        ActiveTab = tab;

        if (initialPath != null)
            _ = vm.NavigateToCommand.ExecuteAsync(initialPath);

        return tab;
    }

    public void CloseTab(TabItem tab)
    {
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        // 如果关闭的是当前活动页签，切换到相邻页签
        if (ActiveTab == tab)
            ActiveTab = Tabs.ElementAtOrDefault(Math.Min(index, Tabs.Count - 1));

        // 注销桥接
        _navigationBridge.Unregister(tab.ViewModel);
        _dragDropBridge.Unregister(tab.ViewModel);
        _directoryChangeNotifier.Unsubscribe(tab.ViewModel);
        tab.Dispose();
    }
}
```

#### Home.razor 改动

```razor
@page "/"
@inject TabViewModel TabViewModel

<MApp>
    <FinderTabBar Tabs="TabViewModel.Tabs"
                  ActiveTab="TabViewModel.ActiveTab"
                  OnTabSelected="tab => TabViewModel.ActiveTab = tab"
                  OnTabClosed="tab => TabViewModel.CloseTab(tab)"
                  OnNewTab="() => TabViewModel.CreateTab()"
                  OnTabReordered="HandleTabReorder"
                  OnTabDraggedOut="HandleTabTearOff" />

    @if (TabViewModel.ActiveTab is { } activeTab)
    {
        <CascadingValue Value="activeTab.ViewModel">
            <CascadingValue Value="activeTab.Scope">
                <!-- 使用 @key 保证切换页签时 Blazor 组件树正确更新 -->
                <TabContentContainer @key="activeTab.Id">
                    <!-- 原有 Finder 布局组件 -->
                    <FinderNavBar />
                    <FinderToolbar />
                    <div class="finder-body">
                        <FinderSidebar />
                        <div class="finder-content-split">
                            <!-- 文件列表 -->
                        </div>
                    </div>
                </TabContentContainer>
            </CascadingValue>
        </CascadingValue>
    }
</MApp>
```

#### 优缺点

| 优点 | 缺点 |
|------|------|
| 切换页签零延迟（VM 一直在内存） | 需修改 DI 注册（ViewModel 改为 Singleton 或其他方案） |
| Blazor 渲染页签栏，完全自定义 | N 个活跃页签 = N 份 ViewModel 树（~2-5MB/个） |
| 共享 WebView，内存增量可控 | 崩溃隔离较弱（共享一个 WebView） |
| Tear-off 可实现（在新窗口创建 Scope） | 需要处理桥接服务的多实例注册 |
| 渐进式交付（先基础页签，后进阶交互） | |

#### 会话持久化实现

```csharp
// 重启时恢复页签
public class SessionRestoreService
{
    private readonly ISettingsService _settings;

    public record TabRestoreInfo(string Path, string Title, bool IsPinned);

    public async Task SaveSessionAsync(IEnumerable<TabItem> tabs)
    {
        var infos = tabs.Select(t => new TabRestoreInfo(
            t.ViewModel.CurrentPath, t.Title, t.IsPinned));
        await _settings.SetAsync("session_tabs", JsonSerializer.Serialize(infos));
    }

    public Task<List<TabRestoreInfo>> LoadSessionAsync()
    {
        var json = _settings.Get<string>("session_tabs");
        var infos = string.IsNullOrEmpty(json)
            ? [new TabRestoreInfo(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "个人", false)]
            : JsonSerializer.Deserialize<List<TabRestoreInfo>>(json);
        return Task.FromResult(infos);
    }
}
```

---

### 3.3 方案 D：多 BlazorWebView（用户提议的方案）

#### 思路

每个页签拥有独立的 `BlazorWebView` 实例。所有 WebView 共存于同一个 MAUI 窗口中，通过 show/hide 原生视图进行切换。每个 WebView 内部是完全独立的 Blazor 应用实例。

#### 架构图

```
┌── NSWindow (MAUI Window) ───────────────────────────────────────────┐
│                                                                      │
│  ┌── 页签栏区域 ──────────────────────────────────────────────────┐  │
│  │  (Blazor 渲染 或 原生 NSSegmentedControl/NSTabView)             │  │
│  │  [📁 项目文件 ×] [📁 下载 ×] [📁 图片 ×] [+]                   │  │
│  └────────────────────────────────────────────────────────────────┘  │
│                                                                      │
│  ┌── 页签内容区（容器 Grid，每个 WebView 填满且互斥显示）─────────┐  │
│  │                                                                │  │
│  │  ┌── BlazorWebView A (IsVisible=true) ────────────────────┐   │  │
│  │  │  完整 Blazor 组件树                                     │   │  │
│  │  │  Router → Home → FinderLayout → FileGridView           │   │  │
│  │  │  自己的 FileListViewModel (Scope A)                    │   │  │
│  │  └────────────────────────────────────────────────────────┘   │  │
│  │                                                                │  │
│  │  ┌── BlazorWebView B (IsVisible=false) ───────────────────┐   │  │
│  │  │  完整 Blazor 组件树（已在内存中，DOM 完整保留）          │   │  │
│  │  │  自己的 FileListViewModel (Scope B)                    │   │  │
│  │  └────────────────────────────────────────────────────────┘   │  │
│  │                                                                │  │
│  │  ┌── BlazorWebView C (IsVisible=false) ───────────────────┐   │  │
│  │  │  ...                                                    │   │  │
│  │  └────────────────────────────────────────────────────────┘   │  │
│  └────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────┘
```

#### 核心代码示例

```csharp
// MultiTabPage.xaml.cs - 动态管理多个 BlazorWebView
public partial class MultiTabPage : ContentPage
{
    private readonly List<TabViewItem> _tabs = [];
    private TabViewItem? _activeTab;
    private readonly Grid _contentGrid; // 承载所有 WebView 的容器

    public class TabViewItem : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Title { get; set; } = "新建页签";
        public bool IsPinned { get; set; }
        public IServiceScope Scope { get; init; }
        public FileListViewModel ViewModel { get; init; }
        public BlazorWebView WebView { get; init; }

        public void Dispose()
        {
            ViewModel?.Dispose();
            Scope?.Dispose();
        }
    }

    public TabViewItem CreateTab(string? initialPath = null)
    {
        var scope = App.ServiceProvider.CreateScope(); // 需要暴露全局 ServiceProvider
        var vm = scope.ServiceProvider.GetRequiredService<FileListViewModel>();

        // 动态创建新的 BlazorWebView
        var webView = new BlazorWebView
        {
            HostPage = "wwwroot/index.html",
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            IsVisible = false,
        };

        // 设置 RootComponent
        webView.RootComponents.Add(new RootComponent
        {
            Selector = "#app",
            ComponentType = typeof(Routes)
        });

        _contentGrid.Children.Add(webView);
        var tab = new TabViewItem { Scope = scope, ViewModel = vm, WebView = webView };
        _tabs.Add(tab);

        // 新页签需要等待 WebView 初始化完成
        // ...

        return tab;
    }

    public void SwitchToTab(TabViewItem tab)
    {
        if (_activeTab != null)
            _activeTab.WebView.IsVisible = false;

        tab.WebView.IsVisible = true;
        _activeTab = tab;
        UpdateTitleBar(tab);
    }
}
```

#### 页签栏实现选择

| 选项 | 方式 | 优点 | 缺点 |
|------|------|------|------|
| Blazor 渲染 | 保留顶部 BlazorWebView 渲染页签栏 | 完全定制、统一技术栈 | 页签栏与内容区分离略不自然 |
| 原生 NSSegmentedControl | P/Invoke AppKit | 原生 macOS 观感 | 定制能力有限 |
| 自绘页签栏 View | 自定义 NSView（参考现有 P/Invoke 模式） | 完全控制 + 原生性能 | 实现量大 |

#### 预加载优化（解决新建页签延迟）

```csharp
// WebViewPool.cs - 预创建闲置 WebView，新页签时直接取用
public class WebViewPool
{
    private readonly Queue<BlazorWebView> _idlePool = new();
    private readonly int _poolSize = 2;

    public BlazorWebView? Acquire()
    {
        if (_idlePool.TryDequeue(out var webView))
            return webView; // 已初始化，立即可用
        return null; // 需要新建
    }

    public void Prewarm()
    {
        for (int i = 0; i < _poolSize; i++)
        {
            var webView = CreateAndInitializeWebView();
            _idlePool.Enqueue(webView);
        }
    }
}
```

#### 优缺点

| 优点 | 缺点 |
|------|------|
| 状态完全隔离，无交叉污染 | 新建页签有 1-3 秒延迟（可用预加载改善） |
| 切换页签零延迟（show/hide） | 需要修改 `MainPage.xaml` 为多 WebView 容器 |
| 崩溃隔离（一个 WebView 不拖累其他） | 需要将全局 IServiceProvider 暴露给 View 层 |
| Tear-off 更接近原生（移动 NSView） | 页签栏需要跨 WebView 或原生实现 |
| 滚动、缩略图、异步状态天然保持 | 每 WebView 内存约 20-40MB（可接受） |
| 与现有架构更兼容（每个 WebView = 一个完整的 Blazor 应用） | 多个 Blazor 实例间通信需要通过 .NET 端中转 |

---

### 3.4 方案 E：AppKit 原生 Tabbing（窗口级页签）

#### 思路

macOS 自带的窗口页签机制：设置 `NSWindow.tabbingMode = .preferred`，AppKit 自动将同一 `tabbingIdentifier` 的多个窗口合并显示为页签。`Cmd+T` 创建新窗口自动合并；拖拽页签分离 → 自动变独立窗口。

```objc
// 伪代码：通过 P/Invoke 设置 NSWindow tabbing
[NSWindow setAllowsAutomaticWindowTabbing: YES];
window.tabbingMode = NSWindowTabbingModePreferred;
window.tabbingIdentifier = @"com.macexplorer.finder-tabs";
```

#### 工作原理

```
初始状态:                          Cmd+T 后:
┌── Window A ──────┐              ┌── Window (A + B 合并) ────┐
│  项目文件         │              │  [项目文件] [~下载] [+]   │
│                  │              │                          │
│  ← → ↑ /Users/   │              │  ← → ↑ /Users/Downloads  │
│  📄 file1.txt    │              │  📄 wallpaper.png        │
└──────────────────┘              └──────────────────────────┘

拖出页签分离:
┌── Window A ──────┐  ┌── Window B ──────┐
│  项目文件         │  │  ~下载            │
│  内容...          │  │  内容...          │
└──────────────────┘  └──────────────────┘
```

#### MAUI 环境下的技术挑战

当前架构是 `UIScene` 管理窗口：每个 Scene 通过 `CreateWindow()` 创建新的 MAUI Window → 内含新的 `MainPage` → 内含新的 `BlazorWebView`。

`NSWindow.tabbingMode` 与 UIScene 体系存在 **潜在冲突**：

1. **AppKit 期望的窗口管理模型**：`[NSWindow addTabbedWindow:ordered:]` — 将一个窗口变为另一个窗口的页签
2. **UIScene 模型**：每个 Scene 独立，Scene Session 有独立生命周期
3. **问题**：将 Scene B 的 NSWindow 合并到 Scene A 后，Scene B 的 UISceneSession 如何处理？AppKit 不会自动销毁 UISceneSession

**可能的解决路径**：

```csharp
// 在 App.xaml.cs 或 SceneDelegate 中，获取到 NSWindow 后尝试设置 tabbing
#if MACCATALYST
// 通过 P/Invoke 获取 NSWindow 实例
var nsWindow = GetNSWindow(window); // 已有类似模式（VibrancyHelper）
// 设置 tabbing mode
objc_msgSend(nsWindow, Selector.GetHandle("setTabbingMode:"), 2); // NSWindowTabbingModePreferred
objc_msgSend(nsWindow, Selector.GetHandle("setTabbingIdentifier:"),
    NSString.FromData("com.macexplorer.tabs"));
#endif
```

但这需要深入验证 MAUI Mac Catalyst 环境下 `NSWindow.tabbingMode` 的实际行为，包括：
- `Cmd+T` 是否会触发正确的窗口创建流程
- UISceneSession 在页签合并/分离时是否会产生异常
- 关闭最后一个页签后 Scene 是否正确销毁

#### 优缺点

| 优点 | 缺点 |
|------|------|
| 完全原生 macOS 页签体验 | 与 UIScene 体系兼容性未验证 |
| 零 UI 实现（AppKit 提供完整交互） | 页签栏不可定制（无右键菜单自定义等） |
| 拖拽合并/分离开箱即用 | 固定页签等高级功能受限 |
| Cmd+T / Cmd+W 等快捷键免费获得 | 每个页签是新 WebView → 同方案 D 的性能特征 |
| macOS 用户最熟悉的交互 | 无法在页签栏添加自定义 UI 元素 |

---

## 四、方案对比总表

| 维度 | 方案 A (快照) | 方案 B (Scope 隔离) | 方案 D (多 WebView) | 方案 E (AppKit Tab) |
|------|:---:|:---:|:---:|:---:|
| **切换页签延迟** | ❌ 重新加载目录 | ✅ 零延迟 | ✅ 零延迟 | ✅ 零延迟 |
| **新建页签速度** | ✅ 瞬时 | ✅ 瞬时 | ⚠️ 1-3s | ⚠️ 1-3s |
| **崩溃隔离** | ❌ | ❌ | ✅ | ✅ |
| **页签栏定制** | ✅ 完全自由 | ✅ 完全自由 | ⚠️ 需要跨 WebView | ❌ 受限于 AppKit |
| **Tear-off 实现** | ❌ 不支持 | ⚠️ 需 Scope 迁移 | ✅ 接近原生 | ✅ 开箱即用 |
| **固定页签** | ✅ | ✅ | ✅ | ❌ 受限 |
| **右键菜单** | ✅ | ✅ | ⚠️ 需原生 | ❌ 不可定制 |
| **实现复杂度** | 低 | 中 | 高 | 中高 |
| **风险** | 状态破损 | 低 | 中 | 未验证 UIScene 兼容 |
| **内存（5 页签）** | ~5MB 增量 | ~25MB 增量 | ~100-200MB 增量 | ~100-200MB 增量 |
| **与会话恢复兼容** | ✅ 自然 | ✅ 需实现 | ✅ 需实现 | ⚠️ AppKit 控制 |

---

## 五、推荐策略

### 短期：方案 B（IServiceScope 隔离）

- 风险最低，利用已有 Scoped 机制
- 快速交付基础页签功能
- 页签栏 UI 完全由 Blazor 控制
- 为 tear-off 预留接口（后续通过创建新窗口 + 迁移 Scope 实现）

### 中期：验证方案 E（尝试 AppKit Tabbing）

- 在静态 P/Invoke 代码块中测试 `NSWindow.tabbingMode` 与 UIScene 的兼容性
- 若兼容 → 可作为 tear-off 的捷径（页签拖出 = AppKit 自动分离窗口）

### 长期：考虑方案 D（若需求演进）

- 如果用户反馈崩溃隔离不足，或需要更彻底的页签独立性
- 可在方案 B 的基础上逐步引入多 WebView
- 方案 B 的 ViewModel 层设计可复用

### 不推荐：方案 A

- 状态序列化不可靠，用户体验差

---

## 六、实施路线建议

### 阶段一：核心页签（MVP）
- [ ] `TabViewModel` + `TabItem` 数据模型
- [ ] 修改 DI：提供 `IServiceScopeFactory` 创建页签 Scope
- [ ] `FinderTabBar.razor` 页签栏组件（Safari 风格）
- [ ] `Home.razor` 集成 TabViewModel
- [ ] 快捷键：Cmd+T 新建、Cmd+W 关闭、Cmd+Shift+[ ] 切换

### 阶段二：进阶交互
- [ ] 拖拽排序页签
- [ ] 页签右键菜单（关闭其他、关闭右侧、复制页签）
- [ ] 会话持久化与恢复

### 阶段三：高级功能
- [ ] 固定页签（Pin）
- [ ] Tear-off 页签到新窗口
- [ ] 拖入文件到页签触发导航
- [ ] 页签可用性指示器（如后台页签有文件变更时闪烁）

---

## 七、待决议题

1. **方案选择**：方案 B（单 WebView Scope 隔离）vs 方案 D（多 WebView）— 尚未最终决策
2. **页签栏可见性规则**：是否仿 Safari "至少 2 个页签才显示页签栏" 还是始终显示
3. **最大页签数**：是否设置上限（如 Safari 无上限 vs Chrome 约 20 个可见）
4. **关闭最后页签**：关闭最后一个页签时是关闭窗口还是回到首页
5. **是否需要验证方案 E（AppKit Tabbing）的可行性** — 可作为技术预研

---

> **下一步**：确认方案选择后，进入 `writing-plans` 阶段编写详细实施计划。
