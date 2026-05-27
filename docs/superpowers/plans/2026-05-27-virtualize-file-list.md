# 文件列表虚拟化渲染 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 用 Blazor 原生 `Virtualize<T>` 替换 `@foreach` 全量渲染，只渲染可视区域条目（~25 个），diff 量减少 95%

**Architecture:** 列表视图直接虚拟化条目，网格视图按行虚拟化（Row = Virtualize item，Row 内 flex 排 N 个 grid item），分组视图保持现有 `@foreach` 不变

**Tech Stack:** .NET Blazor Hybrid (MacCatalyst), Masa.Blazor 1.9.*, Blazor Virtualize<T>

---

### Task 1: CSS 调整 — 移除 overflow 并新增 grid row 样式

**Files:**
- Modify: `src/MacExplorer/wwwroot/css/app.css:1117-1146`

- [ ] **Step 1: 修改 `.file-content-scroll` overflow**

找到 `.file-content-scroll`，将 `overflow-y: auto` 改为 `overflow: hidden`：

在行 1117-1122 附近，将：
```css
.file-content-scroll {
    flex: 1;
    overflow-y: auto;
    overflow-x: hidden;
    position: relative;
}
```
改为：
```css
.file-content-scroll {
    flex: 1;
    overflow: hidden;
    position: relative;
}
```

- [ ] **Step 2: 修改 `.file-grid` 和 `.file-list` overflow**

找到 `.file-grid`（行 1136-1146），将 `overflow-y: auto` 移除：

将：
```css
.file-grid {
    padding: var(--space-6) var(--space-8);
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(96px, 1fr));
    gap: var(--space-3);
    align-content: start;
    background: transparent;
    flex: 1;
    overflow-y: auto;
    overflow-x: hidden;
}
```
改为：
```css
.file-grid {
    padding: var(--space-6) var(--space-8);
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(96px, 1fr));
    gap: var(--space-3);
    align-content: start;
    background: transparent;
}
```

找到 `.file-list`（行 1234-1242 附近），移除 `overflow-y: auto` 和 `overflow-x: hidden`：

将：
```css
.file-list {
    ...
    flex: 1;
    overflow-y: auto;
    overflow-x: hidden;
}
```
改为：
```css
.file-list {
    ...
    flex: 1;
}
```

- [ ] **Step 3: 新增 `.file-grid-row` 样式**

在 `.file-grid` 定义之后插入：

```css
.file-grid-row {
    display: flex;
    gap: var(--space-3);
    height: 100px;
    padding: 0 var(--space-8);
}

.file-grid-row > .file-grid-item {
    flex: 1;
    min-width: 0;
}
```

- [ ] **Step 4: Commit**

```bash
git add src/MacExplorer/wwwroot/css/app.css
git commit -m "style: 调整 overflow 为 Virtualize 准备，新增 .file-grid-row"
```

---

### Task 2: Template 改造 — 非分组视图替换 @foreach 为 Virtualize

**Files:**
- Modify: `src/MacExplorer/Components/FileList/FileGridView.razor:49-108`

- [ ] **Step 1: 改造列表视图非分组部分**

找到列表视图的 flat 分支（行 74-80）：

```razor
else
{
    @foreach (var entry in ViewModel.Entries)
    {
        @RenderListRow(entry);
    }
}
```

替换为：

```razor
else
{
    <Virtualize Items="ViewModel.Entries" ItemSize="38">
        <ItemContent>
            @RenderListRow(context)
        </ItemContent>
    </Virtualize>
}
```

- [ ] **Step 2: 改造网格视图非分组部分**

找到网格视图的 flat 分支（行 98-106）：

```razor
else
{
    <div class="file-grid">
        @foreach (var entry in ViewModel.Entries)
        {
            @RenderGridItem(entry);
        }
    </div>
}
```

替换为：

```razor
else
{
    <Virtualize Items="_gridRows" ItemSize="100" Context="row">
        <ItemContent>
            <div class="file-grid-row">
                @foreach (var entry in row)
                {
                    @RenderGridItem(entry)
                }
            </div>
        </ItemContent>
    </Virtualize>
}
```

> **重要:** 此处引用 `_gridRows`，将在 Task 3 中定义。

- [ ] **Step 3: 保留分组视图不变**

分组视图的 `@foreach` 块（`ViewModel.Groups` 的部分）**完全不改**。

- [ ] **Step 4: 保留 `.file-grid` / `.file-list` 外层 div**

保留 `<div class="file-list">` 和 `<div class="file-grid">` 外层 wrapper（其 CSS 有 padding、flex 等样式需要保留），仅移除其 `overflow-y: auto`（已在 Task 1 中处理）。Virtualize 渲染在里面，由 Virtualize 自带滚动容器处理滚动。

- [ ] **Step 5: Commit**

```bash
git add src/MacExplorer/Components/FileList/FileGridView.razor
git commit -m "feat: 非分组视图 @foreach 替换为 Virtualize 虚拟滚动"
```

---

### Task 3: 新增 _gridRows 计算逻辑和 JS interop

**Files:**
- Modify: `src/MacExplorer/Components/FileList/FileGridView.razor` — `@code` 块

- [ ] **Step 1: 添加状态变量**

在 `@code` 块中，添加到状态变量区域：

```csharp
    // ── Virtualize grid state ──
    private List<FileSystemEntry[]> _gridRows = [];
    private int _itemsPerRow = 8; // default, updated on first render
    private bool _gridRowsComputed;
```

- [ ] **Step 2: 添加 ComputeGridRows 方法**

在 `@code` 块中添加：

```csharp
    private void ComputeGridRows(int itemsPerRow)
    {
        if (itemsPerRow <= 0) itemsPerRow = 1;
        _itemsPerRow = itemsPerRow;
        _gridRows = ViewModel.Entries
            .Select((entry, i) => new { entry, i })
            .GroupBy(x => x.i / itemsPerRow)
            .Select(g => g.Select(x => x.entry).ToArray())
            .ToList();
        _gridRowsComputed = true;
    }
```

- [ ] **Step 3: 在 OnAfterRenderAsync 中测量容器宽度**

在 `OnAfterRenderAsync` 中添加（现有方法约 158 行）：

```csharp
protected override async Task OnAfterRenderAsync(bool firstRender)
{
    if (firstRender)
    {
        await JSRuntime.InvokeVoidAsync("fkfinderColumnResize.init");
        await CreateBlobUrlsForEntriesAsync();
    }
    
    // Measure container width for grid virtualization
    if (ViewModel.ViewMode == ViewMode.Grid && ViewModel.GroupField == GroupField.None)
    {
        var width = await JSRuntime.InvokeAsync<double>("fkfinderGrid.getItemWidth");
        if (width > 0)
        {
            var itemsPerRow = Math.Max(1, (int)(width / 108)); // 96px + 12px gap
            if (itemsPerRow != _itemsPerRow || !_gridRowsComputed)
            {
                ComputeGridRows(itemsPerRow);
                StateHasChanged();
            }
        }
    }
    
    if (_needRestoreScrollAfterRename)
    {
        _needRestoreScrollAfterRename = false;
        try { await JSRuntime.InvokeVoidAsync("fkfinderScroll.restoreScroll"); }
        catch { }
    }
}
```

- [ ] **Step 4: 监听 Entries 变化重新计算**

在 `OnViewModelPropertyChanged` 中，当 `Entries` 变化时重新计算：

找到 `Entries` 属性变更处理分支，在调用 `StateHasChanged()` 前添加：

```csharp
// Recompute grid rows when entries change
if (ViewModel.ViewMode == ViewMode.Grid && ViewModel.GroupField == GroupField.None)
{
    ComputeGridRows(_itemsPerRow);
}
```

- [ ] **Step 5: ViewMode 切换时重置**

在 `OnViewModelPropertyChanged` 的 `ViewMode` 分支中，ViewMode 切换到 Grid 时重置 `_gridRowsComputed`：

```csharp
if (e.PropertyName == nameof(ViewModel.ViewMode))
{
    _gridRowsComputed = false;
    // ... existing logic
}
```

- [ ] **Step 6: Commit**

```bash
git add src/MacExplorer/Components/FileList/FileGridView.razor
git commit -m "feat: 添加 _gridRows 计算和容器宽度 JS 测量"
```

---

### Task 4: 新增 JS 测量函数

**Files:**
- Modify: `src/MacExplorer/wwwroot/js/` — 选择合适的已有 JS 文件或新建

- [ ] **Step 1: 创建 grid-measure.js**

创建 `src/MacExplorer/wwwroot/js/grid-measure.js`：

```javascript
window.fkfinderGrid = {
    getItemWidth: function () {
        var el = document.querySelector('.file-content-scroll');
        if (!el) return 0;
        var style = getComputedStyle(el);
        var width = el.clientWidth;
        var paddingLeft = parseFloat(style.paddingLeft) || 0;
        var paddingRight = parseFloat(style.paddingRight) || 0;
        return width - paddingLeft - paddingRight;
    },

    observeResize: function (dotNetRef) {
        var el = document.querySelector('.file-content-scroll');
        if (!el) return;
        if (this._observer) this._observer.disconnect();
        this._observer = new ResizeObserver(function () {
            dotNetRef.invokeMethodAsync('OnContainerResize');
        });
        this._observer.observe(el);
    }
};
```

- [ ] **Step 2: 在 index.html 中引入 JS**

检查 `src/MacExplorer/wwwroot/index.html` 是否已有 JS 引用模式，按相同方式添加 `<script src="js/grid-measure.js"></script>`。

如果不确定引用方式，使用 `grep` 查找现有 JS 引用：
```bash
grep -n "script.*src" src/MacExplorer/wwwroot/index.html
```

- [ ] **Step 3: Commit**

```bash
git add src/MacExplorer/wwwroot/js/grid-measure.js src/MacExplorer/wwwroot/index.html
git commit -m "feat: 添加 grid-measure.js 容器宽度测量"
```

---

### Task 5: 添加 ResizeObserver 回调

**Files:**
- Modify: `src/MacExplorer/Components/FileList/FileGridView.razor`

- [ ] **Step 1: 在 OnAfterRenderAsync 中注册 ResizeObserver**

在 `firstRender` 分支中添加：

```csharp
if (firstRender)
{
    await JSRuntime.InvokeVoidAsync("fkfinderColumnResize.init");
    await CreateBlobUrlsForEntriesAsync();
    // Register resize observer for grid virtualization
    var dotNetRef = DotNetObjectReference.Create(this);
    await JSRuntime.InvokeVoidAsync("fkfinderGrid.observeResize", dotNetRef);
}
```

- [ ] **Step 2: 添加 OnContainerResize 回调方法**

```csharp
[JSInvokable]
public async Task OnContainerResize()
{
    if (ViewModel.ViewMode != ViewMode.Grid || ViewModel.GroupField != GroupField.None) return;
    var width = await JSRuntime.InvokeAsync<double>("fkfinderGrid.getItemWidth");
    if (width > 0)
    {
        var itemsPerRow = Math.Max(1, (int)(width / 108));
        if (itemsPerRow != _itemsPerRow)
        {
            ComputeGridRows(itemsPerRow);
            StateHasChanged();
        }
    }
}
```

- [ ] **Step 3: 在 DisposeAsync 中清理**

在 `DisposeAsync` 中添加 ResizeObserver 断开：

```csharp
try { await JSRuntime.InvokeVoidAsync("fkfinderGrid.disconnect"); } catch { }
```

- [ ] **Step 4: Commit**

```bash
git add src/MacExplorer/Components/FileList/FileGridView.razor
git commit -m "feat: 添加 ResizeObserver 回调动态更新网格列数"
```

---

### Task 6: 构建验证

- [ ] **Step 1: 构建项目**

```bash
dotnet build src/MacExplorer/MacExplorer.csproj 2>&1 | grep -E "错误|error|生成"
```

预期输出：
```
已成功生成。
    0 个错误
```

- [ ] **Step 2: 如构建失败，根据错误修复后再提交**

---

## 验证清单

构建成功后，在 MacCatalyst 上手动验证：

1. 大目录（500+ 文件）列表视图 → 点击切换文件不卡顿
2. 大目录（500+ 文件）网格视图 → 点击切换文件不卡顿
3. 双击文件夹 → 正常进入
4. 单击已选中文件 → 进入重命名模式
5. 重命名提交 → 列表刷新，滚动位置保持
6. 拖放文件 → 正常拖动
7. 右键菜单 → 正常弹出
8. 窗口 resize → 网格列数重新计算
9. 分组视图（按类型/日期） → 正常渲染（保持原有行为）
10. 空目录 → 显示空状态提示
