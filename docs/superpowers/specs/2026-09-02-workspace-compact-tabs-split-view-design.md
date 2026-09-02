# 完整工作区与精简分屏设计

- 日期：2026-09-02
- 状态：待实现
- 适用项目：FKFinder / Mac Explorer

## 1. 背景

当前多标签和多窗格已经为每个标签创建独立的 `FileListViewModel` 与 DI Scope，因此每个标签能够独立维护路径、前进/后退历史、搜索结果、视图模式和选择状态。但是视图所有权仍然分成两层：

- `MainWindow` 持有唯一的左侧栏、主面包屑、搜索框、文件工具栏、状态栏和信息面板。
- `ExplorerPaneView` 只持有文件/Home/AI 内容；多窗格时额外显示一条小型导航和面包屑。
- 点击窗格后，`MainWindowViewModel.FileList` 切换到该窗格的 `FileListViewModel`，窗口级控件再整体作用于当前活动窗格。

这导致两个直接问题：

1. 多窗格同时出现窗口主面包屑和窗格面包屑，但每个窗格仍不是完整的文件管理页面。
2. 单窗格和多窗格分别维护窗口外壳与 `ExplorerPaneView`，后续增加侧栏、工具栏或预览交互时容易产生两套实现。

本设计将“标签页内容”提升为完整工作区，并让同一个工作区控件原生支持普通和精简两种布局。分屏只负责约束工作区尺寸和强制精简展示，不再使用分屏专用页面。

## 2. 目标与非目标

### 2.1 目标

- 每个标签对应一个完整的 `ExplorerWorkspaceView`，包含侧栏、导航、面包屑、页面搜索、文件工具栏、文件/Home/AI 内容、状态栏和信息面板。
- 单窗格和多窗格使用同一棵控件树、同一个 `FileListViewModel` 和同一组命令。
- 工作区按自身可用宽度切换普通/精简模式；任意多窗格布局直接使用精简模式。
- 每个工作区只显示一条面包屑，不再叠加分屏专用面包屑。
- 精简模式仍可访问全部功能，但允许通过图标栏、覆盖面板和溢出菜单节省空间。
- 每个标签保留独立预览状态，但同一窗口内只有活动窗格维持实时 Quick Look、WebKit 或 AVPlayer 宿主。
- 保留现有标签 DI Scope、导航历史、选择、排序、搜索、文件操作及关闭标签的资源释放行为。

### 2.2 非目标

- 不实现标签拖拽排序、标签跨窗口移动或拆分为新窗口。
- 不持久化标签集合、窗格布局或窗格与标签的分配关系。
- 不重写文件加载、搜索、压缩、SFTP、缩略图或预览渲染服务。
- 不改变全局快速搜索、任务中心、设置、模态对话框和超级预览的窗口级定位。
- 不为精简模式创建第二套 `CompactExplorerView`、`CompactToolbar` 或 `CompactInfoPanel`。

## 3. 术语和不变量

| 术语 | 含义 |
|---|---|
| 标签（Tab） | 一个 `ExplorerTabViewModel`，拥有独立 `FileListViewModel` 和 DI Scope |
| 工作区（Workspace） | 一个标签的完整可视页面，即 `ExplorerWorkspaceView` |
| 窗格槽位（Pane Slot） | 分屏布局中的一个位置，引用一个标签并显示其工作区 |
| 活动窗格 | 当前接收键盘、工具栏、搜索、预览和窗口级命令的窗格 |
| 普通模式 | 侧栏和信息面板可内联显示，工具栏展示完整操作 |
| 精简模式 | 侧栏使用图标窄栏/覆盖展开，工具栏使用核心按钮和溢出菜单，信息面板覆盖显示 |

实现必须保持以下不变量：

1. 一个标签始终只有一个 `FileListViewModel` 和一个 DI Scope。
2. 一个窗格槽位最多引用一个标签；同一个标签不同时占用两个槽位。
3. 同一窗口始终只有一个活动窗格，并且至多只有一个实时原生预览宿主。
4. 不可见标签不保留完整控件树；标签离开所有窗格后销毁其工作区视图，重新进入可见窗格时用原标签 ViewModel 创建新视图。
5. 普通/精简切换只改变展示方式，不重建 `FileListViewModel`，也不清除导航、选择、搜索或滚动恢复状态。

## 4. 当前实现

### 4.1 窗口级外壳

`Views/MainWindow.axaml` 当前包含：

- `SidebarHost` 与 `FinderSidebarView`
- 前进、后退、上级、刷新、主 `BreadcrumbBar` 和页面搜索框
- 标签栏和窗格布局选择器
- `FinderToolbar`
- `InfoDrawer`、`InfoPanelView` 和状态栏
- `PaneLayoutRoot`
- 任务中心、全局搜索、对话框和超级预览等窗口级覆盖层

`ResponsiveWindowLayout.Resolve(width)` 由 `MainWindow.Bounds.Width` 驱动，因此只知道整个窗口是否紧凑，不知道单个分屏窗格实际获得的空间。

### 4.2 标签与窗格

`MainWindowViewModel` 使用：

- `Tabs` 保存所有标签。
- `VisiblePanes` 保存当前窗格布局显示的标签。
- `SelectedTab` 表示活动标签，并把 `FileList` 切换到该标签的 `FileListViewModel`。
- `PaneLayout`/`PaneCount` 决定一至四个槽位。

`MainWindow.RebuildPaneLayout()` 为可见标签创建 `ExplorerPaneView`。`ExplorerPaneView` 在单窗格隐藏自己的页头，在多窗格显示分屏专用导航和面包屑，因此形成重复面包屑。

当用户从标签栏选择一个当前未显示的标签时，`EnsureSelectedTabVisible()` 会替换最后一个可见窗格。目标设计应改为替换当前活动窗格槽位。

### 4.3 预览

`InfoPanelView` 拥有预览 UI 和预览会话；`MainWindow` 当前拥有 `SplitView` 宽度、展开动画和响应式显示方式。Office/PDF 使用原生 Quick Look，HTML 使用 WebKit，视频使用 AVPlayer。原生宿主存在层叠和资源成本，不能在四个窗格中无条件同时保持活动。

## 5. 目标架构

```text
MainWindow
├── NativeTitleBar / Window Chrome
├── WorkspaceToolbarSurface
│   ├── TabStrip + AddTab + PaneLayoutPicker
│   └── 当前窗口级入口（任务中心、设置等）
├── PaneLayoutRoot
│   ├── ExplorerWorkspaceView(Tab A)
│   ├── ExplorerWorkspaceView(Tab B)
│   └── ...最多四个
└── Window Overlays
    ├── Task Center
    ├── Global Search
    ├── Dialog Host
    └── Super Preview

ExplorerWorkspaceView
├── Sidebar SplitView
│   └── FinderSidebarView
└── Main Area
    ├── Navigation + BreadcrumbBar + Page Search
    ├── FinderToolbar
    ├── Content: HomeView / AiView / FileListView
    ├── Info SplitView
    │   └── InfoPanelView
    └── Status Bar
```

### 5.1 所有权

| 能力 | 目标所有者 | 说明 |
|---|---|---|
| 标签集合、添加/关闭标签 | `MainWindow` / `MainWindowViewModel` | 标签栏保持窗口唯一 |
| 窗格布局和槽位分配 | `MainWindow` / `MainWindowViewModel` | 工作区不感知网格形状 |
| 导航、面包屑、页面搜索 | `ExplorerWorkspaceView` | 绑定该标签的 `FileListViewModel` |
| 侧栏和文件工具栏 | `ExplorerWorkspaceView` | 普通和精简模式共用控件 |
| 文件/Home/AI 内容 | `ExplorerWorkspaceView` | 取代 `ExplorerPaneView` 的内容职责 |
| 信息面板状态 | 标签的 `FileListViewModel` | 视图展示方式由工作区决定 |
| 原生预览宿主生命周期 | 窗口级 `LivePreviewCoordinator` | 活动工作区提供承载位置；协调器保证非活动工作区释放实时宿主 |
| 全局搜索、任务、设置、模态对话框 | `MainWindow` | 不随标签复制 |
| 超级预览 | `MainWindow` | 始终使用活动标签选择项 |

## 6. 工作区接口

### 6.1 `ExplorerWorkspaceView`

新增 `Views/ExplorerWorkspaceView.axaml` 和 `.axaml.cs`，根 `DataContext` 为 `ExplorerTabViewModel`，内部控件绑定 `FileList`。

工作区公开以下布局输入和窗口转发入口：

| 成员 | 类型 | 语义 |
|---|---|---|
| `ForceCompact` | `StyledProperty<bool>` | 多窗格槽位设置为 `true`；单窗格为 `false` |
| `IsCompact` | 只读 `DirectProperty<bool>` | `ForceCompact || Bounds.Width < 1180` 的最终结果 |
| `IsLivePreviewEnabled` | `StyledProperty<bool>` | 仅活动且可见的工作区为 `true` |
| `WorkspaceActivated` | `event Action<ExplorerTabViewModel>` | 根控件通过 Tunnel 指针事件或 `GotFocus` 通知窗格激活 |
| `FocusPathInput()` | 方法 | 供 `⌘L` 转发到当前工作区面包屑输入 |
| `ToggleSidebar()` | 方法 | 供仍由窗口捕获的侧栏快捷键切换活动工作区的精简 Overlay；普通模式保持 Inline 打开 |
| `TogglePageSearch()` | 方法 | 展开或收起活动工作区的页面内搜索，不触发全局搜索 |
| `ToggleToolbarMenu(ToolbarMenuKind kind)` | 方法 | 必须保留窗口入口时，打开活动工作区对应的视图/排序/更多菜单 |
| `ToggleInfoPanel()` | 方法 | 切换活动标签的 `IsInfoPanelVisible`，实时加载仍由预览协调器门控 |
| `CloseTransientUi(object? source)` | 方法 | 关闭当前工作区工具栏菜单、路径建议和文件上下文菜单 |
| `TryHandleFileShortcut(KeyEventArgs e)` | 方法 | 把文件快捷键转发给当前 `FileListView` |

`IsCompact` 改变时只设置工作区根伪类 `:compact` 并更新两个 `SplitView` 的显示方式，不替换 `Content` 或 `DataContext`。

### 6.2 响应式布局计算

将 `Controls/ResponsiveWindowLayout.cs` 的职责调整为工作区级计算；内部类型可同步改名为 `ResponsiveWorkspaceLayout`，避免继续暗示它只适用于窗口。

```csharp
internal const double CompactBreakpoint = 1180;

internal static ResponsiveWorkspaceLayout Resolve(
    double workspaceWidth,
    bool forceCompact)
{
    var compact = forceCompact || workspaceWidth < CompactBreakpoint;
    return compact
        ? ResponsiveWorkspaceLayout.Compact
        : ResponsiveWorkspaceLayout.Regular;
}
```

布局结果至少包含：

- `IsCompact`
- `SidebarDisplayMode`
- `SidebarOpenPaneLength`
- `SidebarCompactPaneLength`
- `InfoPanelDisplayMode`

固定规格：

本文所有宽度和高度均为 Avalonia 逻辑设备无关单位（DIP），不是物理像素。

| 参数 | 普通模式 | 精简模式 |
|---|---|---|
| 触发条件 | `!ForceCompact && width >= 1180` | `ForceCompact || width < 1180` |
| 侧栏模式 | `Inline` | `CompactOverlay` |
| 侧栏展开宽度 | 260 | 220 |
| 侧栏紧凑宽度 | 0 | 48 |
| 信息面板模式 | `Inline` | `Overlay` |

任何 `PaneCount > 1` 的布局都必须给每个可见工作区设置 `ForceCompact=true`，包括上下分屏。这样上下分屏即使宽度充足，也与其他多窗格布局保持相同的精简交互。

## 7. 普通与精简交互

### 7.1 左侧栏

使用一个 `SplitView` 和一个 `FinderSidebarView`：

- 普通模式：`DisplayMode=Inline`、`IsPaneOpen=true`、宽度 260。
- 精简模式：`DisplayMode=CompactOverlay`、`CompactPaneLength=48`、`OpenPaneLength=220`。
- 48 DIP 窄栏仅显示主导航图标和当前选中状态；隐藏分组标题、文字标签、辅助信息和编辑入口。
- 点击窄栏导航图标直接导航；点击侧栏展开按钮后，以 220 DIP Overlay 展示完整的同一个 `FinderSidebarView`。
- `FinderSidebarView` 增加展示状态，但不增加第二套控件：仅在“精简且 Pane 关闭”时设置 `:rail`，打开 Overlay 后移除 `:rail`，恢复完整标签、分组和编辑入口。
- 点击工作区文件区域或按 Escape 关闭覆盖侧栏；关闭动作不改变当前目录。
- 侧栏覆盖层不得改变文件列表的 `Bounds`、滚动位置或列宽。

模式转换必须显式更新 `IsPaneOpen`，不能只修改 `DisplayMode`：

| 转换 | 侧栏结果 |
|---|---|
| 初次进入普通模式 | `IsPaneOpen=true`，260 DIP Inline |
| 普通 → 精简 | 先关闭 Pane，再切换 `CompactOverlay`，最终只保留 48 DIP 图标栏 |
| 精简模式中手动展开 | `IsPaneOpen=true`，220 DIP 覆盖显示；文件列表不重新测量 |
| 精简 → 普通 | 关闭精简 Overlay，切换 `Inline` 后设置 `IsPaneOpen=true` |
| 工作区失活或离开槽位 | 关闭精简 Overlay；重新激活时保持 48 DIP 收起状态 |

精简模式的 Overlay 展开状态是瞬态 UI 状态，不跨模式、不跨标签可见周期持久化。当前 `_isSidebarCollapsed` 只是旧窗口精简布局的运行时状态，不是应用级设置；迁移后删除该字段，由每个工作区的 `SplitView.IsPaneOpen` 取代。现有 `sidebar_show_*` 设置仅控制侧栏条目是否显示，继续在普通与精简模式的同一个 `FinderSidebarView` 上生效，不控制整个 Pane 的开闭。

当前 `SettingsButton` 不复制到每个工作区。设置入口移动到窗口级工具栏的溢出菜单，并继续由 `MainWindow.OpenSettings()` 处理。

### 7.2 导航、面包屑和搜索

- 每个工作区只创建一个 `BreadcrumbBar`；删除 `ExplorerPaneView.PaneHeader` 中的第二个面包屑。
- 普通模式保留前进、后退、上级、刷新、面包屑和页面搜索框。
- 精简模式保留导航图标；面包屑占用剩余宽度并延续现有横向滚动/路径编辑行为。
- 精简模式的页面搜索默认显示搜索图标；激活后在当前工作区内展开输入，不调用窗口级全局搜索。
- `⌘L`、页面搜索 Enter/Escape 和路径建议均只作用于活动工作区。
- 原生标题栏只承担窗口拖动和窗口标题，不再持有活动页面唯一面包屑。

### 7.3 文件工具栏

`FinderToolbar` 仍然只有一个控件类型，普通和精简模式绑定同一 `FileListViewModel`。

普通模式保持当前主要布局。精简模式的主栏固定保留：

1. 新建
2. 图标/列表视图切换
3. 排序与分组
4. 信息面板
5. 更多

精简模式将以下低频或有标准快捷键的操作放入“更多”：

- 剪切、复制、粘贴、删除
- 返回首页
- 批量重命名
- 连接远程服务器

普通按钮和溢出菜单项必须调用同一命令源或同一处理方法，不能各自复制文件操作逻辑。命令的 `CanExecute`、选中状态和提示文本都从同一状态计算。

### 7.4 信息面板和预览

每个工作区包含同一个 `InfoPanelView` 类型：

- 普通模式使用右侧 `Inline`，保持现有可调整宽度行为。
- 精简模式使用当前工作区范围内的 `Overlay`，打开时不压缩文件列表。
- 信息面板打开状态继续属于对应标签的 `FileListViewModel`。
- 只有 `IsLivePreviewEnabled=true` 的工作区可以开始或维持 Quick Look、WebKit、AVPlayer 和大文本预览会话。
- 工作区失活、离开可见槽位或关闭标签时，立即取消排队/进行中的预览加载，关闭原生宿主并释放临时预览资源；不清除标签选择和信息面板打开状态。
- 工作区重新激活时，如果信息面板仍打开且恰好选择一个条目，则按当前选择重新加载预览。
- 原生 Quick Look/WebKit 上方的浮动工具继续使用现有 Popup 方案，不退回同层 `Border` 覆盖。

信息面板的模式切换不修改 `FileListViewModel.IsInfoPanelVisible`：

| 转换 | 信息面板结果 |
|---|---|
| 初次进入普通/精简模式 | 分别按 `Inline`/`Overlay` 呈现当前 `IsInfoPanelVisible` 值 |
| 普通 ↔ 精简 | 保留打开状态和 `InfoPanelWidth`，只切换 `DisplayMode`；打开时从 Inline 原位变为窗格内 Overlay，或反向恢复 |
| 工作区失活 | 保持逻辑打开状态，但释放实时宿主并显示非活动占位 |
| 工作区离开槽位 | View 随工作区释放；`IsInfoPanelVisible`、宽度和展开状态留在标签 ViewModel，重新可见时恢复 |

同一窗口内不允许多个实时原生预览同时存在。非活动窗格可以保留信息摘要或占位提示“激活窗格以加载预览”，但不得保留隐藏的原生视图。

“每标签独立预览状态”具体指：

- `FileListViewModel.IsInfoPanelVisible` 和当前文件选择。
- `ExplorerTabViewModel.InfoPanelWidth`，默认 380 DIP，仅在进程内保留。
- `ExplorerTabViewModel.IsPreviewExpanded`，仅在进程内保留。

释放实时宿主后不保证恢复 Quick Look 页码、文档滚动位置或视频播放位置；重新激活时从当前选择重新创建预览会话。若后续需要恢复媒体内部位置，应作为独立功能设计，不能阻塞本次重构。

### 7.5 实时预览协调器

`MainWindow` 持有一个内部 `LivePreviewCoordinator`，负责串行切换唯一实时宿主。它不是预览服务替代品，只协调工作区激活顺序。所有原生子视图增删、Avalonia 属性写入和工作区 Dispose 前的 UI 操作都必须回到 UI Dispatcher。

```csharp
Task ActivateAsync(ExplorerWorkspaceView? nextWorkspace);
```

切换协议：

1. 每次工作区激活请求递增 `activationGeneration`，并以异步互斥锁串行执行。
2. 若存在旧工作区，先把旧工作区 `IsLivePreviewEnabled` 设为 false。
3. 等待旧 `InfoPanelView` 完成取消加载、移除原生子视图和释放临时会话。
4. 再次核对 `activationGeneration`、活动标签和工作区是否仍在可视树中。
5. 只有检查仍有效时，才把新工作区 `IsLivePreviewEnabled` 设为 true 并按当前选择加载。
6. `InfoPanelView` 对每次选择变化、显式刷新和同路径重新加载另行递增 `previewRequestGeneration`，并取消上一个请求；这套代际不依赖工作区是否重新激活。
7. 所有预览异步回调必须同时校验 `activationGeneration`、`previewRequestGeneration` 和当前选择标识；任一不匹配都不得创建原生宿主或回写 UI。路径只能作为辅助校验，不能单独判断请求新旧。

快速连续切换窗格时允许跳过中间工作区的加载，但不能同时存在新旧两个原生宿主。

### 7.6 状态栏

- 每个工作区状态栏绑定自己的 `FileListViewModel`。
- 普通模式显示选择摘要、状态消息和远程位置状态。
- 精简模式保留选择摘要和远程连接状态，低优先级状态文本允许截断。
- 后台任务集合仍由窗口级 `IBackgroundTaskManager` 管理，不随工作区复制。
- 后台任务入口与设置入口放在唯一的窗口级工具栏区域，不放入每个工作区状态栏或 `FinderToolbar`。

## 8. 标签栏和窗格分配

### 8.1 标签栏位置

标签栏保持窗口唯一，视觉上并入顶部工具栏 Surface：

- 第一层：标签列表、新建标签、窗格布局选择器和窗口级更多入口。
- 第二层由各工作区内部呈现页面导航和文件工具栏。
- 不把标签和全部文件工具按钮硬塞进同一横行。
- 分屏时仍只有一个标签栏；工作区内不得再次渲染标签列表。

### 8.2 十二种布局的槽位顺序

`VisiblePanes` 的索引就是稳定槽位编号。不同布局只改变槽位的网格坐标和跨度，不改变同一窗格数下的标签顺序。

| `PaneLayout` | 槽位顺序 |
|---|---|
| `Single` | 0：唯一窗格 |
| `TwoColumns` | 0：左；1：右 |
| `TwoRows` | 0：上；1：下 |
| `ThreeColumns` | 0：左；1：中；2：右 |
| `ThreeRows` | 0：上；1：中；2：下 |
| `MainLeftTwoRowsRight` | 0：左侧主窗格；1：右上；2：右下 |
| `MainRightTwoRowsLeft` | 0：右侧主窗格；1：左上；2：左下 |
| `FourGrid` | 0：左上；1：右上；2：左下；3：右下 |
| `FourColumns` | 0～3：从左到右 |
| `FourRows` | 0～3：从上到下 |
| `MainLeftThreeRowsRight` | 0：左侧主窗格；1：右上；2：右中；3：右下 |
| `MainRightThreeRowsLeft` | 0：右侧主窗格；1：左上；2：左中；3：左下 |

同一窗格数量之间切换布局时，保持 `VisiblePanes` 顺序和 `ActivePaneSlotIndex` 不变，只重新计算网格位置。

### 8.3 活动槽位

`MainWindowViewModel` 增加 `ActivePaneSlotIndex`，范围为 `0..PaneCount-1`。规则如下：

1. 点击工作区时，将其槽位设为活动槽位，并将对应标签设为 `SelectedTab`。
2. 从标签栏选择已经可见的标签时，只切换活动槽位，不改变窗格分配。
3. 从标签栏选择未显示的标签时，用该标签替换 `VisiblePanes[ActivePaneSlotIndex]`。
4. 用户选择需要 N 个窗格的布局而现有标签不足 N 个时，沿用当前 `ApplyPaneLayoutAsync()` 行为：先用 `select:false` 创建标签及独立 DI Scope，直到标签数等于 N，再切换布局。新标签从活动普通文件夹初始化；活动页是虚拟页面时回到 Home。布局不允许空槽位。
5. 新建并选中标签时进入活动槽位；`select:false` 的自动补齐标签按空槽位顺序填充。
6. 关闭可见标签时先记录其槽位：若存在未显示标签，按被关闭标签在 `Tabs` 中的右邻、左邻顺序选择第一个候选补入原槽位；若关闭后标签数少于当前窗格数，则按现有规范布局缩减为 1 个=`Single`、2 个=`TwoColumns`、3 个=`MainLeftTwoRowsRight`。关闭活动标签后，活动槽位保持原索引（超出新范围时钳制到最后一个槽位），`SelectedTab` 改为该槽位最终承载的标签；关闭非活动标签不改变活动槽位。
7. 关闭未显示标签时不改变 `VisiblePanes`、活动槽位或布局；只有关闭后总标签数低于当前窗格数时才按上一条缩减。最后一个标签继续沿用现有规则不可关闭，窗口始终至少有一个完整工作区。
8. 从多窗格切换为单窗格时保留活动标签；从单窗格扩展时按标签顺序补齐其他槽位。

`EnsureSelectedTabVisible()` 不再固定替换最后一个窗格，改为遵守活动槽位规则。

布局窗格数变化时按以下顺序归一化：

1. 保存变更前的活动标签、活动槽位索引和 `VisiblePanes` 顺序。
2. 若缩减窗格数，将活动标签放入 `min(oldActiveIndex, newPaneCount - 1)`。
3. 其余槽位先按原 `VisiblePanes` 顺序填入尚未使用的标签，再按 `Tabs` 顺序补齐。
4. 若扩展窗格数，先按规则 4 创建不足的标签，再保留原槽位，新增槽位按 `Tabs` 顺序填入尚未显示的标签。
5. 最后把 `ActivePaneSlotIndex` 设为活动标签所在索引，并令 `SelectedTab` 与该槽位一致。

该顺序保证活动标签不会因为布局形状或窗格数变化而意外消失。

### 8.4 工作区视图缓存

将 `MainWindow` 的 `_paneViews` 替换为按 `ExplorerTabViewModel` 索引的 `_workspaceViews`：

- 只为 `VisiblePanes` 创建工作区视图。
- 同一标签仍可见时，重排网格不得重建该工作区视图。
- 标签离开所有槽位后，按下面的可等待拆卸协议关闭瞬态 UI 和实时预览，再解绑视图级事件，从可视树及 `_workspaceViews` 字典移除并 Dispose 工作区视图；标签 ViewModel 和 DI Scope 继续存活。
- 标签重新进入槽位时创建新的工作区视图，并绑定原 `ExplorerTabViewModel`，所以导航和选择等模型状态仍可恢复。
- 关闭标签时先完成工作区拆卸，再由标签注册表依次 Dispose 其 `ExplorerTabViewModel` 和 DI Scope。
- 不得把同一个 Avalonia 控件实例同时加入两个父级。

生命周期所有权和顺序固定如下：

- `LivePreviewCoordinator` 是取消预览加载、移除原生子视图和释放预览临时资源的唯一 owner；`ExplorerWorkspaceView.Dispose()` 不重复释放原生宿主。
- `MainWindow` 的工作区注册表是工作区 View 的唯一 owner；标签注册表是 `ExplorerTabViewModel` 和 DI Scope 的唯一 owner。`ExplorerTabViewModel` 只释放自身订阅，不 Dispose `FileListViewModel`；后者由其 DI Scope 恰好释放一次。
- 离开槽位或关闭标签统一调用可等待的 `DetachWorkspaceAsync(tab, reason)`：标记 Detaching 并阻止再激活 → 关闭瞬态 UI → `await LivePreviewCoordinator.DeactivateAndReleaseAsync(workspace)` → 在 UI Dispatcher 从可视树和字典移除、解绑并 Dispose View → 标记 Detached。
- 同一标签上的重复拆卸请求共享一个进行中的 Task；完成后的再次调用为空操作。预览取消或原生宿主关闭即使抛出异常，也必须在记录诊断后通过 `finally` 完成移树和视图解绑。
- 关闭标签只能在上述 Task 完成后从 `Tabs` 和标签注册表移除，并依次 Dispose `ExplorerTabViewModel` 与 DI Scope。离开槽位但未关闭时禁止释放这两者。
- Detaching 期间若用户再次选择该标签，先等待拆卸完成，再创建新的工作区 View 并发起新的 `activationGeneration`；不得复用正在销毁的实例。

## 9. `MainWindow` 命令转发

抽取工作区后，`MainWindow` 不再直接访问唯一的 `SidebarHost`、`ToolbarControl`、`BreadcrumbControl`、`SearchBox`、`InfoDrawer` 或 `InfoPanelControl`。它通过活动工作区转发需要窗口截获的操作。

| 输入/事件 | 目标 |
|---|---|
| `⌘L` | `ActiveWorkspace.FocusPathInput()` |
| 侧栏快捷键/旧窗口侧栏按钮 | `ActiveWorkspace.ToggleSidebar()`；精简模式切换当前窗格 Overlay，普通模式不折叠完整侧栏 |
| 页面搜索快捷键 | `ActiveWorkspace.TogglePageSearch()`；窗口级全局搜索继续走独立命令 |
| 旧窗口视图/排序/更多下拉入口 | 优先把事件移入工作区 `FinderToolbar`；必须保留的窗口入口调用 `ActiveWorkspace.ToggleToolbarMenu(kind)` |
| 信息面板和预览操作 | `ActiveWorkspace.ToggleInfoPanel()` 或活动 `FileListViewModel`；实时宿主创建交给 `LivePreviewCoordinator` |
| 文件列表快捷键 | `ActiveWorkspace.TryHandleFileShortcut(e)` |
| 窗口指针按下 | `ActiveWorkspace.CloseTransientUi(source)` |
| Space 超级预览 | 窗口级 `SuperPreviewView`，数据取活动 `FileListViewModel` |
| 鼠标侧键前进/后退 | 活动 `FileListViewModel` |
| `⌘T`、`⌘W`、Control-Tab | `MainWindowViewModel` 标签命令 |
| 全局搜索 | 窗口级覆盖层，结果打开到活动标签 |
| 批量重命名、远程连接对话框 | 活动 `FileListViewModel` + 窗口级 DialogHost |

页面搜索、侧栏展开、信息面板宽度和工具栏下拉框属于工作区内部，不再由 `MainWindow` 用命名控件直接控制。

工作区激活来源包括：

- 根控件 Tunnel `PointerPressed`，覆盖普通 Avalonia 子控件。
- `GotFocus`，覆盖键盘 Tab、程序化 Focus 和辅助功能导航。
- 点击非活动工作区的信息面板占位层；非活动工作区没有原生预览宿主，因此不存在原生控件吞掉首次激活事件的问题。

激活顺序固定为：先更新 `ActivePaneSlotIndex`/`SelectedTab`，再关闭旧活动工作区瞬态 UI，随后更新桥接服务，最后请求 `LivePreviewCoordinator` 切换宿主。已经活动的原生预览接收点击时不重复触发切换。

Escape 的处理优先级固定为：窗口模态对话框 → 超级预览 → 全局搜索 → 活动工作区文件上下文菜单 → 路径建议 → 工具栏 Popup（含视图、排序和“更多”）→ 已展开的页面搜索 → 精简侧栏 Overlay → 信息面板 Overlay。每一层只有在实际消费按键或关闭 UI 时才设置 `Handled=true` 并停止向后传递，同时把焦点恢复到打开该层的控件或活动文件列表；当前层无可关闭内容时继续下一层，全部未命中时保持未处理，交给文件列表的普通键盘逻辑。

`NavigationBridge`、`IDragDropBridge` 和目录通知注册仍以活动 `FileListViewModel` 为单位；切换活动槽位继续调用现有 Activate/Deactivate 流程。

## 10. 数据与生命周期

本设计不新增持久化结构，也不迁移数据库。

- `ExplorerTabViewModel.FileList` 继续作为标签页面状态根。
- `ExplorerTabViewModel` 增加仅运行时使用的 `InfoPanelWidth` 和 `IsPreviewExpanded`；二者不写入数据库或 `ISettingsService`。
- 新建标签继续创建新的 DI Scope，并在当前普通文件夹初始化；虚拟页面仍回到 Home。
- `FileListViewModel.IsInfoPanelVisible`、导航历史、搜索、排序和选择继续独立保存在标签实例中。
- 主题、`sidebar_show_*` 侧栏条目显示偏好、列布局和其他现有 `ISettingsService` 设置仍是应用级偏好；工作区切换不复制设置数据库记录。精简 Overlay 的临时开闭不新增持久化设置。
- `ForceCompact`、`IsCompact`、活动槽位和实时预览宿主状态仅为运行时 UI 状态，不写入设置。

## 11. 分阶段迁移

每个阶段必须保持可构建、可测试，且不同时维护两套长期页面。

阶段一、二是同一功能分支上的非发布检查点：旧多窗格路径可以暂时保留，以避免未完成的工作区替换进入可发布构建；不得在主分支长期同时支持旧/新页面。阶段三必须把分屏容器、活动命令路由和唯一预览宿主作为一次原子切换提交；三个部分未全部完成时仍是不可发布检查点。阶段三完成后所有窗格只允许走新工作区，阶段四立即删除旧结构。

### 阶段一：抽取完整工作区

1. 新建 `ExplorerWorkspaceView`，从 `MainWindow` 移入侧栏、页面导航、面包屑、页面搜索、文件工具栏、内容、信息面板和状态栏。
2. 首先只按普通单窗格模式渲染，保持现有绑定和事件行为。
3. `MainWindow` 保留标签栏、窗格布局入口和窗口级覆盖层。
4. 将当前选中标签作为新工作区挂入单窗格试验路径；旧多窗格在阶段三完成前保持原行为，不对外宣称重构完成。

验收：单窗格功能和现有界面行为不回退，且窗口中只出现一个面包屑。

### 阶段二：接入工作区精简模式

1. 将响应式计算迁移到 `ExplorerWorkspaceView.Bounds`。
2. 为工作区根、侧栏、工具栏、导航、状态栏和信息面板增加统一 `:compact` 样式。
3. 实现 48 DIP 图标侧栏、220 DIP 覆盖展开、工具栏溢出和信息面板 Overlay。
4. 验证普通/精简切换不替换 DataContext、不丢失选择和滚动状态。

验收：单窗格跨 1180 阈值往返时，所有页面状态保持不变。

### 阶段三：原子替换分屏容器并收敛路由

1. `RebuildPaneLayout()` 改为创建/排列 `ExplorerWorkspaceView`。
2. 多窗格为所有工作区设置 `ForceCompact=true`。
3. 增加活动槽位，并让标签选择替换活动槽位。
4. 将窗格激活事件接入 `SelectedTab` 和活动桥接服务。
5. 在切换 `PaneLayoutRoot` 的同一提交中，将 `MainWindow` 对页面命名控件的访问全部改为活动工作区方法。
6. 同一提交接入 `LivePreviewCoordinator` 和可等待拆卸协议；切换点之前旧分屏路径保持原行为，切换点之后非活动工作区在首次呈现前即设置 `IsLivePreviewEnabled=false`。
7. 把信息面板尺寸、展开状态和实时宿主门控移入工作区，完成关闭、失活和移除时的取消订阅与资源释放。

验收：全部 12 种布局使用同一种工作区且不出现第二面包屑；快捷键、工具栏、搜索、对话框和预览始终作用于活动窗格，任何时刻不超过一个原生预览宿主。未同时满足两组条件不得合并或发布。

### 阶段四：删除旧结构

1. 删除 `ExplorerPaneView` 的使用；确认无其他消费者后删除其 `.axaml`/`.cs`。
2. 删除 `MainWindow` 中已经迁入工作区的旧视图和重复事件处理。
3. 删除旧窗口宽度响应分支、`SetHeaderVisible()` 和固定替换最后窗格的逻辑。
4. 更新现有 Finder parity 文档和必要的 README 描述。

验收：源代码搜索不存在分屏专用面包屑或两套页面渲染路径。

## 12. 测试计划

### 12.1 ViewModel 单元测试

- 标签继续拥有独立 `FileListViewModel`。
- 已显示标签选择只改变活动槽位。
- 未显示标签选择替换活动槽位，不替换固定的最后槽位。
- 关闭活动/非活动标签后的槽位、SelectedTab 和 PaneLayout 正确。
- 关闭未显示标签不改变可见槽位；关闭可见标签且无候选时按 1/2/3 个标签缩减为规范布局；最后一个标签不可关闭。
- 选择标签数不足的 2/3/4 窗格布局时，先自动创建独立 Scope 的标签并填满所有槽位，不允许空槽位。
- 单窗格与多窗格切换始终保留活动标签。
- 12 种 `PaneLayout` 的窗格数量和槽位分配保持正确。
- 12 种布局的槽位索引与第 8.2 节映射完全一致；同窗格数切换不改变标签顺序。
- 缩减/扩展布局后 `ActivePaneSlotIndex` 按第 8.3 节顺序归一化。

### 12.2 Avalonia Headless 测试

- `ForceCompact=false` 且宽度 1180 时为普通模式；1179 时为精简模式。
- `ForceCompact=true` 时，无论宽度多大都为精简模式。
- 普通/精简往返使用同一 `ExplorerWorkspaceView` 和同一 DataContext。
- 普通进入精简时侧栏自动收至 48 DIP，返回普通时恢复 260 DIP Inline 且 `IsPaneOpen=true`；不得残留精简 Overlay 的打开状态。
- `sidebar_show_*` 条目偏好在普通、48 DIP rail 和展开 Overlay 中作用于同一个 `FinderSidebarView`，不会被模式切换改写。
- 每个工作区只有一个 `BreadcrumbBar`。
- 精简侧栏紧凑宽度为 48，展开宽度为 220，打开 Overlay 不改变文件列表 Bounds。
- 信息面板在普通/精简往返时保持 `IsInfoPanelVisible` 与宽度，只在 Inline/Overlay 之间切换；工作区失活后保持打开占位但没有实时宿主。
- 精简工具栏的主操作和溢出操作作用于同一个标签 ViewModel。
- 主操作和溢出项的 `CanExecute`、选中状态和提示文本一致。
- 窗口中只存在一个任务中心入口和一个设置入口，工作区状态栏及 `FinderToolbar` 不复制它们。
- 点击任意工作区后，窗口快捷键转发到对应文件列表。
- 指针、键盘焦点、辅助功能焦点和非活动预览占位层都能激活正确槽位，并恢复合理焦点。
- `⌘L`、页面搜索、路径建议和 Escape 只作用于活动工作区，且不误触发全局搜索。
- Escape 按既定优先级逐层关闭上下文菜单、Popup、页面搜索和两个 Overlay；只有实际消费时设置 `Handled=true`，并恢复正确焦点。
- 非活动工作区 `IsLivePreviewEnabled=false`，活动切换后只有一个工作区为 true。
- 快速连续切换工作区时，协调器必须先完成旧宿主关闭再启动新宿主；过期 `activationGeneration` 不得回写。
- 同一活动工作区内连续选择 A→B→A、同路径显式重新加载时，每次递增 `previewRequestGeneration`；早期 A 或旧同路径结果不得创建宿主或回写。
- 工作区从可视树移除后，工具栏 Popup、路径建议、上下文菜单和预览加载全部关闭。
- 标签离开槽位后工作区视图及视图级订阅被释放，标签 ViewModel/DI Scope 保留；关闭标签后 View、ViewModel、Scope 和 `FileListViewModel` 各由唯一 owner 恰好释放一次。
- 拆卸中的标签被再次激活时等待旧拆卸完成并创建新 View；不得访问、复用或重复释放旧实例。
- 预览临时文件、取消令牌和原生子视图在失活、移除和关闭路径中均释放。
- 预览取消、原生宿主关闭异常和窗口关闭路径均能进入 `finally` 完成移树、解绑及 Scope 释放，并且 UI 操作发生在 Dispatcher 上。

### 12.3 真实 macOS 验证

至少验证以下矩阵：

| 布局 | 尺寸（DIP） | 必验行为 |
|---|---|---|
| 单窗格普通 | 1280×800 | 完整侧栏、工具栏、Inline 信息面板 |
| 单窗格精简 | 1000×680 | 48 DIP 图标栏、工具栏更多、Overlay 信息面板 |
| 左右双窗格 | 1280×800 | 两个精简工作区、活动槽位替换、每页一条面包屑 |
| 上下双窗格 | 1280×800 | 即使宽度充足也强制精简 |
| 三窗格主次布局 | 1280×800 | 主/次窗格都可完整访问功能 |
| 四宫格 | 1280×800 | 无横向溢出、菜单和侧栏覆盖范围正确 |

预览必须覆盖：

- Office/PDF Quick Look 分页和滚动
- HTML WebKit
- 视频 AVPlayer
- Markdown/大文本滚动
- `__remote:` SFTP 文件本地物化后预览
- 活动窗格切换时旧原生预览及时释放
- 使用原生宿主计数或诊断日志确认任何时刻最多存在一个实时 Quick Look/WebKit/AVPlayer 宿主
- 浮动预览工具栏仍显示在原生内容上方
- 图标侧栏支持键盘导航、可访问名称、焦点可见状态和 Escape 关闭/焦点恢复

视觉验收需要保存普通单窗格、精简单窗格、左右双窗格和四宫格截图。构建或 Headless 测试通过不能替代真实渲染与原生预览验证。

## 13. 验收标准

以下条件全部满足后才可认为重构完成：

1. 单窗格和全部多窗格布局只使用 `ExplorerWorkspaceView`。
2. 每个工作区同时包含侧栏、导航、唯一面包屑、工具栏、内容、状态栏和信息面板入口。
3. 普通/精简模式切换不丢失目录、历史、选择、排序、搜索、滚动或面板打开状态。
4. 多窗格全部强制精简；48 DIP 图标栏、220 DIP 覆盖侧栏和工具栏溢出可用。
5. 标签选择按活动槽位替换，行为可预测。
6. 所有窗口级快捷键和对话框作用于活动窗格。
7. 同一窗口最多存在一个实时原生预览宿主；切换、关闭和移除标签后无泄漏。
8. 现有页签、分屏、文件操作和预览测试通过，新增 Headless 测试通过。
9. 真实 macOS 普通、精简、双分屏和四宫格交互及截图验证通过。
10. 源代码中不再存在分屏专用第二面包屑或长期维护的第二套页面。

## 14. 风险与缓解

| 风险 | 缓解措施 |
|---|---|
| Quick Look/WebKit 原生宿主层叠或内存上升 | 只允许活动工作区持有实时宿主；继续使用 Popup 覆盖原生内容 |
| 工作区在网格重排时重复创建、丢失视图状态 | 按可见标签缓存工作区；标签 ViewModel 始终不重建 |
| 事件订阅在标签关闭后泄漏或重复释放 | 使用单一 owner、可等待且幂等的拆卸协议；以恰好一次测试覆盖 View/ViewModel/Scope |
| A→B→A 或同路径重载回写旧预览 | 激活代际和请求代际分离；回调同时验证两级代际与当前选择标识 |
| 四宫格工具栏溢出 | 精简主栏只保留五类核心入口，其余统一进入“更多” |
| 侧栏图标模式与完整模式行为分叉 | 两种模式使用同一个 `FinderSidebarView` 和同一导航处理方法 |
| 窗口快捷键仍引用旧命名控件 | 集中通过 `ActiveWorkspace` 转发，并为每个快捷键增加活动窗格测试 |
| 响应式切换触发布局抖动 | 只在跨越 1180 阈值或 `ForceCompact` 变化时更新伪类和 SplitView 模式 |
| 全局与页面级搜索混淆 | 页面搜索留在工作区；全局搜索继续使用窗口级独立覆盖层和快捷键 |

## 15. 实现检查清单

- [ ] 新建完整 `ExplorerWorkspaceView`
- [ ] 迁移侧栏、页面导航、工具栏、内容、信息面板和状态栏
- [ ] 增加 `ForceCompact`、`IsCompact`、`IsLivePreviewEnabled`
- [ ] 响应式计算改为工作区级
- [ ] 实现 48 DIP `CompactOverlay` 侧栏
- [ ] 实现精简工具栏和统一命令源
- [ ] 实现精简信息面板 Overlay
- [ ] 增加 `ActivePaneSlotIndex`
- [ ] 标签选择改为替换活动槽位
- [ ] 窗口快捷键改为转发 `ActiveWorkspace`
- [ ] 保证仅活动工作区持有实时原生预览
- [ ] 替换 `_paneViews` 和 `ExplorerPaneView`
- [ ] 补充单元、Headless 和真实 macOS 验证
- [ ] 删除旧分屏专用面包屑和窗口级重复页面控件
