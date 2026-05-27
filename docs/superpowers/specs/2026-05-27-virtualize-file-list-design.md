# 文件列表虚拟化渲染：Blazor Virtualize 替换全量 foreach

日期: 2026-05-27 | 状态: 已批准

## 问题

大目录（500+ 文件）下点击切换文件和右键菜单有明显卡顿。每次 `StateHasChanged` 触发全量 Blazor 渲染，对 500+ 个复杂条目（SVG 图标、拖放属性、10+ 事件处理器、星级评分）做 diff。

## 根因

当前 `FileGridView.razor` 用 `@foreach` 渲染全部 `ViewModel.Entries`。每次选择变更 → `CollectionChanged` → `StateHasChanged` → Blazor diff 遍历所有条目节点 → 即使只有 1 个条目改变了 `selected` CSS 类。

MDataTable 没有虚拟化（只有分页），不适合文件管理器的无限滚动体验。

## 方案: Blazor 原生 Virtualize<T>

用 `<Virtualize>` 替换 `@foreach`，只渲染可视区域条目（约 20-30 个），将每次点击的 diff 量减少 95%。

### 架构

```
列表视图 (ViewMode.List):
  <Virtualize Items="ViewModel.Entries" ItemSize="38">
    <ItemContent>
      @RenderListRow(context)  ← 现有模板不变
    </ItemContent>
  </Virtualize>

网格视图 (ViewMode.Grid):
  <Virtualize Items="_gridRows" ItemSize="100" Context="row">
    <ItemContent>
      <div class="file-grid-row">
        @foreach (var entry in row) { @RenderGridItem(entry) }
      </div>
    </ItemContent>
  </Virtualize>

分组视图 (GroupField != None):
  保持当前 @foreach 实现（本期不虚拟化）
```

### 网格按行虚拟化

Blazor Virtualize 用绝对定位放置条目，不支持 CSS Grid 的 `auto-fill` 流式布局。因此以"行"为单位虚拟化：每个 virtual item 是一行，行内用 flex 水平排列 N 个网格项。

```
itemsPerRow = containerWidth / (96px + gap)
_gridRows = Entries.Chunk(itemsPerRow)
```

`itemsPerRow` 在 `OnAfterRenderAsync` 中通过 JS interop 测量容器宽度计算。窗口 resize 时更新。

### 渲染模板复用

`RenderListRow` 和 `RenderGridItem` 两个 `RenderFragment<FileSystemEntry>` 完全不动。选择、拖放、重命名、图标、星级评分全部保持不变。

### ItemSize

| 视图 | 值 | 来源 |
|------|-----|------|
| 列表 | 38px | `.file-list-row` min-height 34px + margin 4px |
| 网格 | 100px | `.file-grid-item` min-height 96px + padding |

### CSS 改动

- `.file-grid` / `.file-list`: 移除 `overflow-y: auto`，改为 `overflow: hidden`（Virtualize 自带滚动容器）
- 新增 `.file-grid-row`: `display: flex; gap: var(--space-3); height: 100px;`

### 边界情况

| 场景 | 处理 |
|------|------|
| 重命名后刷新 | `fkfinderScroll.saveScroll/restoreScroll` 保持位置（已有逻辑） |
| 重命名中输入框 | 重命名条目在可视区域，Virtualize 不会回收 |
| 拖放操作 | `draggable`/`ondragstart` 等属性在模板中，不受影响 |
| 右键菜单 | 右键事件在模板中，不受影响 |
| 容器 resize | `ResizeObserver` → JS interop → 重新计算 `_gridRows` |
| 分组视图 | 保持现有实现，不虚拟化 |
| entries 为空 | Virtualize 渲染空列表，空状态提示在外层 |

### 文件改动

| 文件 | 改动类型 |
|------|---------|
| `FileGridView.razor` | 核心改动：替换 `@foreach` 为 `<Virtualize>`，新增 `_gridRows`/`_itemsPerRow` 计算逻辑，JS interop 测量容器宽度 |
| `app.css` | 小改：`.file-grid`/`.file-list` overflow 调整，新增 `.file-grid-row` |
| `wwwroot/js/` | 可能需要新增 resize observer 回调（如现有 fkfinder JS 无此功能） |

### 不影响的部分

- `FileListViewModel.cs` — 无需改动
- `RenderListRow` / `RenderGridItem` 模板 — 完全不动
- 分组视图渲染 — 不变
- 文件列表头 — 在 Virtualize 外，不变
- 状态栏 — 不变

## 风险

1. Virtualize 对 `Items` 做引用比较，重命名/刷新后 `Entries` 是新集合 → Virtualize 会重置滚动位置。`CommitRename` 已有的 `fkfinderScroll` 机制可缓解。
2. 网格 `itemsPerRow` 需要容器宽度，首次渲染时可能还未测量到 → 使用默认值 6，测量后更新。
3. 拖放到 Virtualize 的空区域会触发 `OnContentDrop`，需要在 Virtualize 容器上也绑定 drop 事件。

## 验证清单

1. 大目录（500+ 文件）列表视图 → 点击切换文件不卡顿
2. 大目录（500+ 文件）网格视图 → 点击切换文件不卡顿
3. 双击文件夹 → 正常进入
4. 单击已选中文件 → 进入重命名
5. 重命名提交 → 列表刷新，滚动位置保持
6. 拖放文件 → 正常拖动
7. 右键菜单 → 正常弹出，不卡顿
8. 窗口 resize → 网格重新计算列数
9. 分组视图（按类型/日期） → 正常渲染
10. 空目录 → 显示空状态提示
