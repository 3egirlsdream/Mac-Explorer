# 工作区 Review 修复方案

## 问题与修复

1. Git rename 状态匹配错误
   - `git status --porcelain -z` 的 rename 输出顺序是当前路径在前、旧路径在后。
   - 修复为使用当前路径标记 `Renamed`，并跳过旧路径记录。

2. Git 状态异步回填会清空用户选择
   - `ResolveGitStatusAsync` 原来调用 `ApplyEntries`，会清空选中项。
   - 新增保留选择的应用路径，按 `FullPath` 恢复选择和 anchor。

3. Git 辅助进程超时不可靠
   - `ReadToEnd()` 在 `WaitForExit()` 前执行，进程卡住时可能阻塞。
   - 统一通过带超时的 helper 执行 git 命令，超时后 kill 进程树。

4. SQLite 索引连接并发访问风险
   - `SqliteFileIndex` 是单例连接，目录读取、更新、失效可能并发访问。
   - 增加连接级 `SemaphoreSlim`，串行化所有索引和图标缓存访问。

5. 通知入口同步等待异步任务可能死锁
   - `DirectoryChangeNotifier` 需要保证返回前索引已失效，但不能阻塞 UI 同步上下文。
   - 通过 `Task.Run(...).GetAwaiter().GetResult()` 保持同步语义，并避免 UI continuation 死锁。

6. 暂存区拖拽 mousedown latch
   - 工作区已采用时间戳方案替代布尔 latch，避免下一个空白点击被错误吞掉。

## 验证

- 运行 `dotnet build MacExplorer.sln`。
- 手动验证 Git rename 文件在列表中显示 `R` 状态。
- 手动验证 Git 状态徽标出现后，当前多选不会丢失。
- 手动验证拖拽移动后，来源目录和目标目录无需手动刷新。
