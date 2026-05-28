# Git 状态集成 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 文件/文件夹图标右下角叠加 Git 状态徽章（Modified/Staged/Untracked 等），通过 libgit2sharp 原生读取 Git 仓库

**Architecture:** `IGitStatusService` → `GitStatusService` 用 libgit2sharp 读取 `.git` 数据并缓存，`FileIconRenderer.RenderGitBadge()` 渲染 SVG 角标，`FileGridView.GetIconSvg()` 在渲染时调用

**Tech Stack:** C# / .NET MAUI MacCatalyst / libgit2sharp / Blazor Hybrid

---

### Task 1: 添加 LibGit2Sharp NuGet 包

**Files:**
- Modify: `src/MacExplorer/MacExplorer.csproj`

- [ ] **Step 1: 添加 NuGet 引用**

在 `MacExplorer.csproj` 的 `<ItemGroup>` 中添加：

```xml
<PackageReference Include="LibGit2Sharp" Version="0.31.*" />
```

- [ ] **Step 2: 验证包恢复**

```bash
dotnet restore src/MacExplorer/MacExplorer.csproj
```

预期：成功恢复，无错误。

- [ ] **Step 3: Commit**

```bash
git add src/MacExplorer/MacExplorer.csproj
git commit -m "build: 添加 LibGit2Sharp NuGet 包"
```

---

### Task 2: 新建 GitFileStatus 枚举和 GitRepoStatus 类

**Files:**
- Create: `src/MacExplorer/Models/GitFileStatus.cs`

- [ ] **Step 1: 创建文件**

```csharp
namespace MacExplorer.Models;

public enum GitFileStatus
{
    None,
    Ignored,
    Unmodified,
    Modified,
    Staged,
    Added,
    Deleted,
    Renamed,
    Untracked,
    Conflicted
}

public class GitRepoStatus
{
    public string RepoRoot { get; init; } = string.Empty;
    public Dictionary<string, GitFileStatus> FileStatuses { get; init; } = new(StringComparer.Ordinal);
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    public bool IsValid => (DateTime.UtcNow - LastUpdated) < TimeSpan.FromSeconds(5);

    public bool HasAnyChange(string directoryPath)
    {
        var prefix = directoryPath.EndsWith('/') ? directoryPath : directoryPath + '/';
        foreach (var (path, status) in FileStatuses)
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (status is GitFileStatus.Modified or GitFileStatus.Staged or GitFileStatus.Added
                or GitFileStatus.Deleted or GitFileStatus.Renamed or GitFileStatus.Untracked
                or GitFileStatus.Conflicted)
                return true;
        }
        return false;
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add src/MacExplorer/Models/GitFileStatus.cs
git commit -m "feat: 添加 GitFileStatus 枚举和 GitRepoStatus 类"
```

---

### Task 3: 修改 FileSystemEntry 添加 Git 状态属性

**Files:**
- Modify: `src/MacExplorer/Models/FileSystemEntry.cs`

- [ ] **Step 1: 添加属性**

在 `VirtualItemCount` 属性后添加：

```csharp
    // Git status
    public GitFileStatus GitStatus { get; init; }
    public bool HasGitChanges { get; init; }
```

- [ ] **Step 2: Commit**

```bash
git add src/MacExplorer/Models/FileSystemEntry.cs
git commit -m "feat: FileSystemEntry 添加 GitStatus 和 HasGitChanges 属性"
```

---

### Task 4: 新建 IGitStatusService 接口

**Files:**
- Create: `src/MacExplorer/Services/IGitStatusService.cs`

- [ ] **Step 1: 创建接口文件**

```csharp
using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IGitStatusService
{
    Task<GitRepoStatus?> GetRepoStatusAsync(string directoryPath);
    void InvalidateCache(string repoRoot);
}
```

- [ ] **Step 2: Commit**

```bash
git add src/MacExplorer/Services/IGitStatusService.cs
git commit -m "feat: 添加 IGitStatusService 接口"
```

---

### Task 5: 新建 GitStatusService 实现

**Files:**
- Create: `src/MacExplorer/Services/Impl/GitStatusService.cs`

- [ ] **Step 1: 创建 GitStatusService**

```csharp
using System.Collections.Concurrent;
using LibGit2Sharp;
using MacExplorer.Models;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public class GitStatusService : IGitStatusService, IDisposable
{
    private readonly ConcurrentDictionary<string, GitRepoStatus> _cache = new(StringComparer.Ordinal);
    private readonly ILogger<GitStatusService>? _logger;
    private FileSystemWatcher? _watcher;
    private string? _watchedRepo;

    public GitStatusService(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<GitStatusService>();
    }

    public Task<GitRepoStatus?> GetRepoStatusAsync(string directoryPath)
    {
        return Task.Run(() => GetRepoStatus(directoryPath));
    }

    private GitRepoStatus? GetRepoStatus(string directoryPath)
    {
        try
        {
            var repoRoot = Repository.Discover(directoryPath);
            if (string.IsNullOrEmpty(repoRoot)) return null;

            if (_cache.TryGetValue(repoRoot, out var cached) && cached.IsValid)
                return cached;

            using var repo = new Repository(repoRoot);
            var statuses = new Dictionary<string, GitFileStatus>(StringComparer.Ordinal);

            foreach (var item in repo.RetrieveStatus(new StatusOptions { Show = StatusShowOption.IndexAndWorkDir }))
            {
                var path = item.FilePath;
                var status = MapStatus(item.State);
                if (status != GitFileStatus.None)
                    statuses[path] = status;
            }

            var result = new GitRepoStatus
            {
                RepoRoot = repoRoot,
                FileStatuses = statuses,
                LastUpdated = DateTime.UtcNow
            };

            _cache[repoRoot] = result;
            WatchRepo(repoRoot);
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to get Git status for {Path}", directoryPath);
            return null;
        }
    }

    public void InvalidateCache(string repoRoot)
    {
        _cache.TryRemove(repoRoot, out _);
    }

    private static GitFileStatus MapStatus(FileStatus state)
    {
        if (state.HasFlag(FileStatus.Conflicted)) return GitFileStatus.Conflicted;
        if (state.HasFlag(FileStatus.DeletedFromIndex) || state.HasFlag(FileStatus.DeletedFromWorkdir))
            return GitFileStatus.Deleted;
        if (state.HasFlag(FileStatus.RenamedInIndex) || state.HasFlag(FileStatus.RenamedInWorkdir))
            return GitFileStatus.Renamed;
        if (state.HasFlag(FileStatus.NewInIndex)) return GitFileStatus.Added;
        if (state.HasFlag(FileStatus.ModifiedInIndex)) return GitFileStatus.Staged;
        if (state.HasFlag(FileStatus.ModifiedInWorkdir)) return GitFileStatus.Modified;
        if (state.HasFlag(FileStatus.NewInWorkdir)) return GitFileStatus.Untracked;
        if (state.HasFlag(FileStatus.Ignored)) return GitFileStatus.Ignored;
        return GitFileStatus.Unmodified;
    }

    private void WatchRepo(string repoRoot)
    {
        if (_watchedRepo == repoRoot) return;

        _watcher?.Dispose();
        _watchedRepo = repoRoot;

        var gitDir = Path.Combine(repoRoot, ".git");
        if (!Directory.Exists(gitDir)) return;

        try
        {
            _watcher = new FileSystemWatcher(gitDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            _watcher.Changed += (_, _) => InvalidateCache(repoRoot);
            _watcher.Created += (_, _) => InvalidateCache(repoRoot);
            _watcher.Deleted += (_, _) => InvalidateCache(repoRoot);
            _watcher.Renamed += (_, _) => InvalidateCache(repoRoot);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to watch .git directory for {Repo}", repoRoot);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _cache.Clear();
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add src/MacExplorer/Services/Impl/GitStatusService.cs
git commit -m "feat: 实现 GitStatusService — libgit2sharp 读取状态 + 缓存 + FileSystemWatcher"
```

---

### Task 6: DI 注册 GitStatusService

**Files:**
- Modify: `src/MacExplorer/MauiProgram.cs`

- [ ] **Step 1: 注册服务**

在其他 `AddSingleton` 调用附近添加：

```csharp
builder.Services.AddSingleton<IGitStatusService, Services.Impl.GitStatusService>();
```

- [ ] **Step 2: Commit**

```bash
git add src/MacExplorer/MauiProgram.cs
git commit -m "feat: DI 注册 IGitStatusService"
```

---

### Task 7: 在 FileIconRenderer 添加 RenderGitBadge

**Files:**
- Modify: `src/MacExplorer/Models/FileIconRenderer.cs`

- [ ] **Step 1: 添加 RenderGitBadge 方法**

在类中添加：

```csharp
public static string RenderGitBadge(GitFileStatus status, int iconSize)
{
    if (status is GitFileStatus.None or GitFileStatus.Unmodified or GitFileStatus.Ignored)
        return string.Empty;

    var (color, letter) = status switch
    {
        GitFileStatus.Modified => ("#E2B714", "M"),
        GitFileStatus.Staged or GitFileStatus.Added => ("#4CAF50", "A"),
        GitFileStatus.Deleted => ("#F44336", "D"),
        GitFileStatus.Renamed => ("#2196F3", "R"),
        GitFileStatus.Untracked => ("#9E9E9E", "?"),
        GitFileStatus.Conflicted => ("#FF5722", "!"),
        _ => ("", "")
    };

    var badgeSize = iconSize <= 24 ? 8 : 12;
    var cx = iconSize <= 24 ? 20 : 56;
    var cy = iconSize <= 24 ? 20 : 56;
    var r = badgeSize;
    var fontSize = iconSize <= 24 ? 7 : 10;
    var textY = cy + fontSize / 2.5;

    return $@"<circle cx=""{cx}"" cy=""{cy}"" r=""{r}"" fill=""{color}"" stroke=""var(--color-bg-primary, #fff)"" stroke-width=""1.5""/>
              <text x=""{cx}"" y=""{textY}"" fill=""#fff"" font-size=""{fontSize}"" font-weight=""bold"" text-anchor=""middle"" dominant-baseline=""middle"">{letter}</text>";
}

public static string RenderGitFolderBadge(int iconSize)
{
    var badgeSize = iconSize <= 24 ? 4 : 6;
    var cx = iconSize <= 24 ? 20 : 56;
    var cy = iconSize <= 24 ? 20 : 56;

    return $@"<circle cx=""{cx}"" cy=""{cy}"" r=""{badgeSize}"" fill=""#E2B714"" stroke=""var(--color-bg-primary, #fff)"" stroke-width=""1""/>";
}
```

- [ ] **Step 2: Commit**

```bash
git add src/MacExplorer/Models/FileIconRenderer.cs
git commit -m "feat: FileIconRenderer 添加 RenderGitBadge 和 RenderGitFolderBadge"
```

---

### Task 8: 在 FileGridView.GetIconSvg 中渲染 Git 角标

**Files:**
- Modify: `src/MacExplorer/Components/FileList/FileGridView.razor`

- [ ] **Step 1: 修改 GetIconSvg 方法**

在 `GetIconSvg` 返回值中添加 Git 角标。在每个 `return` 语句的 SVG 字符串末尾追加。

找到 `GetIconSvg` 方法，在每个 return 语句中，如果 entry 有 Git 状态则附加角标。最简做法是在方法末尾统一处理：

在 `GetIconSvg` 方法的所有 return 之前，统一构建一个局部变量 `svg`，然后根据 Git 状态附加角标。

修改方法的返回逻辑：将所有 `return "..."` 改为先赋值给字符串变量，最后统一附加角标并返回。以最后一个 return（FileIconRenderer.Render 分支）为例：

将：
```csharp
return FileIconRenderer.Render(entry.IconKey, entry.Extension, size);
```
改为：
```csharp
var svg = FileIconRenderer.Render(entry.IconKey, entry.Extension, size);
// Append Git badge if applicable
if (entry.GitStatus is not GitFileStatus.None and not GitFileStatus.Unmodified and not GitFileStatus.Ignored)
{
    svg = svg.Replace("</svg>", FileIconRenderer.RenderGitBadge(entry.GitStatus, size) + "</svg>");
}
else if (entry.IsDirectory && entry.HasGitChanges)
{
    svg = svg.Replace("</svg>", FileIconRenderer.RenderGitFolderBadge(size) + "</svg>");
}
return svg;
```

对所有其他 return 分支做同样的处理（嵌套的 svg 构建 + 角标 + return）。

> **实现提示**：`GetIconSvg` 有多个 return 路径（虚拟头像、AI 图标、缩略图、Blob URL、文件夹、普通文件）。建议在方法开头构建 `svg`，各分支设置 `svg`，最后统一处理角标后 `return svg`。

- [ ] **Step 2: Commit**

```bash
git add src/MacExplorer/Components/FileList/FileGridView.razor
git commit -m "feat: GetIconSvg 渲染 Git 状态角标"
```

---

### Task 9: 在 FileListViewModel 中集成 Git 状态服务

**Files:**
- Modify: `src/MacExplorer/ViewModels/FileListViewModel.cs`

- [ ] **Step 1: 注入 IGitStatusService**

在构造函数参数中添加：

```csharp
IGitStatusService? gitStatusService = null,
```

在构造函数体内保存引用：

```csharp
private readonly IGitStatusService? _gitStatusService;
// ... 在构造函数中
_gitStatusService = gitStatusService;
```

- [ ] **Step 2: 在 LoadDirectoryContentsAsync 后解析 Git 状态**

在 `ApplyEntries` 调用后添加 Git 状态解析。找到 `LoadDirectoryContentsAsync` 方法中的 `ApplyEntries(entries)` 调用，之后添加：

```csharp
// Resolve Git status for entries
if (_gitStatusService != null)
{
    _ = ResolveGitStatusAsync(Entries);
}
```

- [ ] **Step 3: 添加 ResolveGitStatusAsync 方法**

```csharp
private async Task ResolveGitStatusAsync(IReadOnlyList<FileSystemEntry> entries)
{
    try
    {
        var path = _navigation.CurrentPath;
        var repoStatus = await _gitStatusService!.GetRepoStatusAsync(path);
        if (repoStatus == null) return;

        var repoRoot = repoStatus.RepoRoot;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.IsVirtual) continue;

            // Get relative path from repo root
            var relativePath = entry.FullPath.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase)
                ? entry.FullPath[repoRoot.Length..].TrimStart('/')
                : entry.FullPath;

            if (repoStatus.FileStatuses.TryGetValue(relativePath, out var status))
            {
                entries[i] = new FileSystemEntry
                {
                    FullPath = entry.FullPath,
                    Name = entry.Name,
                    IsDirectory = entry.IsDirectory,
                    Size = entry.Size,
                    LastModified = entry.LastModified,
                    Created = entry.Created,
                    Extension = entry.Extension,
                    IsHidden = entry.IsHidden,
                    IsSymbolicLink = entry.IsSymbolicLink,
                    IsReadable = entry.IsReadable,
                    IsWritable = entry.IsWritable,
                    IconKey = entry.IconKey,
                    IconUrl = entry.IconUrl,
                    ThumbnailUrl = entry.ThumbnailUrl,
                    IsVirtual = entry.IsVirtual,
                    VirtualFolderType = entry.VirtualFolderType,
                    VirtualFolderKey = entry.VirtualFolderKey,
                    VirtualItemCount = entry.VirtualItemCount,
                    GitStatus = status,
                    HasGitChanges = entry.IsDirectory && repoStatus.HasAnyChange(relativePath)
                };
            }
            else if (entry.IsDirectory)
            {
                entries[i] = entry with { HasGitChanges = repoStatus.HasAnyChange(relativePath) };
            }
        }

        OnPropertyChanged(nameof(Entries));
    }
    catch (Exception ex)
    {
        _logger?.LogWarning(ex, "Failed to resolve Git status");
    }
}
```

> **注意**：`FileSystemEntry` 使用 `init` 属性，需要完整构造新实例来修改 `GitStatus`。

- [ ] **Step 4: Commit**

```bash
git add src/MacExplorer/ViewModels/FileListViewModel.cs
git commit -m "feat: FileListViewModel 集成 GitStatusService 解析文件 Git 状态"
```

---

### Task 10: 构建验证

- [ ] **Step 1: 构建项目**

```bash
dotnet build src/MacExplorer/MacExplorer.csproj 2>&1 | grep -E "错误|error CS|生成"
```

预期输出：
```
已成功生成。
    0 个错误
```

- [ ] **Step 2: 修复编译错误（如有）**

根据错误信息调整，重点关注 `FileSystemEntry` 的 `init` 属性赋值语法和 libgit2sharp API 调用。

---

## 验证清单

构建成功后，在 MacCatalyst 上：

1. 进入 Git 仓库目录 → 修改过的文件显示黄色 M 角标
2. 新建未跟踪文件 → 灰色 ? 角标
3. `git add` 后 → 绿色 A 角标
4. 有修改的子文件夹 → 实心圆点
5. 非 Git 目录 → 无任何角标
6. `.gitignore` 中的文件 → 无角标
7. 执行 `git add` → 角标更新（FileSystemWatcher + 缓存失效）
