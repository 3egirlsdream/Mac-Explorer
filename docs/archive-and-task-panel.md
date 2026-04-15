# 归档压缩/解压 & 全局任务面板

## 功能概述

MacExplorer 支持文件压缩、解压和归档浏览功能，配合全局后台任务面板实现非阻塞式异步操作体验。

### 支持的归档格式

| 格式 | 扩展名 |
|------|--------|
| ZIP | `.zip` |
| TAR+GZip | `.tar.gz` / `.tgz` |
| TAR+BZip2 | `.tar.bz2` |
| 读取支持 | `.rar`, `.7z`, `.xz`, `.zst` |

---

## 1. 归档浏览

双击归档文件可进入虚拟目录浏览模式，以只读方式查看归档内部结构。

- 使用哨兵路径 `__archive:{archivePath}#{internalPath}` 标识归档内部位置
- 面包屑导航支持归档内层级跳转
- 归档视图下禁用以下操作：剪切、复制、粘贴、删除、新建文件/文件夹
- 工具栏按钮在归档视图下自动置灰
- 双击归档内文件会提取到临时目录后用系统默认程序打开

**相关文件**：
- `Services/IArchiveService.cs` — 归档服务接口
- `Services/Impl/ArchiveService.cs` — SharpCompress 实现
- `Services/ArchivePathHelper.cs` — 哨兵路径解析工具

---

## 2. 压缩

右键文件/文件夹 → "压缩" 打开压缩对话框。

### 压缩对话框

- 无遮罩层，`position: fixed` 居中显示，不阻断其他 UI 交互
- 可配置：文件名、格式（ZIP / tar.gz / tar.bz2）、压缩级别（快速 / 标准 / 最大）
- 确认后以 fire-and-forget 方式在后台执行

### 临时文件策略

压缩过程中先写入 `.fkfinder-tmp` 后缀的临时文件，完成后 rename 为最终文件名。文件列表自动过滤 `.fkfinder-tmp` 文件，确保压缩过程中不会显示未完成的归档。

### 虚拟文件夹（收藏夹）压缩

- 收藏夹中的文件可能来自不同目录，输出路径取第一个选中文件的父目录
- `CompressOptions.CollectionId` 记录当前收藏夹 ID
- 压缩完成后自动将归档文件加入当前收藏夹
- 来自不同目录的同名文件在归档内自动加序号后缀（如 `readme.txt` → `readme 2.txt`）

**相关文件**：
- `Models/CompressOptions.cs` — 压缩配置（含 `CollectionId`）
- `Components/Dialogs/CompressDialog.razor` — 压缩参数对话框

---

## 3. 解压

右键归档文件 → "解压到此处"，解压到归档文件所在目录。

### 重名处理

解压前扫描归档根级条目，与目标目录已有文件/文件夹比对：
- 冲突时自动加序号后缀（如 `docs` → `docs 2`）
- 归档内所有子条目路径同步更新

**相关文件**：
- `Services/Impl/ArchiveService.cs` — `ExtractAsync` 方法

---

## 4. 全局任务面板

### 架构

```
IBackgroundTaskManager (Singleton)
    ├── AddTask(label, onCompleted?) → BackgroundTaskInfo
    ├── UpdateProgress(taskId, progress, currentFile)
    ├── CompleteTask(taskId)     → 触发 onCompleted → 3秒后自动移除
    ├── FailTask(taskId, error)  → 5秒后自动移除
    ├── MinimizeTask(taskId)     → 收起到任务面板
    └── RemoveTask(taskId)
```

### 任务状态

| 状态 | 说明 |
|------|------|
| `Running` | 执行中，显示进度条和当前文件 |
| `Completed` | 已完成，3 秒后自动从面板移除 |
| `Failed` | 失败，显示错误信息，5 秒后自动移除 |

### UI 组件

**CenterProgressCard** — 居中进度卡片
- 当有活跃任务（未收起）时显示在内容区域正中
- 毛玻璃效果卡片，显示：任务标签、进度条+百分比、当前文件名
- 包含手动"收起"按钮，点击后卡片消失，任务转移到右下角面板

**TaskPanel** — 右下角浮动任务面板
- `position: fixed; bottom: 16px; right: 16px;`
- 显示所有已收起的运行中任务和等待自动消失的已完成/失败任务
- 每个条目显示标签、进度条/状态文字
- 无任务时自动隐藏

### 交互流程

```
用户点击"压缩/解压"
  → 居中进度卡片出现（显示实时进度）
  → 用户可点击"收起"按钮
      → 卡片消失，右下角任务面板出现
      → 后台继续异步执行
  → 任务完成
      → 自动刷新文件列表
      → 任务面板条目显示"已完成"
      → 3 秒后条目自动消失
```

**相关文件**：
- `Models/BackgroundTaskInfo.cs` — 任务状态枚举和任务信息模型
- `Services/IBackgroundTaskManager.cs` — 任务管理器接口
- `Services/Impl/BackgroundTaskManager.cs` — 任务管理器实现
- `Components/Tasks/CenterProgressCard.razor` — 居中进度卡片
- `Components/Tasks/TaskPanel.razor` — 右下角任务面板

---

## 5. ViewModel 集成

`FileListViewModel` 中的关键方法：

| 方法 | 说明 |
|------|------|
| `ShowCompressDialog()` | 同步方法，准备参数并显示压缩对话框 |
| `ConfirmCompress(options)` | fire-and-forget，创建后台任务并启动压缩 |
| `ExtractHere(entry)` | fire-and-forget，创建后台任务并启动解压 |
| `MinimizeActiveTask()` | 收起当前居中显示的任务到面板 |
| `CancelCompressDialog()` | 关闭压缩对话框 |

### DI 注册

```csharp
// MauiProgram.cs
builder.Services.AddSingleton<IBackgroundTaskManager, BackgroundTaskManager>();

// FileListViewModel 构造函数参数
IBackgroundTaskManager? taskManager = null
```

---

## 6. CSS 样式

所有新增样式位于 `wwwroot/css/app.css`，遵循项目 Fluent Design 毛玻璃风格：

- `.compress-dialog` — 压缩对话框（fixed 居中，无遮罩）
- `.center-progress-card` — 居中进度卡片
- `.center-progress-minimize` — 收起按钮
- `.task-panel` — 右下角浮动面板
- `.task-panel-item` — 面板内任务条目

Z-index 层级：`var(--z-panel)` (200)

---

## 7. 已知设计决策

1. **火弃式异步**：压缩/解压通过 `Task.Run` 在后台线程执行，ViewModel 方法为 `void` 非 `async Task`，避免阻塞 UI
2. **临时文件**：`.fkfinder-tmp` 后缀确保未完成的归档不出现在文件列表中
3. **收藏夹刷新**：`RefreshAsync()` 和 `DeleteSelectedAsync()` 在收藏夹视图下使用 `NavigateToCollectionAsync` 而非 `LoadDirectoryContentsAsync`，避免用哨兵路径加载文件系统导致空白
4. **CancellationToken**：每个后台任务持有独立的 `CancellationTokenSource`，支持取消
