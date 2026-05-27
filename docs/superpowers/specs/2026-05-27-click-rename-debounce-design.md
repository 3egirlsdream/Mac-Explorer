# 文件列表点击交互修复：重命名 & 双击 & 焦点切换

日期: 2026-05-27 | 状态: 已批准

## 问题

1. **慢点击重命名失效** — 单击已选中文件不进入重命名模式
2. **焦点切换卡顿** — 点击不同文件切换选中时有可感知延迟
3. **连续切换错误打开** — 快速在不同文件间点击偶尔触发错误的 `OpenEntry`

## 根因

WKWebView (MacCatalyst) 对每次物理单击触发 2~3 个 mousedown 事件（间隔 23-50ms）。
当前代码的快速守卫仅在 `!IsEntrySelected` 时拦截重复事件，已选中条目时放过，
导致：

- 第 2 个 mousedown 取消第 1 个建立的 600ms 重命名定时器并重建 → 定时器不断重置 → 重命名永不触发
- 每个 mousedown 都调用 `SelectEntry` → `StateHasChanged` → 全量重渲染 → 卡顿
- `_lastClickTarget` 换文件时不重置，时间戳跨文件误判 → 错误打开

## 方案: mousedown 去抖 + 状态收敛

### 核心改动

`FileGridView.razor` 中 `OnEntryMouseDown` 不再直接执行操作，改为启动 100ms 去抖定时器。
去抖回调统一处理所有逻辑（选择、双击检测、重命名）。

### 交互流程

```
物理单击 A（WKWebView 产生 mousedown#1 + mousedown#2）:

  mousedown#1(T):   捕获 wasAlreadySingleSelected → 启动 100ms 去抖
  mousedown#2(T+30): 重置 100ms 去抖
  click(T+40):      检测到去抖pending → 跳过
  去抖触发(T+130):   ProcessDebouncedMousedown:
                    - 双击检测: _lastClickTarget==A && diff in [150,500)? → 否
                    - wasAlreadySingleSelected? → 是 → 启动 400ms rename timer
                    - SelectEntry(A) → CollectionChanged → StateHasChanged (仅一次)
  rename触发(T+530): StartRename → 进入重命名 ✓
```

```
双击 A (两次物理单击, 间隔 ~300ms):

  第一次单击 → 去抖 → ProcessDebouncedMousedown:
    _lastClickTarget=A, _lastClickTimeMs=T
    wasAlreadySingleSelected? → 否（首次未选中）→ SelectEntry(A)

  第二次单击(300ms后) → 去抖 → ProcessDebouncedMousedown:
    _lastClickTarget==A && 300ms in [150,500)? → 是 → OpenEntry(A) ✓
```

### 状态变量

| 变量 | 用途 |
|------|------|
| `_mousedownDebounceTimer` | 100ms 去抖计时器 |
| `_capturedMouseDownEntry` | 去抖期间捕获的条目 |
| `_capturedWasAlreadySingleSelected` | 去抖期间捕获的是否已单选 |
| `_capturedModifiers` | 去抖期间捕获的修饰键 (ctrl/meta/shift) |
| `_lastClickTarget` / `_lastClickTimeMs` | 双击检测（已有，保持） |
| `_renameTimer` | 慢点击重命名定时器（已有，延迟从 600ms 改为 400ms） |

### 关键规则

1. **去抖内拦截 click**: `OnEntryClick` 检测 `_mousedownDebounceTimer != null` 时直接 return
2. **去抖内拦截 mouseup**: `OnEntryMouseUp` 检测 `_mousedownDebounceTimer != null` 时直接 return
3. **状态在 mousedown 捕获**: `wasAlreadySingleSelected` 和 modifiers 在第一个 mousedown 时就捕获，不依赖后续事件的 ObservableCollection 状态
4. **去抖触发时机**: 最后一次 mousedown 后 100ms，确保 WKWebView 的所有重复事件已到达
5. **双击窗口**: `[150ms, 500ms)`, 150ms 下界 = 100ms 去抖 + 50ms 安全边距

### 文件改动

- `src/MacExplorer/Components/FileList/FileGridView.razor`
  - `OnEntryMouseDown`: 去抖包装，只捕获状态
  - `OnEntryClick`: 新增去抖 pending 跳过
  - `OnEntryMouseUp`: 新增去抖 pending 跳过
  - 新增 `ProcessDebouncedMousedown`: 统一的 mousedown 处理逻辑
  - `_renameTimer` 延迟: 600ms → 400ms

### 不影响的部分

- `FileListViewModel.cs` — 无需改动（`IsEntrySelected`、`SelectEntry` 已优化）
- 拖放逻辑 — `OnDragStart` 正常工作，去抖不影响 dragstart 事件
- 右键菜单 — 不经过 mousedown 去抖路径
- `OnContentMouseDown` — 背景点击、重命名提交逻辑不变

### 风险

- 100ms 去抖引入的响应延迟极小，用户不可感知
- 需要确保 `DisposeAsync` 正确清理 `_mousedownDebounceTimer`

## 验证清单

1. 单击已选中文件 → 400ms 后进入重命名模式
2. 快速双击同一文件 → 打开/进入文件夹
3. 快速切换点击 A→B→C → 每次正确选中，不错误打开
4. 新建文件/文件夹 → 自动进入重命名
5. 拖放多选文件 → 正常拖动，不触发重命名
6. 右键菜单 → 正常弹出
7. 点击空白区域 → 取消选中，正在重命名时提交重命名
