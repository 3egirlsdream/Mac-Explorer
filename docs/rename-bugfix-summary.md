# FKFinder 重命名功能 BUG 修复总结

## 概述

本次修复了重命名功能的三个 BUG，涉及事件处理时序、平台兼容性和 MVVM 数据绑定刷新问题。

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

将 `OnContentMouseDown` 改为 `async void`，在 `_renamingEntry != null` 时调用 `await CommitRename()` 并立即 return：

```csharp
// 修复后（正确）
if (_renamingEntry != null)
{
    await CommitRename();
    return;
}
```

### 涉及文件

- `src/FKFinder/Components/FileList/FileGridView.razor` — `OnContentMouseDown` 方法

---

## Bug 2：AI 汇总目录中点击重命名输入框会退出重命名模式

### 现象

在 AI 视图（人脸聚类等分类目录）中，右键点击条目选择"重命名"后，输入框正常出现。但当用户点击输入框（如定位光标）时，重命名模式意外退出。

### 根因分析

Rename input 上通过 `RenderTreeBuilder` 添加了 `onmousedown:stopPropagation`：

```csharp
builder.AddAttribute(33, "onmousedown:stopPropagation", true);
```

但 **没有注册显式的 `onmousedown` 事件处理器**。在 macOS Catalyst 的 WKWebView 中，`stopPropagation` 在无显式 handler 时可能未能有效阻止事件冒泡。导致 `mousedown` 事件冒泡到外层 content area，触发 `OnContentMouseDown`，从而取消了重命名。

### 修复方案

采用 **防御性标志位** 机制：

1. 添加 `_renameInputMouseDown` 标志字段
2. 在两处 rename input（列表模式和网格模式）添加 **显式** `onmousedown` handler：
   ```csharp
   builder.AddAttribute(33, "onmousedown", 
       EventCallback.Factory.Create<MouseEventArgs>(this, _ => _renameInputMouseDown = true));
   builder.AddAttribute(34, "onmousedown:stopPropagation", true);
   ```
3. 在 `OnContentMouseDown` 开头检查标志：
   ```csharp
   if (_renameInputMouseDown)
   {
       _renameInputMouseDown = false;
       return;
   }
   ```

### 经验教训

> **Blazor RenderTreeBuilder 中的 `stopPropagation` 在无显式事件 handler 时，可能在某些 WebView 平台上不可靠。** 应同时添加显式 handler 作为防御性备份，确保跨平台兼容。

### 涉及文件

- `src/FKFinder/Components/FileList/FileGridView.razor` — rename input 渲染代码 + `OnContentMouseDown` 方法

---

## Bug 3：AI 分类重命名后文件名不立即刷新显示

### 现象

在 AI 视图中重命名文件或人脸聚类后，底层数据已更新（数据库和内存中的 Entry 都已变更），但 UI 上文件名仍显示旧名称，需要切换视图才能看到新名称。

### 根因分析

AI 视图中的两个重命名方法通过 **索引器赋值** 替换集合中的条目：

```csharp
// RenameEntryAsync (IsAiView 分支)
Entries[i] = new FileSystemEntry { Name = newName, ... };

// RenameFaceClusterAsync
Entries[i] = new FileSystemEntry { Name = name, ... };
```

**问题**：`ObservableCollection` 的索引器赋值 `Entries[i] = newEntry` 虽然会触发 `CollectionChanged` 事件，但 `FileGridView` 的 `OnViewModelPropertyChanged` 只监听 `PropertyChanged(nameof(Entries))` 来触发重渲染。由于没有调用 `OnPropertyChanged(nameof(Entries))`，UI 收不到通知。

普通文件系统视图不受影响，因为它在重命名后调用 `LoadDirectoryContentsAsync` 重设整个集合，会自动触发 PropertyChanged。

### 修复方案

在两个方法中替换 entry 后，各添加一行通知：

```csharp
OnPropertyChanged(nameof(Entries));
```

这与同文件中 `ResolveFaceThumbnailsAsync` 等方法的既有模式一致。

### 经验教训

> **ObservableCollection 索引器赋值（`collection[i] = newItem`）不会触发 PropertyChanged。** 当 UI 依赖 PropertyChanged 而非 CollectionChanged 时，必须手动调用 `OnPropertyChanged(nameof(Entries))`。

### 涉及文件

- `src/FKFinder/ViewModels/FileListViewModel.cs` — `RenameEntryAsync` 和 `RenameFaceClusterAsync` 方法

---

## 附加改动：PIN 文件夹路径同步

在 `RenameEntryAsync` 中补充了 PIN（收藏）文件夹路径同步逻辑，确保重命名文件夹后，侧边栏的收藏夹路径同步更新：

```csharp
if (_pinnedFolderService != null && entry.IsDirectory)
{
    await _pinnedFolderService.UpdateFolderPathAsync(oldPath, newPath, newName);
    await LoadPinnedFoldersAsync();
}
```

该逻辑在 AI 视图分支和普通文件系统分支中均已添加。

---

## 修改文件清单

| 文件 | 修改内容 |
|------|---------|
| `src/FKFinder/Components/FileList/FileGridView.razor` | Bug 1: `OnContentMouseDown` 改为 commit 而非 cancel；Bug 2: rename input 添加显式 mousedown handler + 标志位防御 |
| `src/FKFinder/ViewModels/FileListViewModel.cs` | Bug 3: 添加 `OnPropertyChanged(nameof(Entries))`；附加: PIN 文件夹路径同步 |
