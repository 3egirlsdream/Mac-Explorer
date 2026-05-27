# 文件列表点击交互修复 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** mousedown 去抖消除 WKWebView 重复事件，修复重命名失效、焦点切换卡顿、错误打开三个问题

**Architecture:** `OnEntryMouseDown` 改为只捕获状态并启动 100ms 去抖定时器；`ProcessDebouncedMousedown` 去抖回调中统一处理选择、双击检测、重命名；`OnEntryClick`/`OnEntryMouseUp` 在去抖 pending 时直接 return

**Tech Stack:** C# / Blazor Hybrid (.NET MAUI MacCatalyst) / Masa.Blazor

---

### Task 1: 更新状态变量声明

**Files:**
- Modify: `src/MacExplorer/Components/FileList/FileGridView.razor:268-288`

- [ ] **Step 1: 替换 mousedown 相关状态变量**

找到 `@code` 块中的状态变量声明区域（约 268-288 行），替换为以下内容：

```csharp
    // Drag-and-drop state
    private FileSystemEntry? _draggedEntry;
    private List<FileSystemEntry>? _draggedEntries;
    private FileSystemEntry? _dragOverEntry;
    private bool _isDragging;
    private int _dragCount;

    // Inline rename state
    private FileSystemEntry? _renamingEntry;
    private string _renameText = "";
    private bool _skipScrollHandlingOnNextEntriesChange;
    private bool _renameInputMouseDown;
    private DateTime _renameStartedAt;

    // ── Mousedown debounce state ──
    // WKWebView fires 2-3 mousedown events per physical click (~23-50ms apart).
    // Debounce consolidates them: capture state on each mousedown, reset a 100ms timer,
    // and process only after silence. This prevents rename timer reset loops and
    // redundant SelectEntry→StateHasChanged→re-render cycles.
    private System.Threading.Timer? _mousedownDebounceTimer;
    private FileSystemEntry? _capturedMouseDownEntry;
    private bool _capturedWasAlreadySingleSelected;
    private bool _capturedCtrlKey;
    private bool _capturedMetaKey;
    private bool _capturedShiftKey;

    // ── Double-click & slow-click rename state ──
    // Double-click: same entry clicked twice within [150ms, 500ms).
    // 150ms lower bound = 100ms debounce + 50ms safety margin.
    // Slow-click rename: single click on already-selected entry → 400ms timer → rename mode.
    private System.Threading.Timer? _renameTimer;
    private FileSystemEntry? _lastRenameTarget;
    private FileSystemEntry? _lastClickTarget;
    private long _lastClickTimeMs;
```

删除以下旧变量（如果存在）：
- `_lastMouseDownMs`
- `_pendingDeselectEntry`
- `_pendingDeselectTimer`

- [ ] **Step 2: Commit**

```bash
git add src/MacExplorer/Components/FileList/FileGridView.razor
git commit -m "refactor: 替换 mousedown 状态变量声明为去抖模式"
```

---

### Task 2: 重写 OnEntryMouseDown 为去抖包装

**Files:**
- Modify: `src/MacExplorer/Components/FileList/FileGridView.razor:541-616`

- [ ] **Step 1: 替换 OnEntryMouseDown 方法**

找到 `OnEntryMouseDown` 方法（约 541-616 行），替换为：

```csharp
    private void OnEntryMouseDown(FileSystemEntry entry, MouseEventArgs e)
    {
        if (e.Button != 0 || _renamingEntry != null || _isDragging) {
            if (_isDragging) DebugLog($"[DragDrop] OnEntryMouseDown SKIPPED (still dragging): entry={entry.Name}");
            return;
        }

        // Cancel pending slow-click rename on any new mousedown
        _renameTimer?.Dispose();
        _renameTimer = null;
        _lastRenameTarget = null;

        // Capture state at mousedown time — before click/mouseup can mutate
        // SelectedEntries or trigger StateHasChanged.
        _capturedMouseDownEntry = entry;
        _capturedWasAlreadySingleSelected = !e.CtrlKey && !e.MetaKey && !e.ShiftKey
            && ViewModel.SelectedEntries.Count == 1
            && ViewModel.IsEntrySelected(entry);
        _capturedCtrlKey = e.CtrlKey;
        _capturedMetaKey = e.MetaKey;
        _capturedShiftKey = e.ShiftKey;

        // Debounce: reset timer on each mousedown, process after 100ms of silence.
        // This absorbs WKWebView's duplicate mousedown events (23-50ms apart).
        _mousedownDebounceTimer?.Dispose();
        _mousedownDebounceTimer = new System.Threading.Timer(_ =>
        {
            _mousedownDebounceTimer?.Dispose();
            _mousedownDebounceTimer = null;
            InvokeAsync(() => ProcessDebouncedMousedown());
        }, null, 100, Timeout.Infinite);
    }
```

注意：此方法现在**不再调用** `SelectEntry`、不再做双击检测、不再启动重命名定时器。这些逻辑全部移至 `ProcessDebouncedMousedown`。

- [ ] **Step 2: Commit**

```bash
git add src/MacExplorer/Components/FileList/FileGridView.razor
git commit -m "refactor: OnEntryMouseDown 改为去抖包装，只捕获状态"
```

---

### Task 3: 新增 ProcessDebouncedMousedown 统一处理逻辑

**Files:**
- Modify: `src/MacExplorer/Components/FileList/FileGridView.razor` — 在 `OnEntryMouseDown` 之后插入

- [ ] **Step 1: 插入 ProcessDebouncedMousedown 方法**

在 `OnEntryMouseDown` 方法结束后、`OnEntryMouseUp` 方法开始前，插入：

```csharp
    /// <summary>
    /// Called after 100ms of mousedown silence. Handles selection, double-click
    /// detection, and slow-click rename scheduling — all in one place to avoid
    /// redundant StateHasChanged calls from WKWebView's duplicate events.
    /// </summary>
    private void ProcessDebouncedMousedown()
    {
        var entry = _capturedMouseDownEntry;
        if (entry == null || _isDragging) return;

        var nowMs = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

        // Double-click detection: same entry clicked twice within [150ms, 500ms).
        // Lower bound (150ms) = debounce window (100ms) + safety margin (50ms)
        // to prevent WKWebView duplicate mousedown from being counted as a second click.
        if (_lastClickTarget == entry && nowMs - _lastClickTimeMs is >= 150 and < 500)
        {
            _lastClickTarget = null;
            _lastClickTimeMs = 0;
            _renameTimer?.Dispose();
            _renameTimer = null;
            _lastRenameTarget = null;
            if (_renamingEntry == null)
                _ = OpenEntry(entry);
            return;
        }

        _lastClickTarget = entry;
        _lastClickTimeMs = nowMs;

        // Execute selection (single StateHasChanged via CollectionChanged → OnSelectionChanged)
        ViewModel.SelectEntry(entry, _capturedCtrlKey || _capturedMetaKey, _capturedShiftKey);

        // Auto-close metadata panel when clicking a file
        if (ViewModel.IsMetadataPanelVisible)
            ViewModel.CloseMetadataCommand.Execute(null);

        // Schedule slow-click rename if this entry was already the sole selection
        if (_capturedWasAlreadySingleSelected)
        {
            _lastRenameTarget = entry;
            var clickedEntry = entry;
            _renameTimer = new System.Threading.Timer(_ =>
            {
                _renameTimer = null;
                _lastRenameTarget = null;
                if (!_isDragging && _renamingEntry == null
                    && ViewModel.SelectedEntries.Count == 1
                    && ViewModel.IsEntrySelected(clickedEntry))
                {
                    InvokeAsync(() => StartRename(clickedEntry));
                }
            }, null, 400, Timeout.Infinite);
        }
    }
```

- [ ] **Step 2: Commit**

```bash
git add src/MacExplorer/Components/FileList/FileGridView.razor
git commit -m "feat: 新增 ProcessDebouncedMousedown 统一处理选择/双击/重命名"
```

---

### Task 4: 门控 OnEntryClick 和 OnEntryMouseUp

**Files:**
- Modify: `src/MacExplorer/Components/FileList/FileGridView.razor:513-531` (OnEntryClick)
- Modify: `src/MacExplorer/Components/FileList/FileGridView.razor:618-645` (OnEntryMouseUp)

- [ ] **Step 1: 重写 OnEntryClick**

将 `OnEntryClick` 方法替换为：

```csharp
    private void OnEntryClick(FileSystemEntry entry, MouseEventArgs e)
    {
        // If debounce is still pending, ProcessDebouncedMousedown will handle everything.
        // This prevents duplicate StateHasChanged and selection changes from the click
        // event that fires between WKWebView's duplicate mousedown and our debounce callback.
        if (_mousedownDebounceTimer != null || _renamingEntry != null || _isDragging) return;

        ViewModel.SelectEntry(entry, e.CtrlKey || e.MetaKey, e.ShiftKey);

        if (ViewModel.IsMetadataPanelVisible)
            ViewModel.CloseMetadataCommand.Execute(null);
    }
```

- [ ] **Step 2: 重写 OnEntryMouseUp**

将 `OnEntryMouseUp` 方法替换为：

```csharp
    private void OnEntryMouseUp(FileSystemEntry entry, MouseEventArgs e)
    {
        // When debounce is active, selection is handled by ProcessDebouncedMousedown.
        // Multi-selection drag is handled by OnDragStart which fires after mouseup
        // (WKWebView quirk) — it snapshots from SelectedEntries directly, so we
        // don't need the old _pendingDeselectEntry dance.
        if (_mousedownDebounceTimer != null) return;

        if (e.Button != 0) return;
    }
```

- [ ] **Step 3: Commit**

```bash
git add src/MacExplorer/Components/FileList/FileGridView.razor
git commit -m "refactor: OnEntryClick/OnEntryMouseUp 门控去抖 pending"
```

---

### Task 5: 清理 OnDragStart 和 OnDragEnd 的过时引用

**Files:**
- Modify: `src/MacExplorer/Components/FileList/FileGridView.razor:694-792`

- [ ] **Step 1: 清理 OnDragStart**

找到 `OnDragStart` 方法，删除 `_pendingDeselectTimer`、`_pendingDeselectEntry` 相关行：

替换：
```csharp
    private void OnDragStart(FileSystemEntry entry)
    {
        if (entry.IsVirtual) return;
        DebugLog($"[DragDrop] OnDragStart: entry={entry.Name}, isDir={entry.IsDirectory}, selectedCount={ViewModel.SelectedEntries.Count}, draggedEntriesCount={_draggedEntries?.Count}, isDragging={_isDragging}");
        _isDragging = true;
        _renameTimer?.Dispose();
        _renameTimer = null;
        _lastRenameTarget = null;
        _draggedEntry = entry;

        // With debounce, ProcessDebouncedMousedown won't fire since _isDragging is now true.
        // Snapshot selected entries for drag — debounced mousedown doesn't pre-populate
        // _draggedEntries, so OnDragStart always snapshots from SelectedEntries directly.
        _draggedEntries = ViewModel.IsEntrySelected(entry)
            ? ViewModel.SelectedEntries.ToList()
            : new List<FileSystemEntry> { entry };

        if (!ViewModel.IsEntrySelected(entry))
            ViewModel.SelectEntry(entry, false, false);

        _dragCount = _draggedEntries?.Count ?? 1;
        StateHasChanged();
    }
```

- [ ] **Step 2: 清理 OnDragEnd**

找到 `OnDragEnd` 方法，替换为：

```csharp
    private void OnDragEnd()
    {
        DebugLog($"[DragDrop] OnDragEnd: isDragging={_isDragging}, draggedEntriesCount={_draggedEntries?.Count}");
        _isDragging = false;
        _draggedEntry = null;
        _draggedEntries = null;
        _dragOverEntry = null;
        _dragCount = 0;
    }
```

- [ ] **Step 3: Commit**

```bash
git add src/MacExplorer/Components/FileList/FileGridView.razor
git commit -m "refactor: 清理 OnDragStart/OnDragEnd 的过时 _pendingDeselect 引用"
```

---

### Task 6: 更新 DisposeAsync 清理去抖定时器

**Files:**
- Modify: `src/MacExplorer/Components/FileList/FileGridView.razor:258-266`

- [ ] **Step 1: 更新 DisposeAsync**

找到 `DisposeAsync` 方法，替换为：

```csharp
    public async ValueTask DisposeAsync()
    {
        ViewModel.RenameRequested -= OnRenameRequested;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.SelectedEntries.CollectionChanged -= OnSelectionChanged;
        _mousedownDebounceTimer?.Dispose();
        _renameTimer?.Dispose();
        await RevokeAllBlobUrlsAsync();
    }
```

- [ ] **Step 2: Commit**

```bash
git add src/MacExplorer/Components/FileList/FileGridView.razor
git commit -m "fix: DisposeAsync 清理 _mousedownDebounceTimer"
```

---

### Task 7: 构建验证

**Files:**
- （无文件改动）

- [ ] **Step 1: 构建项目**

```bash
dotnet build src/MacExplorer/MacExplorer.csproj 2>&1 | tail -5
```

预期输出：
```
已成功生成。
    0 个错误
```

- [ ] **Step 2: 确认无编译错误**

如果构建失败，根据错误信息修复后再提交。

---

## 验证清单

构建成功后，在 MacCatalyst 上手动验证：

1. 单击已选中文件 → 约 400ms 后进入重命名模式
2. 快速双击同一文件 → 打开/进入文件夹
3. 快速切换点击 A→B→C → 每次正确选中，不错误打开
4. 新建文件/文件夹 → 自动进入重命名
5. 拖放多选文件 → 正常拖动，不触发重命名
6. 右键菜单 → 正常弹出
7. 点击空白区域 → 取消选中（或提交重命名）
