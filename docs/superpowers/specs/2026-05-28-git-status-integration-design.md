# Git 状态集成：文件/文件夹 Git 状态图标角标

日期: 2026-05-28 | 状态: 已批准

## 问题

文件管理器不显示文件/文件夹的 Git 版本控制状态。用户无法直观判断哪些文件被修改、未跟踪、已暂存或有冲突。

## 方案: libgit2sharp 直接读取 Git 仓库 + 图标角标

用 libgit2sharp 原生库直接读取 `.git` 数据，在文件/文件夹图标右下角叠加 Git 状态徽章。后台 FileSystemWatcher 监听变更，增量刷新缓存。

## 架构

### 数据模型

**`GitFileStatus` 枚举（新文件）：**

```
None, Ignored, Unmodified, Modified, Staged, Added, Deleted, Renamed, Untracked, Conflicted
```

**`FileSystemEntry` 新增属性：**

- `GitStatus GitStatus` — 文件自身的 Git 状态（默认 None）
- `bool HasGitChanges` — 文件夹内部有变更时为 true

**`GitRepoStatus` 类（新文件）：**

- `RepoRoot` — 仓库根目录
- `FileStatuses: Dictionary<string, GitFileStatus>` — 相对路径 → 状态
- `LastUpdated` — 缓存时间，超过 5s 视为过期

### 服务接口

```
IGitStatusService
  ├─ Task<GitRepoStatus?> GetRepoStatusAsync(string directoryPath)
  ├─ void InvalidateCache(string repoRoot)
  └─ event Action<string>? StatusChanged

GitStatusService : IGitStatusService, IDisposable
  ├─ Dictionary<string, GitRepoStatus> _cache
  ├─ Repository (libgit2sharp)
  ├─ FileSystemWatcher (.git/index)
  └─ 定时轮询兜底（10s）
```

### 数据流

```
进入目录 → LoadDirectoryContentsAsync()
  → GetRepoStatusAsync(path)
     → 向上查找 .git → 找到
     → 缓存命中且有效? → 返回缓存
     → 缓存过期/不存在 → Repository.RetrieveStatus() → 遍历所有条目
     → 存入缓存 → 启动 FileSystemWatcher
  → ApplyEntries() 为每个 FileSystemEntry 设置 GitStatus
  → 渲染 GetIconSvg() → FileIconRenderer.RenderGitBadge(status, size)
```

不在 Git 仓库中 → `GetRepoStatusAsync` 返回 null → 无角标，零开销。

### UI 渲染

`FileIconRenderer` 新增 `RenderGitBadge(GitFileStatus status, int iconSize)` 方法，在图标右下角叠加 SVG 圆形徽章。

**颜色方案：**

| 状态 | 颜色 | 符号 |
|------|------|------|
| Modified | `#E2B714` (黄) | M |
| Staged / Added | `#4CAF50` (绿) | A |
| Deleted | `#F44336` (红) | D |
| Renamed | `#2196F3` (蓝) | R |
| Untracked | `#9E9E9E` (灰) | ? |
| Conflicted | `#FF5722` (橙) | ! |
| Ignored / Unmodified / None | 不显示 | — |

文件夹 `HasGitChanges == true` → 实心小圆点（无文字），颜色为子文件最高优先级色。

**角标尺寸**：列表视图 24px 图标用 8px 角标，网格视图 64px 图标用 12px 角标。

### 性能策略

| 场景 | 策略 |
|------|------|
| 小仓库 (<1000 文件) | 同步获取，首次 ~50ms |
| 大仓库 (1000+ 文件) | 异步后台获取，状态延迟补充 |
| 增量更新 | FileSystemWatcher 监听 .git/index → 标记缓存失效 → 懒刷新 |
| 不在仓库中 | 快速返回 null（向上查找 .git，找不到即停止） |
| 兜底 | 10s 定时轮询 .git/index LastWriteTime |

### 边界情况

- `.git` 目录不显示 Git 状态
- 子模块：不递归，子模块目录显示为单个提交状态
- 已忽略文件 (Ignored) 不显示角标
- Git 仓库锁或损坏：静默失败，无角标

## 文件改动

| 文件 | 类型 |
|------|------|
| `Models/GitFileStatus.cs` | 新建：枚举 + GitRepoStatus 类 |
| `Models/FileSystemEntry.cs` | 修改：新增 GitStatus、HasGitChanges |
| `Services/IGitStatusService.cs` | 新建：接口 |
| `Services/Impl/GitStatusService.cs` | 新建：libgit2sharp 实现 |
| `Models/FileIconRenderer.cs` | 修改：新增 RenderGitBadge() |
| `Components/FileList/FileGridView.razor` | 修改：GetIconSvg() 调用角标渲染 |
| `ViewModels/FileListViewModel.cs` | 修改：加载目录后获取 Git 状态 |
| `MauiProgram.cs` | 修改：DI 注册 |
| `MacExplorer.csproj` | 修改：添加 LibGit2Sharp NuGet |

## 不影响的部分

- 拖放、重命名、右键菜单、排序、分组、搜索
- AI 视图、归档视图、收藏视图
- 无 Git 仓库的目录：零开销

## 验证清单

1. Git 仓库目录 → 修改过的文件显示黄色 M 角标
2. 新建未跟踪文件 → 灰色 ? 角标
3. `git add` 后 → 绿色 A 角标
4. 有修改的文件夹 → 实心圆点
5. 非 Git 目录 → 无任何角标
6. `echo "node_modules/" > .gitignore` → ignored 文件无角标
7. 子模块目录 → 显示子模块状态
8. 后台 `git add` 后 → FileSystemWatcher 刷新，角标更新
