# MacExplorer 统一文件列表刷新机制

## 概述

MacExplorer 的文件列表刷新存在系统性问题：拖拽后偶尔不刷新，跨窗口操作不同步，外部变更无感知。根本原因是没有统一的刷新管线——每种操作各自实现刷新逻辑，互不关联。

本方案设计了一个**四层统一刷新管线**，将"全局刷新、跨窗口刷新、跨组件刷新、外部变更刷新"纳入同一管线，所有变更源（内部操作 / 拖拽 / 外部应用）都通过同一条路径传递到所有需要更新的 UI 组件。

---

## 四层刷新管线架构

```
═══════════════════════════════════════════════════════════════
 Layer 1: 变更源 (Change Sources)
═══════════════════════════════════════════════════════════════

 ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  ┌───────────────┐
 │ 内部文件操作  │  │ 拖拽操作     │  │ FSEvents     │  │ 未来扩展      │
 │ 粘贴/删除/   │  │ 外部拖入     │  │ 外部应用变更  │  │ iCloud/网络盘 │
 │ 移动/重命名   │  │ 跨窗口拖拽   │  │              │  │              │
 │ 创建文件/夹   │  │ 内部拖拽     │  │              │  │              │
 └──────┬───────┘  └──────┬───────┘  └──────┬───────┘  └──────┬───────┘
        │                 │                  │                 │
        └─────────────────┴──────────────────┴─────────────────┘
                                   │
                    NotifyChanged(directories[])
                                   ▼
═══════════════════════════════════════════════════════════════
 Layer 2: 变更路由 (IDirectoryChangeNotifier - Singleton)
═══════════════════════════════════════════════════════════════

   ┌─────────────────────────────────────────────────┐
   │  收集受影响的目录路径                             │
   │  200ms 防抖合并                                  │
   │  遍历所有注册的 ViewModel                        │
   │  匹配 CurrentPath → 调度到主线程 → 触发刷新      │
   └──────────────────────┬──────────────────────────┘
                          │
          ┌───────────────┼───────────────┐
          ▼               ▼               ▼
═══════════════════════════════════════════════════════════════
 Layer 3: ViewModel 刷新 (Scoped, 每窗口一个)
═══════════════════════════════════════════════════════════════

  ┌─────────────┐   ┌─────────────┐   ┌─────────────┐
  │  VM 窗口A   │   │  VM 窗口B   │   │  VM 窗口C   │
  │             │   │             │   │             │
  │ LoadDir()   │   │ LoadDir()   │   │ LoadDir()   │
  │ → Entries ✓ │   │ → Entries ✓ │   │ → Entries ✓ │
  │ → Status ✓  │   │ → Status ✓  │   │ → Status ✓  │
  └──────┬──────┘   └──────┬──────┘   └──────┬──────┘
         │                 │                  │
  PropertyChanged    PropertyChanged   PropertyChanged
  ("Entries" etc.)   ("Entries" etc.)  ("Entries" etc.)
         │                 │                  │
         ▼                 ▼                  ▼
═══════════════════════════════════════════════════════════════
 Layer 4: Blazor 组件响应 (每窗口内多组件)
═══════════════════════════════════════════════════════════════

  ┌──────────────────────────────────────────────┐
  │ FileGridView  ← Entries 变更 → 重渲染文件列表 │
  │ PreviewPane   ← Entries 变更 → 验证选中文件   │
  │ FinderSidebar ← PinnedFolders → 更新侧边栏   │
  │ BreadcrumbBar ← CurrentPath  → 更新面包屑    │
  │ FinderToolbar ← (参数驱动)   → 按钮状态      │
  └──────────────────────────────────────────────┘
```

---

## 修复的问题

### Bug 1: 拖拽广播导致竞态条件

**原因**：`MacDragDropBridge.ExternalDropReceived` 事件广播给**所有**窗口的 ViewModel。多个 VM 竞争执行 `MoveAsync`，第一个成功后其余 `moved == 0` → 跳过刷新。

**修复**：文件移动逻辑从 ViewModel 迁移到 Bridge，Bridge 自行执行 `MoveAsync` 后通过 `IDirectoryChangeNotifier` 统一通知，不再广播事件。

### Bug 2: 事件泄漏

**原因**：ViewModel 构造函数订阅 `ExternalDropReceived`，但 `Unregister` 只移除列表引用，不取消事件订阅。

**修复**：删除了 `ExternalDropReceived` 事件和 ViewModel 中的事件订阅。

### Bug 3: 跨窗口操作无通知

**原因**：`PasteAsync`/`DeleteSelectedAsync` 等操作只调用自身 `LoadDirectoryContentsAsync`，不通知其他窗口。

**修复**：所有文件操作（粘贴、删除、移动、重命名、创建）在本地刷新后调用 `NotifyChanged` 通知其他窗口。

### Bug 4: 跨组件刷新不完整

**原因**：`PreviewPane` 在 `Entries` 变更后未验证选中文件是否仍存在，可能显示已删除文件的预览。

**修复**：PreviewPane 在 Entries 变更时检查 `_selectedEntry` 是否仍在列表中，不存在则清空预览。

### Bug 5: 无外部变更感知

**原因**：没有 FSEvents 监听，外部应用修改文件后 MacExplorer 不更新。

**修复**：新增 `MacFSEventsWatcher` 通过 CoreServices P/Invoke 监听当前浏览目录，外部变更自动通过 Notifier 刷新。

---

## 两种刷新策略与防双重刷新

根据变更源的不同，刷新走两条路径：

### 路径 A — 内部操作（ViewModel 发起）

即时本地刷新 + Notifier 跨窗口通知：

```
PasteAsync / DeleteAsync / MoveAsync / ...
  → 本地 LoadDirectoryContentsAsync(forceRefresh: true)  ← 即时，0ms
  → NotifyChanged(dirs, excludeVm: this)                 ← 通知其他窗口，200ms 防抖
```

本窗口即时刷新（保证 scroll-to-selected 等 UX），`excludeVm: this` 防止 Notifier 对本窗口二次刷新。

### 路径 B — Bridge 发起的操作（拖拽）

仅 Notifier 通知：

```
MacDragDropBridge.HandleExternalDrop
  → 执行 MoveAsync（Bridge 中，不在任何 ViewModel 中）
  → NotifyChanged(dirs)                                  ← 所有匹配窗口，200ms 防抖
```

文件移动逻辑在 Bridge 中执行（不属于任何 ViewModel），所以没有"本地刷新"。所有窗口（包括拖拽目标窗口）统一通过 Notifier 刷新。**不传 excludeVm**，因为没有 VM 已自行刷新。

**为什么不会双重刷新？** ViewModel 中原有的 `HandleExternalDrop` 方法（含本地刷新 + 事件订阅）已删除。拖拽场景下唯一的刷新路径就是 Notifier，200ms 防抖延迟对拖拽 UX 完全可接受。

---

## 各场景信号流向

| 场景 | Layer 1 变更源 | Layer 2 路由 | Layer 3 VM 刷新 | Layer 4 组件响应 |
|------|---------------|-------------|----------------|-----------------|
| **单窗口粘贴** | PasteAsync → NotifyChanged([CurrentPath], this) | Notifier 无匹配（excludeVm=this） | 本地已即时刷新 | FileGridView 重渲染 |
| **跨窗口粘贴** | PasteAsync → NotifyChanged([CurrentPath, sourceDirs], this) | 通知查看 sourceDir 的其他 VM | 其他窗口 RefreshFromNotification | 其他窗口 FileGridView 重渲染 |
| **外部拖入** | MacDragDropBridge.HandleExternalDrop → NotifyChanged([target, sources]) | 通知所有匹配的 VM | 所有匹配窗口 RefreshFromNotification | FileGridView + PreviewPane 重渲染 |
| **跨窗口拖拽** | 同上（Bridge 执行移动+通知） | 源窗口 + 目标窗口都收到 | 两个窗口都刷新 | 两个窗口 FileGridView 重渲染 |
| **内部拖拽** | MoveEntriesAsync → NotifyChanged([CurrentPath, target], this) | 通知查看 target 的其他 VM | 本地即时刷新 + 其他窗口被动刷新 | 本地 FileGridView 重渲染 |
| **Terminal 创建文件** | FSEvents 回调 → NotifyChanged([dir]) | 通知查看该 dir 的所有 VM | 所有匹配窗口 RefreshFromNotification | FileGridView 重渲染 |
| **删除已预览文件** | DeleteSelectedAsync → NotifyChanged + 本地刷新 | 同上 | Entries 变更 | PreviewPane 检测文件消失 → 清空预览 |

---

## 文件清单

### 新建文件 (4个)

| 文件 | 作用 |
|------|------|
| `src/MacExplorer/Services/IDirectoryChangeNotifier.cs` | Layer 2 统一变更路由接口：`NotifyChanged` / `Subscribe` / `Unsubscribe` |
| `src/MacExplorer/Services/Impl/DirectoryChangeNotifier.cs` | 实现：WeakReference VM 注册 + 200ms 防抖 + MainThread 分发 |
| `src/MacExplorer/Services/IFSEventsWatcher.cs` | FSEvents 监听接口：引用计数的 `WatchDirectory` / `UnwatchDirectory` |
| `src/MacExplorer/Platforms/MacCatalyst/Services/MacFSEventsWatcher.cs` | CoreServices P/Invoke 实现：FSEventStream 自动重建、静态委托防 GC |

### 修改文件 (8个)

| 文件 | 核心变更 |
|------|---------|
| `src/MacExplorer/ViewModels/FileListViewModel.cs` | +`RefreshFromNotification()`, 7个操作加 `NotifyChanged`, 删除 `HandleExternalDrop`, NavigateToAsync/GoHome 加 FSEvents watch/unwatch, 构造函数新增 2 参数 |
| `src/MacExplorer/Services/IDragDropBridge.cs` | 删除 `ExternalDropReceived` 事件和 `NotifyExternalDrop`, 新增 `HandleExternalDrop(paths, target, nsWindow)` |
| `src/MacExplorer/Platforms/MacCatalyst/Services/MacDragDropBridge.cs` | 构造函数注入 `IFileService` + `IDirectoryChangeNotifier`, Bridge 自行执行 MoveAsync + Notifier 通知, 删除事件广播和 `RefreshSourceDirectories` |
| `src/MacExplorer/Platforms/MacCatalyst/Handlers/DropOverlayHelper.cs` | `ResolveTargetAndNotify` 中 `NotifyExternalDrop` → `HandleExternalDrop`（两处，传 nsWindow） |
| `src/MacExplorer/Platforms/MacCatalyst/Handlers/NativeDragDropHelper.cs` | `FileDropMessageHandler` 中 `NotifyExternalDrop` → `HandleExternalDrop`（附加 nsWindow 查找） |
| `src/MacExplorer/Components/Preview/PreviewPane.razor` | Entries 变更时验证选中文件是否仍存在，不存在则清空预览 |
| `src/MacExplorer/Components/Pages/Home.razor` | 注入 `IDirectoryChangeNotifier` + `IFSEventsWatcher`, OnInitialized 加 `Subscribe`, DisposeAsync 加 `Unsubscribe` + `UnwatchDirectory`, `selectAll` 改用 `InvokeAsync(StateHasChanged)` |
| `src/MacExplorer/MauiProgram.cs` | 注册 `IDirectoryChangeNotifier` / `IFSEventsWatcher` / 修改 `IDragDropBridge` 注册（工厂注入依赖）, FileListViewModel 工厂增加 2 个参数 |

---

## 核心接口

### IDirectoryChangeNotifier

```csharp
public interface IDirectoryChangeNotifier
{
    void NotifyChanged(string[] directoryPaths, FileListViewModel? excludeVm = null);
    void Subscribe(FileListViewModel vm);
    void Unsubscribe(FileListViewModel vm);
}
```

### IDragDropBridge (修改后)

```csharp
public interface IDragDropBridge
{
    void Register(FileListViewModel vm);
    void SetActive(FileListViewModel vm);
    void Unregister(FileListViewModel vm);
    string[] GetDragFilePaths();
    string GetCurrentDirectory();
    string GetCurrentDirectoryForWindow(string windowId);
    void HandleExternalDrop(string[] sourcePaths, string targetDirectory, IntPtr nsWindow);
}
```

### IFSEventsWatcher

```csharp
public interface IFSEventsWatcher
{
    void WatchDirectory(string path);
    void UnwatchDirectory(string path);
}
```

---

## DirectoryChangeNotifier 核心实现

- `_viewModels`: `List<WeakReference<FileListViewModel>>`，lock 保护
- `_pendingChanges`: `HashSet<string>` 累积待刷新目录路径
- `_excludedVms`: `HashSet<FileListViewModel>` 记录已自行刷新的 VM
- `_debounceTimer`: `System.Threading.Timer`，200ms 超时
- `NotifyChanged()`:
  1. lock 加入目录到 `_pendingChanges`，记录 `excludeVm`
  2. 重置 timer（每次调用都延后 200ms）
- Timer 回调:
  1. lock 快照 `_pendingChanges` + `_excludedVms` 并清空
  2. `MainThread.BeginInvokeOnMainThread` 在主线程执行
  3. 遍历所有 VM（清理已 GC 的 WeakReference）
  4. 跳过：`IsHomePage` / `IsArchiveView` / `IsAiView` / `IsCollectionView`
  5. 跳过：`CurrentPath` 不在待刷新集合中
  6. 跳过：VM 在 `_excludedVms` 中
  7. 调用 `vm.RefreshFromNotification()`，catch 异常防一窗口出错影响其他窗口

---

## MacFSEventsWatcher 核心实现

- `_watchedPaths`: `Dictionary<string, int>` 引用计数（两个窗口看同一目录 → 计数 2）
- `_stream`: 当前 FSEventStream 句柄
- `WatchDirectory(path)` → 引用计数 +1 → 如果是新路径则重建 stream
- `UnwatchDirectory(path)` → 引用计数 -1 → 归零时移除并重建 stream
- 重建流程：Stop → Invalidate → Release 旧 stream → Create 新 stream → Schedule → Start
- FSEvents 回调：提取变更文件的父目录 → 过滤为正在监听的目录 → `_notifier.NotifyChanged(dirs)`
- `FSEventStreamCreate` 参数：latency = 0.5s，flags = `kFSEventStreamCreateFlagFileEvents | kFSEventStreamCreateFlagNoDefer`
- 调度到 `CFRunLoopGetMain()`（主线程 RunLoop）
- 静态委托防 GC（与 DropOverlayHelper 相同模式）

---

## 边界情况

| 场景 | 处理方式 |
|------|---------|
| 快速连续拖拽 | 200ms 防抖合并为 1 次刷新 |
| VM 已被 GC | WeakReference.TryGetTarget 返回 false → 跳过 + 清理列表 |
| 导航离开后收到旧通知 | RefreshFromNotification 检查 CurrentPath + 当前视图模式 |
| 两个窗口查看同一目录 | 两个 VM 都收到通知，都刷新 |
| 跨卷移动（后台任务） | TaskManager.CompleteTask 回调中调用 NotifyChanged |
| FSEvents + 内部操作重叠 | 内部操作传 excludeVm=this 避免自身双重刷新；FSEvents 通知也到达但防抖合并 |
| 特殊视图（Archive/AI/Home） | RefreshFromNotification 跳过；FSEvents 不 watch |
| FSEvents 批量事件 | FSEvents latency=0.5s 已预聚合 + Notifier 200ms 防抖 |

---

## 验证步骤

1. **编译**：`dotnet build src/MacExplorer` 确保无错误
2. **单窗口拖入**：从 Finder 拖文件到 MacExplorer → 列表刷新 → 连续拖 3 次均刷新
3. **跨窗口拖拽**：A 拖文件到 B → B 显示新文件 + A 文件消失
4. **内部拖拽**：同窗口文件拖到子文件夹 → 文件从列表消失
5. **跨窗口粘贴**：A 剪切 → B 粘贴 → A 文件消失 + B 显示新文件
6. **外部变更**：Terminal 执行 `touch /viewed/dir/new.txt` → MacExplorer ~1s 内显示
7. **同目录多窗口**：A 和 B 查看同目录 → A 删除文件 → B 也刷新
8. **预览面板**：选中文件 → 在另一窗口删除该文件 → 预览自动清空
9. **防抖**：快速操作 → 日志确认合并刷新
