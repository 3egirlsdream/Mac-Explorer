# PIN 文件夹重命名同步修复方案

## 1. 问题描述

**BUG 现象：** 用户在文件列表中对已 PIN 到侧边栏的文件夹执行重命名操作后，再点击侧边栏中对应的 PIN 文件夹，导航无反应。

**影响范围：** 侧边栏 PIN 文件夹导航功能完全失效——所有被重命名过的 PIN 文件夹均无法通过侧边栏点击跳转。

## 2. 根因分析

1. PIN 文件夹在 SQLite 数据库的 `pinned_folders` 表中以 `folder_path` 作为唯一标识进行存储。
2. `FileListViewModel.RenameEntryAsync()` 在执行重命名时，已正确更新了文件索引（FTS5）和 AI 标签路径，但**遗漏了对 `pinned_folders` 表的路径同步**。
3. 重命名完成后，`pinned_folders` 表中的 `folder_path` 仍指向旧路径（已不存在），侧边栏点击时尝试导航到无效路径，因此无反应。

## 3. 修复方案

修复分三步：扩展接口 → 实现方法 → 在重命名流程中调用。

### 3.1 扩展接口 — `IPinnedFolderService.cs`

在接口中新增 `UpdateFolderPathAsync` 方法声明，接受旧路径、新路径和新显示名三个参数：

```csharp
public interface IPinnedFolderService
{
    Task<IReadOnlyList<PinnedFolder>> GetAllAsync();
    Task PinAsync(string folderPath, string displayName);
    Task UnpinAsync(string folderPath);
    Task<bool> IsPinnedAsync(string folderPath);
    Task UpdateFolderPathAsync(string oldPath, string newPath, string newDisplayName);
}
```

### 3.2 实现方法 — `PinnedFolderService.cs`

使用参数化 SQL `UPDATE` 语句，同时更新 `folder_path` 和 `display_name` 两个字段：

```csharp
public async Task UpdateFolderPathAsync(string oldPath, string newPath, string newDisplayName)
{
    using var cmd = _connection.CreateCommand();
    cmd.CommandText = """
        UPDATE pinned_folders 
        SET folder_path = @newPath, display_name = @newDisplayName
        WHERE folder_path = @oldPath
        """;
    cmd.Parameters.AddWithValue("@oldPath", oldPath);
    cmd.Parameters.AddWithValue("@newPath", newPath);
    cmd.Parameters.AddWithValue("@newDisplayName", newDisplayName);
    await cmd.ExecuteNonQueryAsync();
}
```

### 3.3 调用更新 — `FileListViewModel.cs`（`RenameEntryAsync` 方法）

在 `RenameEntryAsync` 方法中，**AI 视图分支**和**常规视图分支**都增加了 PIN 路径同步逻辑。仅当 `entry.IsDirectory` 时触发，避免文件重命名时误更新。

**AI 视图分支（`IsAiView == true`）：**

```csharp
// 同步更新 PIN 文件夹路径
if (_pinnedFolderService != null && entry.IsDirectory)
{
    await _pinnedFolderService.UpdateFolderPathAsync(oldPath, newPath, newName);
    await LoadPinnedFoldersAsync();
}
```

**常规视图分支（`IsAiView == false`）：**

```csharp
// 同步更新 PIN 文件夹路径
if (_pinnedFolderService != null && entry.IsDirectory)
{
    await _pinnedFolderService.UpdateFolderPathAsync(oldPath, newPath, newName);
    await LoadPinnedFoldersAsync();
}
```

两个分支的逻辑一致，均在文件系统重命名完成、索引更新之后执行，更新后立即调用 `LoadPinnedFoldersAsync()` 刷新侧边栏显示。

## 4. 涉及文件清单

| 文件路径 | 改动说明 |
|---|---|
| `src/MacExplorer/Services/IPinnedFolderService.cs` | 新增 `UpdateFolderPathAsync` 接口方法声明 |
| `src/MacExplorer/Services/Impl/PinnedFolderService.cs` | 实现 `UpdateFolderPathAsync`，使用参数化 SQL 更新 `folder_path` 和 `display_name` |
| `src/MacExplorer/ViewModels/FileListViewModel.cs` | 在 `RenameEntryAsync` 的 AI 视图分支和常规视图分支中调用 PIN 路径同步并刷新侧边栏 |

## 5. 验证要点

- **防 SQL 注入：** `UpdateFolderPathAsync` 使用 `@oldPath`、`@newPath`、`@newDisplayName` 参数化查询，杜绝注入风险。
- **双分支覆盖：** AI 视图（`IsAiView == true`）和常规视图两个代码路径均已添加同步逻辑。
- **仅目录生效：** 通过 `entry.IsDirectory` 条件判断，文件重命名不会触发 PIN 更新。
- **异常处理：** PIN 同步代码位于外层 `try/catch` 块内，异常统一由 `StatusText = $"重命名失败: {ex.Message}"` 处理。
- **即时刷新：** 更新完成后调用 `LoadPinnedFoldersAsync()` 确保侧边栏立即反映新名称。
