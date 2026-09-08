# 高性能文件列表 FastFileList 实施方案

| 项 | 值 |
|---|---|
| 日期 | 2026-09-08 |
| 适用工程 | MacExplorer（FKFinder），Avalonia **12.0.4** |
| 状态 | 待评审 → 实施 |
| 关联分析 | 与 gpui 实现的 FileMate 对比结论（五维差距：保留模式可视树 / 容器回收虚拟化 / 位图图标生命周期 / 多跳线程管道 / GC） |

---

## 1. 背景与动机

当前 `Views/FileListView.axaml` 的列表路径为 `ListBox + VirtualizingStackPanel(CacheLength="1") + 重行模板（每行约 20 个控件实例）`。快速滚动时存在三类可感知问题：

1. **空白行**：容器回收是事后补救——滚动已发生而容器尚未 realize，中间帧即空白；
2. **滚动掉帧**：每行 realize 需经历样式解析 → 模板实例化 → 绑定订阅 → `AttachedToVisualTree` 钩子 → Measure/Arrange 两遍递归 → 图片异步生命周期管理（`OnEntryImageLoaded/Unloaded/DataContextChanged` + 取消令牌）；
3. **加载闪烁**：快照整体替换 `Entries` 触发全列表容器重建，图标管道随之重跑。

**方案核心**：绕过控件实例层，用即时模式自绘 + 逻辑滚动复刻 gpui `uniform_list` 的机制——每帧一次除法算出可见行区间，同步画完提交 Skia。结构上不存在"容器未生成"的中间帧，空白行不可能出现。

Avalonia 本身即 Skia 绘制（所有控件都是画出来的），`Control.Render(DrawingContext)` + `ILogicalScrollable` 将这层能力开放给应用。AvaloniaEdit（数万行代码编辑器）为同路线先例。

---

## 2. 目标 / 非目标

### 目标

- [ ] 单目录 **10 万行**：滚轮滚动 ≥ 55 FPS（Pro 级机器），`Render()` ≤ 2ms（约 24 可见行 @1280×720）
- [ ] 快速滚动 / 跳顶 / 跳底 **零空白行**（截图逐帧验证）
- [ ] 快照整体替换数据时**不重建任何控件**，加载不再闪烁
- [ ] 与现有 ViewModel/数据管道（`FileListDataPipeline`、快照流）**零侵入**——只替换 View 层渲染路径
- [ ] 列表视图全部交互对等：单/多选、Shift/Cmd 选、键盘导航、双击打开、右键菜单、框选、拖放、内联重命名、剪切半透明、Git 徽章
- [ ] 主题/深浅色跟随现有 DynamicResource 刷子

### 非目标（明确排除）

- 网格（图标）视图保持 ListBox 路径——数据量小、模板成本可接受
- 分组视图保持现有嵌套 ListBox 路径，P2 期再评估
- SFTP/回收站/AI 视图不变更
- 完整 VoiceOver 逐行无障碍（见 §11 风险，提供回退开关兜底）

---

## 3. 方案总览

```
┌────────────────────────────────────────────────────────────┐
│ ScrollViewer（原生滚动条 / 触摸板惯性 / 键盘滚动，全部免费） │
│  └─ FastFileList : Control, ILogicalScrollable  ← 直接子级  │
│       ├─ Render(DrawingContext)   即时模式绘制              │
│       │    可见区间 = _offset ÷ RowHeight … (一次除法)      │
│       │    每行: 1 背景矩形 + 1 位图 + 5 段文本 (缓存)      │
│       ├─ 文本缓存  (rowIndex → FormattedText[], 代际失效)   │
│       ├─ 图标      SvgIconCache 现有位图, DrawImage         │
│       ├─ 缩略图槽  IImage?[rows.Count], 异步补图后失效重绘  │
│       └─ 命中测试  Y → 行号 = 除法; 列 X → 列枚举           │
└────────────────────────────────────────────────────────────┘
数据侧（不动）: FileListDataPipeline → 快照 → ViewModel.Entries
接入点:        FileListView.axaml.cs 在快照应用处调用
               FastList.SetRows(entries, scrollBehavior)
```

一帧流程：滚动事件 → ScrollContentPresenter 写 `FastFileList.Offset` → setter 存值 + `InvalidateVisual()` → 下一帧 `Render` 同步画完可见 30 行 → 提交。**offset 变化与行绘制在同一帧内闭环**。

### 每帧成本模型

| 项 | 数量 | 说明 |
|---|---|---|
| 圆角矩形（选中/hover/drag-over 背景） | ≤ 30 | `DrawRectangle` |
| 位图（文件图标） | 30 | `DrawImage`，SvgIconCache 命中即零成本 |
| 文本（名称/日期/大小/类型/徽章） | ~130 | `FormattedText` 缓存命中即引用 |
| 绘制指令合计 | ~190 | Skia 录制 + GPU 提交 < 1ms |

被**整体移除**的每行开销：控件实例化 ×20、绑定订阅 ×10、`AttachedToVisualTree` 钩子、图片取消令牌管理、样式伪类匹配、Measure/Arrange 递归。

---

## 4. 已验证的框架契约（Avalonia 12.0.4，反射核实）

> 以下签名均通过反射从 `~/.nuget/packages/avalonia/12.0.4/ref/net8.0/Avalonia.Controls.dll` / `Avalonia.Base.dll` 导出核实，非文档转录。**写代码时以此为准。**

### 4.1 `ILogicalScrollable`（Avalonia.Controls.Primitives，继承 `IScrollable`）

```csharp
// 继承自 IScrollable：
Size   Extent   { get; }        // 内容总尺寸：new(可用宽度, rows.Count * RowHeight)
Vector Offset   { get; set; }   // presenter 写入滚动偏移；setter 是我们的滚动入口
Size   Viewport { get; }        // 视口尺寸：Bounds.Size
bool   CanHorizontallyScroll { get; }
bool   CanVerticallyScroll   { get; }

// ILogicalScrollable 自身：
bool CanHorizontallyScroll { get; set; }   // presenter 会写
bool CanVerticallyScroll   { get; set; }
bool IsLogicalScrollEnabled { get; }       // 恒 true
Size ScrollSize     { get; }               // 滚轮一格 = new(16, RowHeight)
Size PageScrollSize { get; }               // PageUp/Down = new(0, Viewport.Height)
bool BringIntoView(Control target, Rect targetRect);        // 无子控件 → false
Control GetControlInDirection(NavigationDirection d, Control from); // → null
void RaiseScrollInvalidated(EventArgs e);  // 数据变化时由控件主动调用
event EventHandler? ScrollInvalidated;     // presenter 订阅
```

**关键机制**：`ScrollContentPresenter` 只在**直接子级**上探测 `ILogicalScrollable`（见 Avalonia issue #17867）——`FastFileList` 必须是 `ScrollViewer` 的直接子级。

### 4.2 绘制 API（Avalonia.Media.DrawingContext）

```csharp
void DrawText(FormattedText text, Point origin);            // 前景刷子在 FormattedText 构造时传入
void DrawRectangle(IBrush brush, IPen pen, Rect rect, double radiusX, double radiusY, BoxShadows boxShadows);
void DrawRectangle(IBrush brush, IPen pen, RoundedRect rrect, BoxShadows boxShadows);
void DrawImage(IImage source, Rect destRect);               // 简单形式
void DrawImage(IImage source, Rect sourceRect, Rect destRect); // 需控制采样区时
void DrawLine(IPen pen, Point p1, Point p2);
void Custom(ICustomDrawOperation custom);                   // 需要裸 SKCanvas 时
// 上下文栈：PushOpacity(double)/Pop() 用于剪切行半透明
```

注意：v12 的 `Visual.InvalidateVisual()` **无 Rect 重载**（已验证），缩略图补图采用整控件失效（成本 <1ms，可接受）。

### 4.3 已知框架缺陷及规避

**Issue #20484**（未修复）：`ScrollContentPresenter.UpdateFromScrollable` 先拷贝 `Extent` 触发 Offset 强制 coerce——会用**旧的、钳制后的 offset 回写子控件 Offset**，再读取它，导致子控件刚设置的程序化偏移被覆盖。

**规避**：
- 程序化滚动（`ScrollToIndex` 等）**不依赖**自身 `Offset` setter 生效路径，改为定位祖先 `ScrollViewer` 并直接设置其 `Offset` 属性（确定性下发）；
- 数据替换后先钳制自身 `_offset` 再 `RaiseScrollInvalidated`，使 coerce 结果与预期一致（钳制语义本就是我们想要的）。

---

## 5. 文件清单

### 新增

| 文件 | 职责 | 预估行数 |
|---|---|---|
| `Controls/FastFileList.cs` | 核心自绘控件（滚动契约、渲染、命中测试、键盘导航） | ~650 |
| `Controls/FastFileListModel.cs` | 行数据包装 + 文本缓存 + 代际失效 + 缩略图槽位 | ~200 |
| `Tests/MacExplorer.Tests/FastFileListTests.cs` | 命中测试 / 缓存失效 / 滚动数学的单测 | ~250 |
| `Tools/Performance/FastListBench.cs` | 10 万行合成数据基准（复用现有 Tools/Performance 目录） | ~120 |

### 修改

| 文件 | 改动 |
|---|---|
| `Views/FileListView.axaml` | 在 `FileScroll` Grid 内与 `FileItemsList` 并列新增 `FastListHost`（ScrollViewer + FastFileList）；两者 `IsVisible` 互斥 |
| `Views/FileListView.axaml.cs` | ① 快照应用处（`SnapshotApplied` 事件）分发 `SetRows`；② 列宽变化处（`_effectiveColumnWidths` 更新点，约 L754）同步列宽到控件；③ 交互事件转发（选择/双击/右键/框选/拖放命中）改调控件 API |
| `ViewModels/FileListViewModel.cs` | 无数据层改动；仅暴露当前 `ScrollBehaviorAfterLoad`（已有） |
| `Services/Impl/SvgIconCache.cs` | 无改动（直接复用）；如需 HiDPI 精确像素再加重采样入口 |
| `Services/Settings`（设置服务） | 新增布尔设置 `UseFastFileList`（v1.0.37 起默认 true，保留手动关闭设置） |

---

## 6. 核心控件实现

### 6.1 滚动契约（骨架，签名已对 v12 核实）

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using MacExplorer.Models;

namespace MacExplorer.Controls;

public class FastFileList : Control, ILogicalScrollable
{
    public const double RowHeight = 30.0;   // 与现有 FileListItemTheme Height=30 一致

    private IReadOnlyList<FileSystemEntry> _rows = [];
    private double _offset;                 // 垂直偏移（px，DIP）
    private FastFileListModel _model = new();
    private ScrollViewer? _scrollOwner;     // AttachedToVisualTree 时缓存，用于程序化滚动

    // ── IScrollable ──
    public Size Extent => new(0, _rows.Count * RowHeight);
    public Size Viewport => Bounds.Size;
    public Vector Offset
    {
        get => new(0, _offset);
        set
        {
            var y = Math.Clamp(value.Y, 0, Math.Max(0, _rows.Count * RowHeight - Viewport.Height));
            if (Math.Abs(y - _offset) < 0.01) return;
            _offset = y;
            InvalidateVisual();
        }
    }

    // ── ILogicalScrollable ──
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public bool IsLogicalScrollEnabled => true;
    public Size ScrollSize => new(16, RowHeight);
    public Size PageScrollSize => new(0, Viewport.Height);
    public event EventHandler? ScrollInvalidated;
    public bool BringIntoView(Control target, Rect targetRect) => false;
    public Control? GetControlInDirection(NavigationDirection d, Control from) => null;
    public void RaiseScrollInvalidated(EventArgs e) => ScrollInvalidated?.Invoke(this, e);

    protected override Size MeasureOverride(Size availableSize) => availableSize;

    // 数据入口：快照应用时由 View 层调用（唯一的数据更新通道）
    public void SetRows(IReadOnlyList<FileSystemEntry> rows, ScrollMode behavior)
    {
        _rows = rows;
        _model.OnRowsReplaced(rows);            // 代际 +1，清空文本缓存与缩略图槽
        ApplyScrollBehavior(behavior);          // PreservePosition / ScrollToSelected / Top
        _offset = Math.Clamp(_offset, 0, Math.Max(0, Extent.Height - Viewport.Height));
        RaiseScrollInvalidated(EventArgs.Empty); // 通知 presenter 重算滚动条
        InvalidateVisual();
    }
}
```

### 6.2 行渲染管线

```csharp
public override void Render(DrawingContext ctx)
{
    var bg        = GetBrush("ColorBgContent");
    var hover     = GetBrush("InteractionHoverBrush");
    var selected  = GetBrush("InteractionSelectedBrush");
    var selHover  = GetBrush("InteractionSelectedHoverBrush");
    var primary   = GetBrush("TextPrimaryBrush");
    var secondary = GetBrush("TextSecondaryBrush");

    ctx.DrawRectangle(bg, null, new Rect(Bounds.Size));   // 画布底色（透明也能滚，但底色防撕裂）

    // ① 可见区间：一次除法（uniform_list 同款）
    int first = (int)Math.Floor(_offset / RowHeight);
    int last  = (int)Math.Ceiling((_offset + Bounds.Height) / RowHeight);
    first = Math.Max(0, first);
    last  = Math.Min(_rows.Count, last);

    var cols = _columnWidths;                             // 见 §8.2，与表头同源
    for (int r = first; r < last; r++)
    {
        double y = r * RowHeight - _offset;
        var row  = _rows[r];
        var rect = new Rect(0, y, Bounds.Width, RowHeight);

        // ② 行背景：普通 / hover / 选中 / 选中+hover / 拖放目标，五选一
        var brush = IsSelected(r)
            ? (r == _hoverRow ? selHover : selected)
            : (r == _hoverRow ? hover : null);
        if (brush != null)
            ctx.DrawRectangle(brush, null, rect, 6, 6, default);

        // ③ 剪切行整体半透明（替代旧 Opacity 绑定）
        bool cut = row.IsCut;
        if (cut) ctx.PushOpacity(0.5);

        // ④ 图标：SvgIconCache 位图（HiDPI：请求像素 = 22 × 显示缩放，见 §6.4）
        var icon = _model.GetIcon(row);
        if (icon != null)
            ctx.DrawImage(icon, new Rect(9, y + (RowHeight - 22) / 2, 22, 22));

        // ⑤ 文本五段：全部走缓存（§6.3）
        var texts = _model.GetTexts(r, row, cols, primary, secondary);
        ctx.DrawText(texts.Name,      new Point(30, y + 7));            // 列几何见下表
        ctx.DrawText(texts.Modified,  new Point(52 + cols.Name, y + 7));
        ctx.DrawText(texts.Size,      new Point(52 + cols.Name + cols.Modified + cols.Size - texts.Size.Width - 12, y + 7)); // 右对齐
        ctx.DrawText(texts.Kind,      new Point(52 + cols.Name + cols.Modified + cols.Size, y + 7));

        // ⑥ Git 徽章（可选）：小圆角矩形 + 单字符 FormattedText（按状态字母缓存）
        if (row.HasGitBadge)
            DrawGitBadge(ctx, row, 30 + texts.Name.Width + 4, y);

        if (cut) ctx.Pop();
    }
}
```

**列几何**（与 `FileListView.axaml` 表头 `Grid ColumnDefinitions="22,420,170,110,110,*"` 及行模板 margin 严格对齐）：

| 列 | 起始 X | 宽度来源 | 对齐 |
|---|---|---|---|
| 图标 | 9（22px 槽居中） | 固定 22 | 居中 |
| 名称 | 30 | `cols.Name`（列宽服务） | 左，超出截断（缓存时按列宽生成带省略号的 FormattedText） |
| 修改日期 | 52 + Name | `cols.Modified` | 左 |
| 大小 | 末尾 −12 | `cols.Size` | 右 |
| 类型 | Name+Modified+Size | `cols.Type` | 左 |

### 6.3 文本缓存与失效

```csharp
// FastFileListModel 内部
private int _generation;                                   // 数据版本号
private readonly Dictionary<int, RowText> _texts = [];    // rowIndex → 五段缓存

public RowText GetTexts(int row, FileSystemEntry e, FileListColumnWidths cols, IBrush p, IBrush s)
{
    if (_texts.TryGetValue(row, out var t) && t.Generation == _generation)
        return t;
    var built = new RowText(_generation, cols,
        Build(e.DisplayName, FontSizeBody, p, max: cols.Name - 8),   // 截断由 FormattedText约束实现
        Build(e.ModifiedText, FontSizeLabel, s),
        Build(e.FormattedSize, FontSizeLabel, s),
        Build(e.KindText, FontSizeLabel, s));
    _texts[row] = built;
    if (_texts.Count > 4096) _texts.Clear();               // 长滚动会话防膨胀（全清，代价一次重建）
    return built;
}

private static FormattedText Build(string text, double em, IBrush fg, double? max = null)
{
    var ft = new FormattedText(text, CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight, AppTypography.ListTypeface, em, fg);
    if (max is { } m && ft.Width > m)
        ft.Constraint = new Size(m, double.PositiveInfinity);  // 触发 ellipsis（需 Trimming 设置）
    return ft;
}

public void OnRowsReplaced(IReadOnlyList<FileSystemEntry> rows)
{
    _generation++;
    _texts.Clear();
    _thumbnails = new IImage?[rows.Count];
}
```

要点：
- **代际失效**：快照整体替换 = 一次 `generation++` + 清空，O(1) 触发；
- 名称截断：`FormattedText.Constraint` + `TextTrimming.CharacterEllipsis`（构造后设置）；
- 字号取现有 token：`FontSizeBody` 13 / `FontSizeLabel` 12（与模板 `DynamicResource` 一致）；
- 缓存上限 4096 行，超限全清（重建 30 行 <0.5ms，可接受）。

### 6.4 图标接入（复用 SvgIconCache）

- 现有 `SvgIconCache.GetFileIcon(iconKey, extension, size)` / `GetFolderIcon(size)` 直接返回缓存 `Bitmap`，控件内**零改动复用**；
- 旧路径取 48px 位图再缩到 18px（`BitmapInterpolationMode=LowQuality`）。新路径请求 **size = 22 × DisplayScale**（视网膜屏 44px）一次到位，视觉更锐；
- DisplayScale 获取：`TopLevel?.RenderScaling`，在 `AttachedToVisualTree` 时记录；
- `IconKey == "app-bundle"` 走 `CachedImagePath` 的分支保留：模型层持有 `IImage` 引用数组（复用 `TryGetCachedEntryImage` 现有缓存）。

### 6.5 缩略图槽位（异步补图）

```csharp
// 槽位数组与行数据同长；补图只影响对应行
private IImage?[] _thumbnails;

// View 层在 Render 前（或 100ms 节流回调里）对可见行发起：
foreach (var r in VisibleRange())
    if (_thumbnails[r] == null && NeedsThumbnail(_rows[r]))
        _ = vm.GetListThumbnailAsync(_rows[r], pixelSize, ct)
              .ContinueWith(t => { if (t.Result is { } img) {
                  _model.SetThumbnail(r, img); InvalidateVisual(); } },
                  TaskScheduler.FromCurrentSynchronizationContext());
```

- 相比旧路径（每行 Image 控件 + 订阅 `ThumbnailUrl` 变更 + 取消令牌 + DataContext 防串），新路径是**行号索引的纯数组写入**；
- 快速滚动时旧请求自然失效（`DirectoryWork` token 已有），到货后行号若已不可见，写入槽位但不失效——滚回来直接命中；
- 渲染顺序：`_thumbnails[r]` 非空画缩略图，否则画 §6.4 的类型图标。

### 6.6 命中测试与公开 API

```csharp
// Y → 行号：除法
public int RowIndexAt(Point p) =>
    p.Y < 0 || p.Y >= Extent.Height ? -1 : (int)((p.Y + _offset) / RowHeight);

// 框选支持：矩形与行区间求交
public IEnumerable<int> RowsIntersecting(Rect viewportRect);

// 程序化滚动（#20484 规避：走 ScrollViewer）
public void ScrollToIndex(int row, ScrollPosition pos = Center)
{
    var target = Math.Max(0, row * RowHeight - (Viewport.Height - RowHeight) / 2);
    _scrollOwner?.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(0, target));
}

// 对外交互事件（View 层订阅，替代原 ListBox 事件）
public event Action<int, PointerEventArgs>? RowPointerPressed;
public event Action<int>? RowDoubleTapped;        // Enter 复用
public event Action<IReadOnlyList<FileSystemEntry>>? SelectionChanged;
public event Action<int, PointerPoint>? RowContextMenuRequested;
```

---

## 7. 交互对等

### 7.1 选择模型

- 控件内部 `HashSet<int> _selected` O(1) 渲染判定；
- 变更时**差量**同步 `entry.IsSelected`（旧值翻转的行才写），避免全列表 `INotifyPropertyChanged` 风暴；
- `SelectionChanged` 事件 → View 层转发 ViewModel：`SelectedEntries.Clear()/Add()`（现有 `OnControlSelectionChanged` 的等价替换，逻辑迁移而非重写）；
- Shift 选区 / Cmd 单击切换：在 `OnPointerPressed` 内按修饰键维护 `_anchor` 锚点，规则照搬现有 `_selectionFlaggedEntries` 语义。

### 7.2 键盘导航（控件内实现，`KeyDown`）

| 键 | 行为 |
|---|---|
| ↑/↓ | 选中 ±1 行；`ScrollToIndex` 保证可见 |
| Shift+↑/↓ | 扩展选区 |
| PageUp/PageDown | `PageScrollSize` 偏移 + 移动选中 |
| Home/End | offset=0 / 底部，选中首/末行 |
| Cmd+A | 全选（一次 `_selected` 重建 + 差量同步） |
| Enter | `RowDoubleTapped(当前行)`（与双击同路） |
| 空格/F2 等 | 冒泡给外层 `OnFileListKeyDown` → `TryHandleFileShortcut`（现有快捷键体系不动） |

### 7.3 双击 / 右键菜单

- 双击：`OnPointerPressed` 记录时间与位置，阈值内同位置二次按下 → `RowDoubleTapped` → View 层调用现有打开逻辑（`OnFileItemDoubleTapped` 的行参数版）；
- 右键：命中行 → 若未选中则单选该行 → `RowContextMenuRequested` → View 层用现有 `ContextMenuPopupStyler` 在指针位置弹出（菜单构建完全复用 ViewModel 的 `ContextMenuActions`）。

### 7.4 框选与拖放

- 外层 `InteractionSurface` 的 `DragOver/Drop` 冒泡处理**不变**（自绘控件默认不拦截）；
- 拖放高亮：DragOver 处理器调 `RowIndexAt(e.GetPosition(FastList))`，写控件 `DropTargetRowIndex` 依赖属性，Render 画 `ColorAccentLight` 背景——替代旧 `entry-content.drag-over` 类；
- 框选：`SelectionMarquee` Border 保留；拖动中调 `RowsIntersecting(marqueeRect)` 增量并入 `_selected`——替代旧逐容器命中。

### 7.5 内联重命名

- 临时 `TextBox` 浮层（`AdornerLayer` 或直接 Grid 内绝对定位）：`Y = row * RowHeight - _offset + 4`，`X = 30`，宽 = Name 列宽；
- 现有 `FileOpsViewModel` 重命名提交逻辑不动，仅定位来源从 ListBoxItem 改为行号数学。

---

## 8. 与现有模块的集成点

### 8.1 数据接入（管道零改动）

`FileListViewModel.Snapshots.cs` 的 `SnapshotApplied` 事件已有暴露，View 层订阅：

```csharp
// FileListView.axaml.cs（构造/Attach 处）
ViewModel!= null 时：
ViewModel.SnapshotApplying += FastList.OnSnapshotApplying;   // 可选：冻结 hover
ViewModel.SnapshotApplied  += () =>
    FastList.SetRows(ViewModel.Entries, ViewModel.ScrollBehaviorAfterLoad);
```

- `publishIntermediateSnapshots:false`（现状）下每次目录加载只有 1~2 次提交，`SetRows` 的 O(可见行) 重建成本可忽略；
- 搜索/排序/过滤变化走的也是同一条快照路径——**无需为过滤、排序写任何额外代码**。

### 8.2 列宽联动（复用 FileListColumnLayoutService）

- 控件暴露 `FileListColumnWidths ColumnWidths` 属性（直接用现有类型 `Services/Impl/FileListColumnLayoutService.cs` 中的宽度结构）；
- View 层在现有 `_effectiveColumnWidths` 更新点（列拖拽、`PreferredWidthsChanged`、窗口 resize，约 L754/L778）同步赋值 → 控件 setter 内 `_texts.Clear()` + `InvalidateVisual()`（列宽变化需重建截断文本）；
- 表头本身仍是普通 Grid + 拖拽手柄，**列宽调整体验完全不变**。

### 8.3 主题资源

绘制用刷子全部 `TryFindResource` 动态解析（主题切换时 `ActualThemeVariant` 变化 → `InvalidateVisual`）：

`ColorBgContent` `InteractionHoverBrush` `InteractionSelectedBrush` `InteractionSelectedHoverBrush` `TextPrimaryBrush` `TextSecondaryBrush` `ColorAccentLight` `SelectionOutlineShadow`（选中描边用 `DrawRectangle` pen 形式）

深浅色切换、Liquid Glass 主题调整**自动生效**，无需为新控件维护独立色板。

### 8.4 空态 / 加载 / 错误

现有 `EmptyState` StackPanel、加载徽标、错误文案都是 `FileScroll` Grid 内的独立控件，与本方案无耦合——保留原样，仅 `IsVisible` 逻辑不变。

---

## 9. 迁移策略：双路径 + 开关

```
设置 UseFastFileList (bool, v1.0.37 起默认 true)
├─ true  → FastListHost 可见（ScrollViewer + FastFileList）
└─ false → FileItemsList 可见（现有 ListBox 路径，代码保留）
```

- **P0**：功能在设置里手动开启，团队日常自用验证；
- **P1**：交互对等清单（§7 全项）勾完 → 默认值切 true；
- **ListBox 路径永久保留**：分组视图、网格视图继续用它；同时作为无障碍回退（用户辅助功能需求时切回）与 A/B 性能对照基线；
- 两路径由同一个 ViewModel 驱动，无状态分叉（选择状态都在 entry / ViewModel 上，控件只是渲染器）。

---

## 10. 边界情况清单

| 场景 | 处理 |
|---|---|
| 0 行（空目录） | `Extent.Height=0`，滚动条自然消失；空态由现有 EmptyState 显示 |
| 数据替换瞬间旧 offset 越界 | `SetRows` 内钳制 + `RaiseScrollInvalidated`（§4.3 规避） |
| 行数减少时选中索引越界 | `_selected` 按新 Count 过滤 + 差量同步 entry 标志 |
| 高速滚动中缩略图到货 | 槽位写入，行不可见则不失效；回滚命中 |
| HiDPI / 多屏不同缩放 | `RenderScaling` 在 Attach 时记录；跨屏拖动时 Avalonia 重绘自动用新 DIP 坐标，位图像素尺寸偏差可接受（P1 再加缩放变化监听） |
| 行高未来可配置 | `RowHeight` 改 const → 属性，缓存键加入行高（当前与主题 30px 锁定，暂不做） |
| 字体运行时变更 | 监听 `AppTypography` 变化事件 → `_texts.Clear()` |
| RTL | 不支持（现有模板也仅 LTR），名称列仍左对齐 |

---

## 11. 风险与规避

| 风险 | 等级 | 规避 |
|---|---|---|
| **无障碍退化**：自绘行对 VoiceOver 不可见 | 高 | ① P2 提供基础 `AutomationPeer`（列表角色 + 当前行/选中数播报）；② `UseFastFileList=false` 一键回退完整可访问路径；③ README/发布说明明示 |
| **Issue #20484** offset 回写覆盖 | 中 | 程序化滚动一律走 `ScrollViewer.Offset`（§4.3），不依赖自身 setter 路径；升级 Avalonia 时重测该 issue 状态 |
| 交互回归面大（3400 行 code-behind 耦合） | 中 | 双路径开关保证可回退；§7 清单逐项勾验 + 现有 Tests 补控件级单测 |
| 自绘文本排版与原生 TextBlock 微差（字距/截断） | 低 | 同一 `Typeface`/字号 token；视觉回归用 artifacts/ui-visual-review 流程截图比对 |
| 长会话缓存膨胀 | 低 | 文本缓存 4096 上限全清；缩略图槽数组随行数一次分配 |
| 维护者心智成本（团队对即时模式不熟） | 低 | 控件边界清晰（单一文件 + 模型文件）；本文档 §4 契约节即 onboarding 材料 |

---

## 12. 性能验收基准（P1 出口条件）

在 `Tools/Performance/FastListBench`（合成数据，无磁盘 IO 变量）：

| 指标 | 目标 | 测量方式 |
|---|---|---|
| 10 万行滚轮持续滚动 30s | 平均 ≥ 55 FPS，P99 帧间隔 ≤ 40ms | 控件 Render 内 Stopwatch 聚合（Debug 统计）+ `FileListPerformanceMetrics` 扩展 `FastListRendered(double ms)` |
| 跳顶/跳底/行号跳转 | 目标帧无空白（截图非空像素占比 100%） | Avalonia UITest 截图 + 像素断言 |
| Render 耗时（24 可见行 @1280×720） | ≤ 2ms | Stopwatch 计入性能计数器 |
| 10 万行 → 过滤 3 千行首帧 | ≤ 150ms | 快照提交到 Render 完成计时 |
| 60s 连续滚动内存 | 无单调增长（缓存上限生效） | `GC.GetTotalMemory` 采样 |
| 对照组 | 同场景旧 ListBox 路径 FPS / 空白帧计数（双路径开关即 A/B 开关） | 同一基准跑两次 |

---

## 13. 实施阶段

### P0 — 渲染内核（估 1.5 天）
1. `FastFileListModel`：行包装、文本缓存、代际失效（含单测）
2. `FastFileList`：ILogicalScrollable 契约 + Render（图标/四列文本/选中/hover）+ `SetRows`
3. `FileListView.axaml` 双路径布局 + 设置开关
4. 集成：SnapshotApplied → SetRows；列宽联动；主题刷子
5. 手测：滚动、跳底、主题切换、深浅色
**出口**：10 万行基准跑通，FPS/空白帧达标

### P1 — 交互对等（估 2 天）
1. 选择全家桶（单/Shift/Cmd/Cmd+A/差量同步 ViewModel）
2. 键盘导航 + Enter 打开
3. 双击、右键菜单（复用 ContextMenuPopupStyler）
4. 拖放高亮、框选（RowsIntersecting）
5. 剪切半透明、Git 徽章、缩略图槽
6. 内联重命名浮层
7. 全量交互清单勾验 + 单测补齐
**出口**：§7 清单全勾 + §12 基准全绿 → 默认开关切 true

### P2 — 打磨（估 1 天，可与发布并行）
1. 基础 AutomationPeer（列表角色 + 选中播报）
2. RenderScaling 变化监听（跨屏）
3. 分组模式自绘化评估（组头行绘制 vs 维持嵌套 ListBox——按 P1 实测数据决定）
4. 视觉回归截图入 artifacts 流程

---

## 附：被移除的复杂度（每行）

| 旧路径机制 | 新路径替代 |
|---|---|
| `OnEntryImageLoaded/Unloaded/DataContextChanged` 三钩子 | 无（行号索引数组） |
| `LoadEntryImageAsync` + CancellationToken + 防串校验 | 缩略图槽位写入（§6.5） |
| `ObserveEntryImage` 的 PropertyChanged 订阅/退订 | 无 |
| `OnFileListRowAttachedToVisualTree` | 无 |
| ListBoxItem 容器 realize/unrealize | 除法算区间 |
| 绑定表达式 ×10/行 | 直读 entry 字段 |
| `VirtualizingStackPanel CacheLength` 调参 | 不存在该问题 |
