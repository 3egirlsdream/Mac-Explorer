# MacExplorer 重命名功能 & 点击交互 BUG 修复总结

## 概述

本文档记录了文件列表点击交互的完整修复历程，涵盖重命名失效、焦点切换卡顿、连续切换错误打开、WKWebView 事件处理等多项问题。

---

## Bug 1：点击空白区域重命名不生效，只有回车才生效

### 现象

用户在文件列表中进入重命名模式后，按回车可以正常提交新名称，但点击空白区域（失焦）时，重命名被取消而非提交。

### 根因分析

`FileGridView.razor` 中的 `OnContentMouseDown` 方法在检测到 `_renamingEntry != null` 时，直接清除状态：

```csharp
// 修复前（错误）
if (_renamingEntry != null)
{
    _renamingEntry = null;
    _renameText = "";
}
```

由于浏览器事件执行顺序 `mousedown` **先于** `blur` 触发：

1. 用户点击空白区域 → `OnContentMouseDown` 运行 → 清除 `_renamingEntry`
2. Input 失焦 → `onblur` 触发 → `CommitRename()` 运行
3. `CommitRename()` 检查 `_renamingEntry == null` → 提前 return，重命名未提交

### 修复方案

将 `OnContentMouseDown` 改为在 `_renamingEntry != null` 时调用 `await CommitRename()`：

```csharp
// 修复后（正确）
if (_renamingEntry != null)
{
    await CommitRename();
    return;
}
```

### 涉及文件

- `src/MacExplorer/Components/FileList/FileGridView.razor` — `OnContentMouseDown` 方法

---

## Bug 2：AI 汇总目录中点击重命名输入框会退出重命名模式

### 现象

在 AI 视图（人脸聚类等分类目录）中，右键点击条目选择"重命名"后，输入框正常出现。但当用户点击输入框（如定位光标）时，重命名模式意外退出。

### 根因分析

Rename input 上通过 `RenderTreeBuilder` 添加了 `onmousedown:stopPropagation`，但**没有注册显式的 `onmousedown` 事件处理器**。在 macOS Catalyst 的 WKWebView 中，`stopPropagation` 在无显式 handler 时可能未能有效阻止事件冒泡。

### 修复方案

采用**防御性标志位**机制：

1. 添加 `_renameInputMouseDown` 标志字段
2. 在两处 rename input 添加显式 `onmousedown` handler
3. 在 `OnContentMouseDown` 开头检查标志并跳过

### 涉及文件

- `src/MacExplorer/Components/FileList/FileGridView.razor` — rename input 渲染代码 + `OnContentMouseDown` 方法

---

## Bug 3：AI 分类重命名后文件名不立即刷新显示

### 现象

在 AI 视图中重命名文件或人脸聚类后，底层数据已更新，但 UI 上文件名仍显示旧名称。

### 根因分析

`ObservableCollection` 的索引器赋值 `Entries[i] = newEntry` 虽触发 `CollectionChanged`，但 `FileGridView` 监听的是 `PropertyChanged(nameof(Entries))`。未调用 `OnPropertyChanged` 导致 UI 不刷新。

### 修复方案

在替换 entry 后手动调用 `OnPropertyChanged(nameof(Entries))`。

### 涉及文件

- `src/MacExplorer/ViewModels/FileListViewModel.cs` — `RenameEntryAsync` 和 `RenameFaceClusterAsync` 方法

---

## Bug 4：单击已选中文件不进入重命名模式（WKWebView 双重 mousedown）

### 现象

单击已选中的文件，等很久也不进入重命名模式。同时偶尔出现单击文件直接打开（误判为双击）的问题。

### 根因分析

WKWebView (MacCatalyst) 对每次物理单击触发 2~3 个 mousedown 事件（间隔 23-50ms）。原代码的快速守卫仅在 `!IsEntrySelected` 时拦截重复事件，已选中条目时放过。导致：

1. 第 2 个 mousedown 取消第 1 个建立的 600ms 重命名定时器并重建 → 定时器不断重置 → 重命名永不触发
2. 第 2 个 mousedown 与原 `_pendingOpenEntry` 匹配 → 误判为双击 → 错误打开文件

### 修复方案

**统一 60ms 门控**拦截所有重复 mousedown，选择/双击/重命名全部在 `OnEntryMouseDown` 内直接处理：

```csharp
var nowMs = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

// Guard: block ALL WKWebView duplicate mousedown (23-50ms apart)
if (nowMs - _lastMouseDownMs < 60) return;
_lastMouseDownMs = nowMs;

// Capture wasAlreadySingleSelected BEFORE SelectEntry mutates selection
bool wasAlreadySingleSelected = !e.CtrlKey && !e.MetaKey && !e.ShiftKey
    && ViewModel.SelectedEntries.Count == 1
    && ViewModel.IsEntrySelected(entry);

// Double-click: same entry within [100ms, 500ms)
if (_lastClickTarget == entry && nowMs - _lastClickTimeMs is >= 100 and < 500)
{
    _lastClickTarget = null;
    _lastClickTimeMs = 0;
    _ = OpenEntry(entry);
    return;
}
_lastClickTarget = entry;
_lastClickTimeMs = nowMs;

// Select immediately — instant visual feedback
ViewModel.SelectEntry(entry, e.CtrlKey || e.MetaKey, e.ShiftKey);

// Schedule slow-click rename (400ms timer)
if (wasAlreadySingleSelected)
{
    _renameTimer = new System.Threading.Timer(_ =>
    {
        if (!_isDragging && _renamingEntry == null
            && ViewModel.SelectedEntries.Count == 1
            && ViewModel.IsEntrySelected(clickedEntry))
        {
            InvokeAsync(() => StartRename(clickedEntry));
        }
    }, null, 400, Timeout.Infinite);
}
```

**关键设计决策：**
- 60ms 门控统一拦截所有 WKWebView 重复事件（之前只在 `!IsEntrySelected` 时拦截）
- `wasAlreadySingleSelected` 在 `SelectEntry` **之前**捕获（避免 SelectEntry 改变状态后误判）
- 双击检测使用时间戳比较（`[100ms, 500ms)`），替代旧的 `_pendingOpenEntry` 匹配方式
- 重命名定时器从 600ms 缩短为 400ms（无重复事件干扰后）
- `OnEntryClick` 简化为安全 fallback，`OnEntryMouseUp` 为 no-op

### 涉及文件

- `src/MacExplorer/Components/FileList/FileGridView.razor` — `OnEntryMouseDown`、`OnEntryClick`、`OnEntryMouseUp` 重写

---

## Bug 5：焦点切换卡顿（每次点击多次 StateHasChanged）

### 现象

在大目录（500+ 文件）中点击不同文件切换焦点时，有可感知的卡顿延迟。

### 根因分析

经过多次迭代排除了以下原因：

1. **去抖延迟**（已废弃）：最初尝试用 100ms 去抖定时器，`InvokeAsync` 引入额外 `StateHasChanged`，每点击 2 次渲染
2. **Virtualize 方案**（已回退）：Blazor Virtualize 在 MAUI Hybrid flex 布局链中无法获取有效高度约束，滚动失效
3. **_pendingDeselectEntry 机制**（已移除）：多选拖放的延迟取消机制在去抖方案中不再需要

最终确认：60ms 门控方案（Bug 4 修复）本身是高效的——每物理点击仅 1 次 `SelectEntry` → 1 次 `StateHasChanged`。卡顿的根因是**全量渲染**：大目录下所有条目参与 Blazor diff。

### 当前状态

60ms 门控方案已最小化 `StateHasChanged` 调用次数。进一步的渲染性能优化（如虚拟滚动）需要解决 MAUI Hybrid 中的高度约束问题，后续单独处理。

### 涉及文件

- `src/MacExplorer/Components/FileList/FileGridView.razor` — 去抖逻辑多次迭代后回退至 60ms 门控
- `src/MacExplorer/wwwroot/css/app.css` — Virtualize 配套 CSS 已回退

---

## Bug 6：`OnContentMouseDown` 取消重命名定时器（stopPropagation 不可靠）

### 现象

重命名定时器在 `OnEntryMouseDown` 中正常启动，但 400ms 到期前被取消。"Timer fired" 日志从未出现。

### 根因分析

WKWebView 中 `@onmousedown:stopPropagation` 不可靠。点击文件条目时，`OnEntryMouseDown` 启动了重命名定时器，但 `mousedown` 事件同时也冒泡到了 `OnContentMouseDown`，后者**无条件取消**重命名定时器：

```csharp
// OnContentMouseDown 中的问题代码
_renameTimer?.Dispose();
_renameTimer = null;
```

### 修复方案

添加 `_entryMouseDownHandled` 标志位。`OnEntryMouseDown` 在 60ms 门控通过后设置标志，`OnContentMouseDown` 检查标志并跳过：

```csharp
// OnEntryMouseDown
if (nowMs - _lastMouseDownMs < 60) return;
_lastMouseDownMs = nowMs;
_entryMouseDownHandled = true;  // signal OnContentMouseDown to skip

// OnContentMouseDown
if (_entryMouseDownHandled)
{
    _entryMouseDownHandled = false;
    return;
}
```

### 涉及文件

- `src/MacExplorer/Components/FileList/FileGridView.razor` — `OnEntryMouseDown`、`OnContentMouseDown`

---

## Bug 7：重命名输入框闪现后消失（spurious blur 事件）

### 现象

`StartRename` 被正常调用，但重命名输入框出现约 1.4 秒后自动消失，用户无法输入新名称。

### 根因分析

日志追踪发现完整链路：

1. `StartRename` → `_renamingEntry = entry` → `StateHasChanged` → 渲染 rename input
2. `FocusRenameTextField` 尝试聚焦 `MTextField` → `FocusAsync()` 可能静默失败（异常被 `catch { }` 吞掉）
3. 输入框未获得焦点 → ~1.4 秒后 spurious `blur` 事件触发
4. `OnRenameBlur` → 超过 500ms 守卫 → `CommitRename()`
5. `CommitRename` 先设 `_renamingEntry = null` 移除输入框，**然后**检查 `newName == entry.Name` → 名字没变 → 静默返回

用户看到的是输入框闪现后消失，因为 `CommitRename` 在检查名字之前就移除了 DOM 元素。

### 修复方案

**两处修复：**

1. **`OnRenameBlur` 不再提交重命名**：blur 时只重新聚焦输入框，不调用 `CommitRename`。重命名只能通过 Enter 键或点击空白区域提交：

```csharp
private async Task OnRenameBlur()
{
    // Spurious blur events are common in WKWebView. Always re-focus.
    if (_renamingEntry != null)
    {
        _renameInputFocused = false;
        StateHasChanged();  // triggers ComponentReferenceCapture → FocusRenameTextField
    }
}
```

2. **`CommitRename` 名字检查前置**：在移除 DOM 元素之前检查名字是否变化。若未变化，取消重命名（而非静默返回）：

```csharp
var newName = _renameText.Trim();
if (string.IsNullOrWhiteSpace(newName) || newName == entry.Name)
{
    _renamingEntry = null;
    _renameText = "";
    _renameInputFocused = false;
    StateHasChanged();
    return;
}
// ... scroll save, blur active, rename
```

### 涉及文件

- `src/MacExplorer/Components/FileList/FileGridView.razor` — `OnRenameBlur`、`CommitRename`

---

## Bug 8：连续切换点击文件导致错误打开

### 现象

快速在不同文件间点击偶尔触发错误的 `OpenEntry`（文件/文件夹被打开而非选中）。

### 根因分析

与 Bug 4 共享根因。原代码的 `_pendingOpenEntry` 匹配方式在 WKWebView 双重 mousedown 下将第二个 mousedown 误判为双击。

Bug 4 的修复（60ms 门控 + 时间戳双击检测）同步解决了此问题。

---

## 修改文件清单

| 文件 | 修改内容 |
|------|---------|
| `src/MacExplorer/Components/FileList/FileGridView.razor` | 所有 Bug 修复：60ms 门控、`_entryMouseDownHandled` 标志、blur 重新聚焦、CommitRename 名字检查前置 |
| `src/MacExplorer/ViewModels/FileListViewModel.cs` | Bug 3: 添加 `OnPropertyChanged(nameof(Entries))`；`_selectedEntriesSet` (HashSet) O(1) 查找；`SelectEntry` 优化 |
| `src/MacExplorer/wwwroot/css/app.css` | Bug 5 探索过程中 CSS 调整（overflow 变更已回退至原始状态） |

---

## 关键经验教训

1. **WKWebView 双重 mousedown**：MacCatalyst WKWebView 每次物理点击触发 2~3 个 mousedown 事件（间隔 23-50ms）。必须用时间门控（60ms）统一拦截，不能只在特定条件下拦截。

2. **`stopPropagation` 不可靠**：在 WKWebView 中，Blazor 的 `@onmousedown:stopPropagation` 可能不生效。需要额外的标志位机制（`_entryMouseDownHandled`、`_renameInputMouseDown`）来防止事件处理函数间的冲突。

3. **`System.Threading.Timer` 回调的线程安全**：Timer 回调在线程池线程运行，调用 `InvokeAsync` 需要确保目标组件未被 Dispose。访问 `ObservableCollection` 和 `HashSet` 需要注意跨线程安全。

4. **Blazor `InvokeAsync` 的副作用**：`InvokeAsync(Action)` 在 Blazor 中执行完 action 后会触发 `StateHasChanged()`，导致额外的渲染。只在必要时使用。

5. **重命名提交的时序**：名字检查必须在 DOM 清理之前。`CommitRename` 应先验证名字有变化，再执行滚动保存、blur、DOM 清理。

6. **Blazor Virtualize 在 MAUI Hybrid 中的局限**：Virtualize 需要父容器有明确的高度约束（`height: 100%` 在 flex 布局链中无法解析）。在 MAUI Hybrid 的嵌套 flex 布局中不可用。后续可考虑自定义虚拟滚动或 `content-visibility: auto` CSS 优化。
