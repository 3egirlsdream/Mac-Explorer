# 文件移动后列表刷新问题修复方案

## 问题现象

- 在文件列表中拖拽文件到另一个文件夹后，进入目标文件夹看不到该文件，手动刷新后才出现。
- 剪切文件并粘贴到另一个文件夹后，回到原文件夹仍能看到旧文件，手动刷新后才消失。
- 此时双击旧文件没有可见反馈，因为列表项仍指向已经不存在的旧路径。

## 根本原因

移动、剪贴操作已经正确修改了磁盘，但未打开目录只收到一次 `DirectoryChangeNotifier.NotifyChanged` 通知。原实现只把通知分发给当前已经打开且 `CurrentPath` 命中的窗口，并不会让 SQLite 目录索引失效。

之后导航到受影响目录时，`LoadDirectoryContentsAsync(forceRefresh: false)` 会优先读取 60 秒内仍被认为新鲜的索引，于是拿到移动前的旧列表。

## 修复策略

1. 把 `DirectoryChangeNotifier` 作为统一变更入口：所有目录变更通知先同步失效对应目录的索引，再 debounce 刷新已打开窗口。
2. 在 `IFileIndexWriter` 增加 `InvalidateDirectoriesAsync`，删除受影响目录的 `files.parent_path` 记录和 `directories` 新鲜度记录，保证下一次进入目录会从真实文件系统重读。
3. 批量移动时收集所有来源目录，而不是只通知第一个来源目录。
4. 跨卷后台移动完成后再通知来源目录和目标目录，避免后台任务完成后 UI 仍停留在旧列表。
5. 打开文件前检查路径是否仍存在；如果列表项已过期，给出状态提示并强制刷新当前目录。

## 涉及文件

- `src/MacExplorer/Indexing/SqliteFileIndex.cs`
- `src/MacExplorer/Services/Impl/DirectoryChangeNotifier.cs`
- `src/MacExplorer/ViewModels/FileOpsViewModel.cs`
- `src/MacExplorer/ViewModels/FileListViewModel.cs`

## 验证

运行 `dotnet build MacExplorer.sln`，构建通过。
