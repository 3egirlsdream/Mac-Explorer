# 面包屑导航逻辑重构方案

## 问题描述

打开压缩文件路径之后，无法点击面包屑回到上一层。问题的根本原因是：

1. `FileListViewModel.NavigateToArchiveAsync` 没有正确设置 `_navigation.IsArchiveView = true`
2. 也没有设置 `_navigation.CurrentArchivePath` 和 `_navigation.CurrentArchiveInternalPath`
3. 这导致 `NavigationViewModel.UpdateBreadcrumbs` 无法正确生成压缩文件视图的面包屑

## 架构目标

统一面包屑逻辑，由 `NavigationViewModel` 集中管理：
- 面包屑生成
- 视图状态管理（IsArchiveView, IsAiView, IsCollectionView）
- 导航历史记录

## 修改内容

### 1. NavigationViewModel.cs

#### 1.1 NavigateToAsync 方法增强

在导航到普通文件系统路径时，自动重置特殊视图标志位：

```csharp
[RelayCommand]
public async Task NavigateToAsync(string path)
{
    if (string.IsNullOrWhiteSpace(path)) return;

    // Handle archive sentinel paths - delegate to specialized handler
    if (ArchivePathHelper.IsArchivePath(path))
    {
        // Archive navigation is handled by FileListViewModel.NavigateToArchiveAsync
        return;
    }

    // Handle AI sentinel paths
    if (AiPathHelper.IsAiPath(path))
    {
        // AI navigation is handled by FileListViewModel.HandleAiNavigationAsync
        return;
    }

    // Validate that the path exists on the filesystem
    if (path != _fileService.TrashDirectory && !Directory.Exists(path))
    {
        return;
    }

    if (CurrentPath == path) return;

    // When navigating to a normal filesystem path from a special view,
    // reset the special view flags
    if (IsArchiveView || IsAiView || IsCollectionView)
    {
        IsArchiveView = false;
        IsAiView = false;
        IsCollectionView = false;
        CurrentArchivePath = null;
        CurrentArchiveInternalPath = "";
        CurrentCollectionId = null;
        CurrentCollectionName = null;
    }

    // ... rest of the method
}
```

### 2. FileListViewModel.cs

#### 2.1 NavigateToArchiveAsync 方法修复

添加缺失的视图状态设置：

```csharp
private async Task NavigateToArchiveAsync(string sentinelPath)
{
    _navigation.IsHomePage = false;
    _navigation.IsCollectionView = false;
    _navigation.IsArchiveView = true;        // 新增
    _navigation.IsAiView = false;            // 新增
    _ai.Reset();
    _navigation.IsSearchMode = false;

    var (archivePath, internalPath) = ArchivePathHelper.Parse(sentinelPath);
    _navigation.CurrentArchivePath = archivePath;           // 新增
    _navigation.CurrentArchiveInternalPath = internalPath;  // 新增

    await _archive.NavigateToArchiveAsync(
        archivePath,
        internalPath,
        path => { _navigation.CurrentPath = path; },
        () => _navigation.UpdateBreadcrumbsForArchive(),
        entries => ApplyEntries(entries),
        msg => StatusText = msg,
        loading => IsLoading = loading
    );

    _navigation.UpdateHistoryForSentinelPath(sentinelPath);
    _navigation.WatchCurrentDirectory();
}
```

#### 2.2 HandleAiNavigationAsync 方法修复

添加缺失的视图状态设置：

```csharp
private async Task HandleAiNavigationAsync(string sentinelPath)
{
    _navigation.IsHomePage = false;
    _navigation.IsCollectionView = false;
    _navigation.IsArchiveView = false;
    _navigation.IsAiView = true;             // 新增
    _navigation.IsSearchMode = false;

    await _ai.HandleAiNavigationAsync(
        // ... parameters
    );

    _navigation.UpdateHistoryForSentinelPath(sentinelPath);
}
```

## 面包屑逻辑架构

### 职责划分

| 组件 | 职责 |
|------|------|
| **NavigationViewModel** | 面包屑生成、视图状态管理、导航历史 |
| **FileListViewModel** | 路径分发、内容加载、协调子视图模型 |
| **BreadcrumbBar.razor** | 面包屑渲染、点击处理、路径输入编辑 |

### 面包屑生成逻辑

`NavigationViewModel.UpdateBreadcrumbs` 根据当前视图状态生成面包屑：

1. **收藏夹视图** (`IsCollectionView`): 显示 "收藏夹 > 收藏夹名称"
2. **压缩文件视图** (`IsArchiveView`): 显示完整路径层级，包括外部文件系统路径和压缩文件内部路径
3. **AI 视图** (`IsAiView`): 由 AiViewModel 单独处理
4. **普通文件系统**: 显示标准路径层级

### 导航流程

当用户点击面包屑时：

1. **BreadcrumbBar** 调用 `ViewModel.NavigateToCommand.ExecuteAsync(path)`
2. **FileListViewModel.NavigateToAsync** 根据路径类型分发：
   - 压缩文件哨兵路径 -> `NavigateToArchiveAsync`
   - AI 哨兵路径 -> `HandleAiNavigationAsync`
   - 普通路径 -> 调用 `NavigationViewModel.NavigateToAsync`（内部负责重置特殊视图标志 + 更新历史 + 更新面包屑）
3. **NavigationViewModel.NavigateToAsync** 更新历史记录和面包屑

---

## 代码审查：已发现的问题与修复方案

### P0 级别（必须立即修复）

#### BUG-1: LoadDirectoryContentsAsync 变成 fire-and-forget

**位置**: `FileListViewModel.NavigateToAsync`（第 304 行）

**问题**: 重构后将 `await LoadDirectoryContentsAsync()` 改为了 `_ = LoadDirectoryContentsAsync()`，并在其后立即执行 `IsLoading = false`。原来的实现是在 try-finally 中 await 完成后才重置 IsLoading。

**影响**: 加载指示器一闪而过，用户感知不到加载状态；如果加载抛异常也无法捕获。

**修复**:

```csharp
// FileListViewModel.NavigateToAsync - 修复后
[RelayCommand]
public async Task NavigateToAsync(string path)
{
    // ... 前面的校验和状态设置不变 ...

    _navigation.IsHomePage = false;
    var oldPath = _navigation.CurrentPath;
    await _navigation.NavigateToAsync(path);

    try
    {
        await LoadDirectoryContentsAsync();      // 改回 await
    }
    catch (Exception ex)
    {
        StatusText = $"无法访问: {ex.Message}";   // 恢复异常处理
    }
    finally
    {
        IsLoading = false;                        // 移到 finally 中
    }

    _navigation.UnwatchCurrentDirectory(oldPath);
    _navigation.WatchCurrentDirectory();
}
```

#### BUG-2: 后退/前进到 Archive/AI 路径时 CurrentPath 未更新

**位置**: `FileListViewModel.NavigateBackAsync` / `NavigateForwardAsync`（第 349-385 行）

**问题**: `_navigation.NavigateBackAsync()` 内部调用 `NavigateToAsync(_historyStack[_historyIndex])`，但 `NavigateToAsync` 遇到 archive/AI 哨兵路径直接 return，不更新 `CurrentPath`。随后 `FileListViewModel` 用 `_navigation.CurrentPath`（仍是旧值）来判断路径类型，导致判断失败。

**修复**: 在 `NavigationViewModel` 的 `NavigateBackAsync` / `NavigateForwardAsync` 中，不经过 `NavigateToAsync`，直接更新 `CurrentPath`：

```csharp
// NavigationViewModel - 修复后
[RelayCommand]
public async Task NavigateBackAsync()
{
    if (!CanGoBack) return;
    _historyIndex--;
    _isNavigatingHistory = true;
    try
    {
        var targetPath = _historyStack[_historyIndex];
        // 对所有路径类型都更新 CurrentPath，不走 NavigateToAsync
        CurrentPath = targetPath;
    }
    finally
    {
        _isNavigatingHistory = false;
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }
}

[RelayCommand]
public async Task NavigateForwardAsync()
{
    if (!CanGoForward) return;
    _historyIndex++;
    _isNavigatingHistory = true;
    try
    {
        var targetPath = _historyStack[_historyIndex];
        CurrentPath = targetPath;
    }
    finally
    {
        _isNavigatingHistory = false;
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }
}
```

`FileListViewModel` 侧保持不变，继续根据 `_navigation.CurrentPath` 的路径类型分发到对应的加载方法。

#### BUG-3: 标志位提前清零导致 NavigationViewModel 内部清理被跳过

**位置**: `FileListViewModel.NavigateToAsync`（第 273-278 行）

**问题**: `FileListViewModel` 在调用 `_navigation.NavigateToAsync(path)` 之前就把 `_navigation.IsArchiveView` / `IsAiView` / `IsCollectionView` 全部设为 false。`NavigationViewModel.NavigateToAsync` 内部的条件判断 `if (IsArchiveView || IsAiView || IsCollectionView)` 永远为 false，导致 `CurrentArchivePath`、`CurrentCollectionId`、`CurrentCollectionName` 等关联状态残留。

**修复**: 删除 `FileListViewModel` 中的提前清零，让 `NavigationViewModel.NavigateToAsync` 内部统一处理：

```csharp
// FileListViewModel.NavigateToAsync - 修复后
[RelayCommand]
public async Task NavigateToAsync(string path)
{
    if (string.IsNullOrWhiteSpace(path)) return;

    if (ArchivePathHelper.IsArchivePath(path))
    {
        await NavigateToArchiveAsync(path);
        return;
    }

    if (AiPathHelper.IsAiPath(path))
    {
        await HandleAiNavigationAsync(path);
        return;
    }

    // 只重置 AI 运行时状态，不动导航标志位
    _ai.Reset();

    if (string.IsNullOrEmpty(PendingSelectFileName))
        ScrollBehaviorAfterLoad = ScrollMode.ResetToTop;

    if (path != _fileService.TrashDirectory && !Directory.Exists(path))
    {
        StatusText = $"路径不存在: {path}";
        return;
    }

    if (_navigation.CurrentPath == path && Entries.Count > 0) return;

    IsLoading = true;
    if (IsMetadataPanelVisible) CloseMetadata();
    _navigation.IsHomePage = false;

    // 删除: 不再在这里提前设置 _navigation.IsAiView = false 等
    // NavigationViewModel.NavigateToAsync 内部会统一处理标志位重置

    var oldPath = _navigation.CurrentPath;
    await _navigation.NavigateToAsync(path);

    try
    {
        await LoadDirectoryContentsAsync();
    }
    catch (Exception ex) { StatusText = $"无法访问: {ex.Message}"; }
    finally { IsLoading = false; }

    _navigation.UnwatchCurrentDirectory(oldPath);
    _navigation.WatchCurrentDirectory();
}
```

同时需要在 `NavigationViewModel.NavigateToAsync` 的重置块中补充 AI 相关状态的清理：

```csharp
// NavigationViewModel.NavigateToAsync 内部 - 补充清理
if (IsArchiveView || IsAiView || IsCollectionView)
{
    IsArchiveView = false;
    IsAiView = false;
    IsCollectionView = false;
    CurrentArchivePath = null;
    CurrentArchiveInternalPath = "";
    CurrentCollectionId = null;
    CurrentCollectionName = null;
    CurrentFaceClusterId = null;       // 补充
    CurrentAiContextLabel = null;      // 补充
}
```

### P1 级别（需尽快修复）

#### BUG-4: ArchiveViewModel 与 NavigationViewModel 状态重复存储

**位置**: `ArchiveViewModel.cs`（第 17-23 行）、`NavigationViewModel.cs`（第 37-43 行）

**问题**: `IsArchiveView`、`CurrentArchivePath`、`CurrentArchiveInternalPath` 在两个 ViewModel 中各存一份。`NavigateToArchiveAsync` 会同时设置两方，但 Reset 或其他路径可能只重置一方。

**修复**: 删除 `ArchiveViewModel` 中的 `IsArchiveView`、`CurrentArchivePath`、`CurrentArchiveInternalPath` 属性，统一使用 `NavigationViewModel` 上的。`ArchiveViewModel.NavigateToArchiveAsync` 不再自行维护这些状态，改为接收参数：

```csharp
// ArchiveViewModel - 修复后，删除重复属性
public partial class ArchiveViewModel : ObservableObject
{
    // 删除: IsArchiveView, CurrentArchivePath, CurrentArchiveInternalPath
    // 这些状态统一由 NavigationViewModel 管理

    [ObservableProperty]
    private bool _isCompressDialogVisible;

    [ObservableProperty]
    private CompressOptions? _pendingCompressOptions;

    [ObservableProperty]
    private string? _activeTaskId;

    public async Task NavigateToArchiveAsync(
        string archivePath,
        string internalPath,
        Action<string> setCurrentPath,
        Action updateBreadcrumbs,
        Action<IReadOnlyList<FileSystemEntry>> applyEntries,
        Action<string> setStatus,
        Action<bool> setLoading)
    {
        if (_archiveService == null) return;
        // 不再设置 IsArchiveView / CurrentArchivePath 等
        setCurrentPath(ArchivePathHelper.Build(archivePath, internalPath));
        updateBreadcrumbs();
        setLoading(true);
        try
        {
            var entries = await _archiveService.GetArchiveContentsAsync(archivePath, internalPath);
            applyEntries(entries);
        }
        catch (Exception ex) { setStatus($"无法打开归档: {ex.Message}"); }
        finally { setLoading(false); }
    }

    // ShowCompressDialog 中 IsArchiveView 判断改为参数传入
    public void ShowCompressDialog(
        IReadOnlyList<FileSystemEntry> selectedEntries,
        FileSystemEntry? contextMenuEntry,
        string currentPath,
        bool isCollectionView,
        bool isArchiveView,        // 从外部传入
        int? currentCollectionId)
    { ... }
}
```

#### BUG-5: PropertyChanged 转发不完整

**位置**: `FileListViewModel` 构造函数（第 203 行）

**问题**: 只监听了 `_navigation.PropertyChanged` 并转发。`_ai`、`_sortFilter`、`_collection`、`_archive` 的属性变更都未被转发，导致 UI 绑定这些转发属性时收不到变更通知。

**影响列举**:
- `AiViewMode`、`FaceClusters`、`AiCategories`、`TextTokens` 变更时 UI 不刷新
- `Collections`、`PinnedFolders` 集合变更时 UI 不刷新
- `IsCompressDialogVisible`、`PendingCompressOptions` 变更时 UI 不刷新

**修复**: 补充其他子 ViewModel 的 PropertyChanged 转发：

```csharp
// FileListViewModel 构造函数中补充
_navigation.PropertyChanged += OnNavigationPropertyChanged;
_ai.PropertyChanged += OnAiPropertyChanged;
_sortFilter.PropertyChanged += OnSortFilterPropertyChanged;
_archive.PropertyChanged += OnArchivePropertyChanged;
_collection.PropertyChanged += OnCollectionPropertyChanged;
```

每个转发方法根据实际被绑定的属性名按需转发。例如：

```csharp
private void OnAiPropertyChanged(object? sender, PropertyChangedEventArgs e)
{
    if (e.PropertyName is nameof(AiViewModel.AiViewMode)
        or nameof(AiViewModel.CurrentFaceClusterId)
        or nameof(AiViewModel.CurrentAiContextLabel)
        or nameof(AiViewModel.IsAiAnalysisEnabled))
    {
        OnPropertyChanged(e.PropertyName);
    }
}

private void OnArchivePropertyChanged(object? sender, PropertyChangedEventArgs e)
{
    if (e.PropertyName is nameof(ArchiveViewModel.IsCompressDialogVisible)
        or nameof(ArchiveViewModel.PendingCompressOptions)
        or nameof(ArchiveViewModel.ActiveTaskId))
    {
        OnPropertyChanged(e.PropertyName);
    }
}
```

### P2 级别（建议优化）

#### 问题-6: CollectionViewModel 回调地狱

**位置**: `FileListViewModel` 中所有调用 `_collection.NavigateToCollectionAsync` 的地方（第 566、1191、1467、1495、1524 行等，共 5 处）

**问题**: 每次调用传入 15 个 Action 回调，且 5 处调用完全重复。修改一处必须同步改 5 处，极易遗漏。

**修复方案**: 让 `CollectionViewModel` 构造时注入 `NavigationViewModel` 和 `AiViewModel` 的引用，内部直接操作状态：

```csharp
// CollectionViewModel - 修复后
public partial class CollectionViewModel : ObservableObject
{
    private readonly NavigationViewModel _navigation;
    private readonly AiViewModel _ai;
    // ...

    public CollectionViewModel(
        NavigationViewModel navigation,
        AiViewModel ai,
        ICollectionService? collectionService = null,
        // ...
    )
    {
        _navigation = navigation;
        _ai = ai;
        // ...
    }

    public async Task NavigateToCollectionAsync(
        int collectionId,
        Action<bool> setLoading,
        Action<IReadOnlyList<FileSystemEntry>> applyEntries,
        Action<string> setStatus)
    {
        _navigation.IsHomePage = false;
        _navigation.IsCollectionView = true;
        _navigation.IsAiView = false;
        _ai.CurrentFaceClusterId = null;
        _ai.CurrentAiContextLabel = null;
        _navigation.CurrentArchivePath = null;
        _navigation.CurrentArchiveInternalPath = "";
        _navigation.IsSearchMode = false;
        // ... 加载数据 ...
        _navigation.UpdateBreadcrumbs();
    }
}
```

这样 `FileListViewModel` 的调用简化为：

```csharp
await _collection.NavigateToCollectionAsync(
    collectionId,
    v => IsLoading = v,
    entries => ApplyEntries(entries),
    msg => StatusText = msg
);
```

#### 问题-7: NavigationViewModel.RefreshFromNotification 是空方法

**位置**: `NavigationViewModel.cs`（第 85-89 行）

**问题**: async 方法，内部只有两个 return 守卫，没有任何实际操作，也没有 await。

**修复**: 删除此方法。`FileListViewModel.RefreshFromNotification` 已经通过 `NeedsRefreshFromNotification` 做了等价判断。

#### 问题-8: GoHome 双重清理面包屑

**位置**: `FileListViewModel.GoHome()`（第 394-397 行）

**问题**: `_navigation.GoHome()` 内部已经执行了 `Breadcrumbs.Clear()`，随后又调了 `_navigation.ClearBreadcrumbs()`。

**修复**: 删除多余的 `_navigation.ClearBreadcrumbs()` 调用。

#### 问题-9: 收藏夹面包屑 FullPath 为空，无法点击导航

**位置**: `NavigationViewModel.UpdateBreadcrumbs`（第 248-249 行）

**问题**: 收藏夹面包屑的 `FullPath` 设为空字符串 `""`。用户点击后调用 `NavigateToAsync("")`，被空白检查拦截，无法导航。

**修复**: 为 "收藏夹" 段设置一个可导航的路径（比如回到首页），或将 `HasDropdown` 设为 false 并在 BreadcrumbBar 中对空路径禁止点击：

```csharp
// 方案 A: 点击 "收藏夹" 回到首页
segments.Add(new BreadcrumbSegment { Name = "收藏夹", FullPath = "HOME", HasDropdown = false });

// 方案 B (推荐): 不可点击，仅作为标签显示
// 保持 FullPath = "" 但在 BreadcrumbBar 中对空路径不触发导航
```

BreadcrumbBar.razor 对应修改：

```razor
<span class="breadcrumb-link @(segment == ViewModel.Breadcrumbs.Last() ? "current" : "") @(string.IsNullOrEmpty(segment.FullPath) ? "disabled" : "")"
      @onclick="() => { if (!string.IsNullOrEmpty(segment.FullPath)) NavigateTo(segment.FullPath); }"
      @onclick:stopPropagation>
```

---

## 测试验证

### 测试场景

1. **压缩文件导航**
   - 打开压缩文件 -> 面包屑正确显示外部路径 + 压缩文件 + 内部路径
   - 点击外部路径面包屑 -> 正确导航到对应目录
   - 点击压缩文件面包屑 -> 正确导航到压缩文件根目录
   - 点击内部路径面包屑 -> 正确导航到对应内部文件夹

2. **AI 视图导航**
   - 打开 AI 视图 -> 面包屑正确显示
   - 点击面包屑 -> 正确导航

3. **收藏夹视图导航**
   - 打开收藏夹 -> 面包屑正确显示
   - 点击 "收藏夹" 段 -> 不触发无效导航（FullPath 为空时禁止点击）
   - 点击收藏夹名称段 -> 不触发无效导航

4. **普通文件系统导航**
   - 导航到普通目录 -> 面包屑正确显示
   - 点击面包屑 -> 正确导航

5. **后退/前进按钮**（新增测试项）
   - 普通目录 -> 压缩文件 -> 后退 -> 回到普通目录
   - 普通目录 -> AI 视图 -> 后退 -> 回到普通目录
   - 后退后前进 -> 正确回到压缩文件/AI 视图
   - 连续多次后退/前进 -> 历史栈正确

6. **加载状态**（新增测试项）
   - 导航到新目录时 -> IsLoading 保持为 true 直到加载完成
   - 加载失败时 -> 显示错误提示，IsLoading 正确重置为 false

7. **特殊视图切换**（新增测试项）
   - 压缩文件视图 -> 点击外部路径面包屑导航到普通目录 -> CurrentArchivePath 被正确清空
   - 收藏夹视图 -> 点击侧栏目录 -> CurrentCollectionId 被正确清空
   - AI 视图 -> 导航到普通目录 -> IsAiView 被正确重置

## 文件变更清单

### 已完成的修改
- `src/FKFinder/ViewModels/NavigationViewModel.cs` - 新增，集中管理面包屑、视图状态、导航历史
- `src/FKFinder/ViewModels/FileListViewModel.cs` - 重构为协调器，修复 NavigateToArchiveAsync 和 HandleAiNavigationAsync

### 已实施的修复 (2026-04-15)

| 优先级 | 编号 | 文件 | 修改内容 | 状态 |
|--------|------|------|---------|------|
| P0 | BUG-1 | `FileListViewModel.cs` | `NavigateToAsync`: 将 `_ = LoadDirectoryContentsAsync()` 改回 `await`，恢复 try-catch-finally | 已修复 |
| P0 | BUG-2 | `NavigationViewModel.cs` | `NavigateBackAsync`/`NavigateForwardAsync`: 不再经过 `NavigateToAsync`，直接设置 `CurrentPath`；新增 `EndHistoryNavigation()` 供协调器在 reload 完成后调用 | 已修复 |
| P0 | BUG-2 | `FileListViewModel.cs` | 新增 `ReloadAfterHistoryNavigation()`，在 Back/Forward 后根据路径类型分发到 Archive/AI/普通目录加载，完成后调用 `EndHistoryNavigation()` | 已修复 |
| P0 | BUG-3 | `FileListViewModel.cs` | `NavigateToAsync`: 删除提前清零 `IsAiView`/`IsArchiveView`/`IsCollectionView` 的代码，改为仅重置 `_ai.Reset()` 和 `IsSearchMode` | 已修复 |
| P0 | BUG-3 | `NavigationViewModel.cs` | `NavigateToAsync`: 添加哨兵路径拦截（Archive/AI 路径直接 return）；重置块中补充 `CurrentFaceClusterId = null`、`CurrentAiContextLabel = null` | 已修复 |
| P1 | BUG-4 | `ArchiveViewModel.cs` | 删除 `IsArchiveView`、`CurrentArchivePath`、`CurrentArchiveInternalPath` 重复属性；`ShowCompressDialog` 新增 `isArchiveView` 参数；`Reset()` 不再操作已移除的属性 | 已修复 |
| P1 | BUG-5 | `FileListViewModel.cs` | 构造函数中补充 `_ai`、`_archive`、`_collection`、`_sortFilter` 的 PropertyChanged 转发 | 已修复 |
| P2 | 问题-7 | `NavigationViewModel.cs` | 删除空方法 `RefreshFromNotification`（无调用点） | 已修复 |
| P2 | 问题-8 | `FileListViewModel.cs` | `GoHome` 中删除多余的 `_navigation.ClearBreadcrumbs()` 调用 | 已修复 |
| P2 | 问题-9 | `BreadcrumbBar.razor` + `app.css` | 空 FullPath 的面包屑段添加 `disabled` CSS 类，禁止触发导航 | 已修复 |

### 未实施的优化 (P2，后续处理)

| 优先级 | 编号 | 文件 | 修改内容 |
|--------|------|------|---------|
| P2 | 问题-6 | `CollectionViewModel.cs` | 构造注入 NavigationViewModel 和 AiViewModel，消除 15 个回调参数的重复 |
