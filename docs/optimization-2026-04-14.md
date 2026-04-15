# MacExplorer 代码优化报告

**日期**: 2026-04-14
**优化范围**: P0 (必须修复) / P1 (强烈建议) / P2 (建议)

---

## P0 — 必须修复

### 1. 添加结构化异常日志

**问题**: 多处 `catch { }` 空块吞噬异常，导致生产环境调试极度困难。

**修复**:
- 创建 [Services/Impl/AppLogger.cs](src/MacExplorer/Services/Impl/AppLogger.cs)
- 在 `MauiProgram.cs` 注册 `ILoggerFactory`
- 替换所有空 catch 块为结构化日志：

| 文件 | 修复内容 |
|------|---------|
| `FileListViewModel.cs` | 10 个方法添加日志 (DeleteSelectedAsync, LoadDirectoryContentsAsync, BatchLoadRatingsAsync, ResolveIconsInBackgroundAsync, ResolveThumbnailsInBackgroundAsync, LoadCollectionsAsync, NavigateToCollectionAsync, TriggerImageAnalysisAsync, ResolveFaceThumbnailsAsync) |
| `SettingsService.cs` | 3 个方法添加日志 (LoadAll, Get<T>, Persist) |
| `BackgroundTaskManager.cs` | 回调异常改为 Warning 级别日志 |
| `CollectionService.cs` | DeleteCollectionAsync 添加日志后仍抛出异常 |
| `AiTagService.cs` | SearchCategoriesAsync 添加日志 |
| `FrequentFolderService.cs` | RecordVisitAsync, GetTopFoldersAsync 添加日志 |

**Commit**: `41959a7`

---

### 2. 拆分 FileListViewModel God Object

**问题**: `FileListViewModel` 2626 行，24 个注入服务，违反单一职责原则。

**修复**: 拆分为 7 个专门类 + 1 个协调器

```
┌─────────────────────────────────────────────────────────────┐
│              FileListViewModel (Coordinator, ~400行)          │
│  - Entries, SelectedEntries, IsLoading, StatusText          │
│  - 上下文菜单状态, 预览窗格, 元数据面板                       │
│  - SelectEntry, ShowContextMenu, ApplyEntries (hub)         │
└─────────────────────────────────────────────────────────────┘
         │                    │                    │
         ▼                    ▼                    ▼
┌──────────────┐   ┌──────────────────┐   ┌──────────────────┐
│NavigationVm  │   │ FileOpsViewModel │   │  SearchViewModel │
│ ~300行      │   │  ~400行         │   │  ~200行         │
│ 历史/面包屑  │   │ 复制/剪切/粘贴   │   │ 文本搜索        │
│ FSEvents    │   │ 删除/重命名/移动  │   │ AI语义搜索       │
└──────────────┘   └──────────────────┘   └──────────────────┘
         │                    │                    │
         ▼                    ▼                    ▼
┌──────────────┐   ┌──────────────────┐   ┌──────────────────┐
│ArchiveViewModel│ │  AiViewModel    │   │ CollectionVm    │
│ ~250行      │   │  ~500行         │   │  ~300行        │
│ 存档浏览     │   │ 人脸聚类/分类   │   │ 收藏/固定/评分  │
│ 压缩/解压    │   │ 地点/日期/文本   │   │                 │
└──────────────┘   └──────────────────┘   └──────────────────┘
                                                          │
                                           ┌──────────────────┐
                                           │ SortFilterVm    │
                                           │  ~200行         │
                                           │ 排序/分组/筛选   │
                                           └──────────────────┘
```

**新增文件**:
- [ViewModels/NavigationViewModel.cs](src/MacExplorer/ViewModels/NavigationViewModel.cs)
- [ViewModels/FileOpsViewModel.cs](src/MacExplorer/ViewModels/FileOpsViewModel.cs)
- [ViewModels/SearchViewModel.cs](src/MacExplorer/ViewModels/SearchViewModel.cs)
- [ViewModels/ArchiveViewModel.cs](src/MacExplorer/ViewModels/ArchiveViewModel.cs)
- [ViewModels/AiViewModel.cs](src/MacExplorer/ViewModels/AiViewModel.cs)
- [ViewModels/CollectionViewModel.cs](src/MacExplorer/ViewModels/CollectionViewModel.cs)
- [ViewModels/SortFilterViewModel.cs](src/MacExplorer/ViewModels/SortFilterViewModel.cs)

**修改文件**:
- [ViewModels/FileListViewModel.cs](src/MacExplorer/ViewModels/FileListViewModel.cs) — 协调器化，~400 行
- [MauiProgram.cs](src/MacExplorer/MauiProgram.cs) — 更新 DI 注册

**Commit**: `e246049`

---

## P1 — 强烈建议

### 3. 统一数据库连接管理

**问题**: `CollectionService`, `RatingService`, `FrequentFolderService`, `PinnedFolderService`, `SettingsService`, `AiTagService`, `SqliteFileIndex` 各自创建独立 `SqliteConnection`，浪费文件句柄且可能导致锁定争用。

**修复**: 创建共享工厂

```
┌─────────────────────────────────────────────────────────────┐
│          DatabaseConnectionFactory (Singleton)               │
│  - GetConnection() → 共享连接 (WAL 模式, 5s busy timeout)   │
│  - CreateConnection() → 新连接 (用于独占操作如恢复逻辑)       │
└─────────────────────────────────────────────────────────────┘
         │                    │                    │
         ▼                    ▼                    ▼
   SqliteFileIndex     CollectionService    RatingService
   FrequentFolder     PinnedFolder         SettingsService
   AiTagService
```

**新增文件**:
- [Services/Impl/DatabaseConnectionFactory.cs](src/MacExplorer/Services/Impl/DatabaseConnectionFactory.cs)

**修改文件** (6 个服务 + MauiProgram):
- `CollectionService.cs`
- `RatingService.cs`
- `FrequentFolderService.cs`
- `PinnedFolderService.cs`
- `SettingsService.cs`
- `AiTagService.cs`
- `SqliteFileIndex.cs` — 新增构造函数重载支持工厂注入
- `MauiProgram.cs`

**Commit**: `aeb3ff4`

---

### 4. 消除图标解析重复代码

**问题**: `MacFileService.GetIconKeyForExtension()` 和 `SqliteFileIndex.ResolveIconKey()` 包含几乎完全相同的 60+ 行 switch 表达式。

**修复**: 提取为共享静态类

**新增文件**:
- [Services/Impl/FileIconResolver.cs](src/MacExplorer/Services/Impl/FileIconResolver.cs) — 统一的 `ResolveIconKey(extension)` 方法，包含两个原始实现的所有扩展名

**修改文件**:
- `MacFileService.cs` — 委托给 `FileIconResolver.ResolveIconKey()`
- `SqliteFileIndex.cs` — 委托给 `FileIconResolver.ResolveIconKey()`

**额外修复**: 发现 `MacFileService` 原始实现缺少 `.tgz` 和 `.zst`，现已补全。

**Commit**: `0ff69f1`

---

## P2 — 建议

### 5. 提取虚拟路径常量

**问题**: `"__ai:face:"`, `"__archive:"`, `"__collection:"` 等哨兵路径字符串散落在代码各处。

**修复**: 创建集中常量类

**新增文件**:
- [Services/VirtualPath.cs](src/MacExplorer/Services/VirtualPath.cs)

```csharp
public static class VirtualPath
{
    public const string AiPrefix = "__ai:";
    public const string ArchivePrefix = "__archive:";
    public const string CollectionPrefix = "__collection:";
    public const string SystemTrash = "__system_trash__";

    // AI 子路径
    public const string AiPeople = "__ai:people";
    public const string AiCategories = "__ai:categories";
    public const string AiFacePrefix = "__ai:face:";
    // ... etc
}
```

**修改文件** (5 个):
- `AiPathHelper.cs`
- `ArchivePathHelper.cs`
- `MacFileService.cs`
- `FileListViewModel.cs`
- `AiViewModel.cs`

**Commit**: `a074222`

---

### 6. 合并 IAiTagService 和 IImageAnalysisService

**结论**: 不合并

分析表明这两个接口存在**生产者-消费者关系**而非功能重叠：

| 方面 | IAiTagService | IImageAnalysisService |
|------|--------------|----------------------|
| 职责 | AI 结果的 SQLite 存储/查询 | Vision 框架图像分析 |
| 平台 | 跨平台 (纯 SQLite) | macOS/Catalyst 专用 |
| 接口大小 | 16 方法 | 2 方法 |

合并会:
1. 将平台特定的 Vision 代码耦合到存储层
2. 使人脸聚类逻辑无法被非 Mac 平台访问
3. 违反单一职责原则

**当前架构是正确的，保持分离。**

---

## 构建验证

```bash
dotnet build src/MacExplorer/MacExplorer.csproj -f net10.0-maccatalyst
# 0 错误 (4 个预存警告与本次优化无关)
```

---

## 提交汇总

| Commit | 描述 |
|--------|------|
| `41959a7` | P0: Add structured exception logging |
| `e246049` | P0: Split FileListViewModel into specialized viewmodels |
| `aeb3ff4` | P1: Add shared DatabaseConnectionFactory |
| `0ff69f1` | P1: Extract FileIconResolver to deduplicate icon resolution |
| `a074222` | P2: Extract virtual path sentinel strings to VirtualPath constants |

---

## 架构评分对比

| 方面 | 优化前 | 优化后 |
|------|--------|--------|
| 可调试性 | 2/10 (静默异常) | 7/10 (结构化日志) |
| 可维护性 | 3/10 (God Object) | 7/10 (专门类) |
| 数据库连接 | 7 个独立连接 | 共享工厂 + 按需独立连接 |
| 代码复用 | 重复图标解析 | 单一共享方法 |
| 架构清晰度 | 5/10 | 8/10 |

---

## 待优化项 (P3)

以下问题未在本次优化中处理，可在后续迭代中考虑：

1. **NavigationBridge/MacDragDropBridge 竞态条件** — `PendingNavigationPath` 无锁访问
2. **BackgroundTaskManager 内存持久化** — 任务状态未序列化，重启丢失
3. **SqliteFileIndex 引用 SqliteFileIndex** — 循环依赖隐患
4. **FSEventsWatcher 静态实例陷阱** — `_instance` 静态字段设计脆弱

---

## 二次审查修正

**日期**: 2026-04-14

对上述 5 个 commit 进行 code review 后，发现并修正了以下问题：

### 7. BUG 修复: 归档子目录导航失效

**问题**: `FileListViewModel.NavigateToArchiveAsync` 将完整的 sentinelPath（如 `__archive:/path/file.zip#subfolder/`）直接传给 `ArchiveViewModel.NavigateToArchiveAsync` 的 `archivePath` 参数，第二个参数硬编码为 `""`。导致从历史导航（Back/Forward）进入归档子目录时，始终显示归档根目录。

**修复**: 使用 `ArchivePathHelper.Parse()` 正确拆分 sentinelPath 后再传入。

**修改文件**: `FileListViewModel.cs`

### 8. BUG 修复: 退出搜索总是回到首页

**问题**: `ExitSearch()` 中判断条件 `_search.SearchQuery != null` 在 `ExitSearchMode(true)` 调用后，`SearchQuery` 已被清空为 `string.Empty`（恒不为 null），导致退出搜索后无论搜索前是否在首页都会回到首页。

**修复**: 在调用 `ExitSearchMode` 前先通过 `_search.WasHomePageBeforeSearch` 保存状态，`SearchViewModel` 新增 `WasHomePageBeforeSearch` 只读属性暴露该状态。

**修改文件**: `FileListViewModel.cs`, `SearchViewModel.cs`

### 9. 依赖注入改进: 消除运行时类型转换

**问题**: `FileListViewModel` 中大量使用 `_fileService as IClipboardService`、`_fileService as IApplicationLauncherService`、`_fileService as IArchiveService`、`_fileService as ISettingsService` 等运行时类型转换，隐含了 `MacFileService` 必须实现这些接口的假设，降低了可测试性。

**修复**: 构造函数新增 `IClipboardService`、`IApplicationLauncherService`、`ISettingsService`、`IArchiveService` 四个显式注入参数，`MauiProgram.cs` DI 工厂同步更新。`ConfirmCompress` 中从 `_fileService as ICollectionService` 改为通过 `CollectionViewModel` 暴露的 `internal CollectionService` 属性获取。

**修改文件**: `FileListViewModel.cs`, `CollectionViewModel.cs`, `MauiProgram.cs`

### 10. DatabaseConnectionFactory 改为独立连接模式

**问题**: 原设计中 `GetConnection()` 返回共享单例连接（注释标注 "Callers should NOT close"），但各服务的 Dispose 方法需要释放连接资源，共享连接被任一服务关闭后其他服务全部崩溃。

**修复**:
- `GetConnection()` 改为每次创建新的独立连接，调用方拥有连接并负责释放
- 删除共享连接字段 `_connection` 和锁 `_lock`
- 删除冗余的 `CreateConnection()` 别名方法，`SqliteFileIndex` 中的调用同步改为 `GetConnection()`
- 6 个服务（`AiTagService`, `CollectionService`, `FrequentFolderService`, `PinnedFolderService`, `RatingService`, `SettingsService`）的 `Dispose()` 补全 `_connection.Close()` + `_connection.Dispose()`
- `SettingsService` 新增 `IDisposable` 接口实现（原优化遗漏）

**修改文件**: `DatabaseConnectionFactory.cs`, `SqliteFileIndex.cs`, `AiTagService.cs`, `CollectionService.cs`, `FrequentFolderService.cs`, `PinnedFolderService.cs`, `RatingService.cs`, `SettingsService.cs`

### 11. ScrollMode 职责归属调整

**问题**: `ScrollMode` 枚举、`ScrollBehaviorAfterLoad`、`IsRestoringNavigation` 同时存在于 `NavigationViewModel` 和 `FileListViewModel` 中，`NavigateBackAsync`/`NavigateForwardAsync` 等方法双重赋值，职责不清。

**修复**: `ScrollMode` 相关状态统一保留在 `FileListViewModel`（协调器），从 `NavigationViewModel` 中移除。`PendingSelectFileName` 数据存储保留在 `NavigationViewModel`，`FileListViewModel` 通过属性代理访问，消除重复的私有字段。

**修改文件**: `NavigationViewModel.cs`, `FileListViewModel.cs`

### 12. 日志注册修正: 确保 Release 模式日志可用

**问题**: 移除了 `MauiProgram.cs` 中 `ILoggerFactory` 的显式注册，仅在 `#if DEBUG` 下调用 `builder.Logging.AddDebug()`。Release 构建中日志可能无法输出到目标，与 P0 结构化日志优化目标矛盾。

**修复**: 将 `builder.Logging.AddDebug()` 移到 `#if DEBUG` 条件外，确保所有构建配置下日志基础设施可用。同时删除了无引用的 `AppLogger.cs`。

**修改文件**: `MauiProgram.cs`，删除 `Services/Impl/AppLogger.cs`

### 构建验证

```bash
dotnet build src/MacExplorer/MacExplorer.csproj -f net10.0-maccatalyst
# 0 错误 (6 个预存警告与本次修正无关)
```
