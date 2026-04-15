# 文件图标/缩略图渲染管线

## 数据模型

`FileSystemEntry` 中与图标相关的字段：

| 字段 | 类型 | 说明 |
|------|------|------|
| `IconKey` | `string` | 图标分类键，如 `"file-image"`, `"app-bundle"`, `"file-generic"` |
| `IconUrl` | `string?` | 应用图标的本地 PNG 路径（仅 `.app` bundle 使用） |
| `ThumbnailUrl` | `string?` | 缩略图 data URI（`data:image/png;base64,...`）或人脸裁剪 URL |
| `IsVirtual` | `bool` | 是否为虚拟条目（AI 顶层文件夹、压缩包内部条目） |

## 渲染管线总览

```
文件条目创建
    │
    ├─ 1. IconKey 静态解析（同步，创建时确定）
    │      FileIconResolver.ResolveIconKey(extension)
    │
    ├─ 2. 首次渲染（同步，GetIconSvg）
    │      根据 IconKey 渲染 SVG 图标作为占位
    │
    └─ 3. 后台异步解析（fire-and-forget）
           ├─ ResolveIconsInBackgroundAsync → 提取 .app 原生图标 → 设置 IconUrl
           ├─ ResolveThumbnailsInBackgroundAsync → 生成图片缩略图 → 设置 ThumbnailUrl
           └─ CreateBlobUrlsForEntriesAsync → 将 IconUrl PNG 转为 Blob URL 供渲染
```

## 阶段 1：IconKey 静态解析

入口：`FileIconResolver.ResolveIconKey(extension)`

在 `FileSystemEntry` 创建时由文件扩展名确定 `IconKey`。该值在条目生命周期内不变。

主要映射：

| 扩展名 | IconKey |
|--------|---------|
| `.app` | `app-bundle` |
| `.jpg/.png/.heic/.dng/...` | `file-image` |
| `.mp4/.mov/...` | `file-video` |
| `.zip/.rar/.7z/...` | `file-archive` |
| `.pdf` | `file-pdf` |
| `.cs/.js/.py/...` | `file-code` |
| 其他 | `file-generic` |

Bundle 目录（`.app`, `.photoslibrary`, `.pvm` 等）有独立的解析：`SqliteFileIndex.ResolveBundleIconKey(extension)`。

虚拟 AI 条目使用固定键：`ai-face`, `ai-scene`, `ai-object`, `ai-animal`, `ai-location`, `ai-date`。

## 阶段 2：同步渲染 — GetIconSvg

入口：`FileGridView.razor` → `GetIconSvg(FileSystemEntry entry, int size)`

按优先级依次匹配，首个命中即返回：

```
┌─ entry.IsVirtual && ThumbnailUrl 非空?
│  → <img> 人脸缩略图（圆形），class="ai-face-thumbnail"
│
├─ entry.IsVirtual?
│  → FileIconRenderer.RenderAiIcon() 彩色 AI SVG 图标
│
├─ ThumbnailUrl 非空?
│  → <img> 图片缩略图，class="file-list-thumbnail" | "file-grid-thumbnail"
│
├─ IconUrl 在 BlobURL 缓存中?
│  → <img> 应用原生图标，class="app-icon-img"
│
├─ IsDirectory 且非 Bundle?
│  → FileIconRenderer.RenderFolder() 金色文件夹 SVG
│
└─ 默认
   → FileIconRenderer.Render(iconKey, extension) 带扩展名标签的文件 SVG
```

尺寸统一规则：所有 `<img>` 分支使用 `displaySize = size <= 20 ? 20 : 64`。
SVG 分支直接使用原始 `size` 参数（SVG 是矢量可缩放的）。

## 阶段 3：后台异步解析

### 3a. 应用图标解析 — ResolveIconsInBackgroundAsync

```
对 entries 中的 .app bundle：
  ├─ MacFileService.ResolveAppIconsAsync()
  │   └─ 通过 JXA 脚本提取 macOS 原生应用图标
  │   └─ 保存为 PNG 到 icon-cache 目录
  │   └─ 设置 entry.IconUrl = PNG 文件路径
  ├─ 触发 OnPropertyChanged(Entries)
  └─ FileGridView 响应变更 → CreateBlobUrlsForEntriesAsync()
      └─ 读取 PNG → JS createBlobUrl() → 缓存到 _blobUrlCache
      └─ StateHasChanged() → 重新渲染，GetIconSvg 命中 BlobURL 分支
```

### 3b. 图片缩略图解析 — ResolveThumbnailsInBackgroundAsync

```
对 entries 中 IconKey == "file-image" && ThumbnailUrl == null 的条目：
  ├─ 按批次处理（每批 20 个）
  │   └─ MacThumbnailService.GetThumbnailAsync(path, maxPixelSize=128)
  │   └─ 使用 macOS CGImageSource API 生成缩略图
  │   └─ 设置 entry.ThumbnailUrl = "data:image/png;base64,..."
  ├─ 每批完成后触发 OnPropertyChanged(Entries)
  └─ FileGridView 重新渲染，GetIconSvg 命中 ThumbnailUrl 分支
```

### 3c. AI 人脸缩略图解析 — ResolveFaceThumbnailsAsync

```
仅在 AI 人脸聚类顶层视图触发（AiViewModel 内部）：
  ├─ 接收参数：clusters（人脸聚类列表）、entries（已创建的虚拟条目）、onUpdated（通知回调）
  ├─ 对每个 FaceCluster：
  │   └─ MacThumbnailService.GetFaceCropAsync(path, bbox, maxSize=128)
  │   └─ 裁剪人脸区域 → base64 data URI
  │   └─ 设置 cluster.FaceThumbnailUrl = url（更新数据源）
  │   └─ 设置 entry.ThumbnailUrl = url（同步回写到对应 FileSystemEntry）
  │   └─ 调用 onUpdated()
  └─ onUpdated 由 FileListViewModel 传入：() => OnPropertyChanged(nameof(Entries))
     └─ FileGridView 响应 Entries 变更 → 重新渲染，GetIconSvg 命中人脸缩略图分支
```

调用链路：
```
FileListViewModel.HandleAiNavigationAsync
  → AiViewModel.HandleAiNavigationAsync(onEntriesUpdated: () => OnPropertyChanged(nameof(Entries)))
    → AiViewModel.LoadAiTopLevelAsync(onEntriesUpdated)
      → AiViewModel.ResolveFaceThumbnailsAsync(clusters, entries, onUpdated: onEntriesUpdated)
        → 解析完成 → entry.ThumbnailUrl = url → onUpdated() → UI 刷新
```

## 各视图类型的解析流程

| 视图类型 | ApplyEntries | ResolveIcons | ResolveThumbnails | 其他 |
|----------|:---:|:---:|:---:|------|
| 真实文件夹（文件系统） | Y | Y | Y | + TriggerImageAnalysis |
| 真实文件夹（索引缓存） | Y | Y | Y | |
| 垃圾桶 | Y | Y | Y | 通过 LoadDirectoryContentsAsync |
| 应用程序文件夹 | Y | Y | Y | 合并 /System/Applications |
| AI 顶层（虚拟条目） | Y | Y* | Y* | + ResolveFaceThumbnails |
| AI 详情（人脸/分类/搜索） | Y | Y | Y | 真实条目来自索引 |
| 收藏夹 | Y | Y | Y | 真实条目来自索引/文件系统 |
| 压缩包 | Y | - | - | 虚拟条目，无需解析 |

*AI 顶层视图通过 `ResolveRealEntries` 调用，但因条目全为虚拟会被过滤跳过。

## 统一调度机制

`FileListViewModel` 中的调度方式：

**方式 A — LoadDirectoryContentsAsync（真实文件夹、垃圾桶、应用程序文件夹）**
```csharp
ApplyEntries(entries);
_ = ResolveIconsInBackgroundAsync(entries);
_ = ResolveThumbnailsInBackgroundAsync(entries);
_ = TriggerImageAnalysisAsync(entries);
```

**方式 B — ResolveRealEntries 回调包装（AI视图、收藏夹）**
```csharp
// 在回调 lambda 中：
entries => { ApplyEntries(entries); ResolveRealEntries(entries); }
```

`ResolveRealEntries` 内部：
```csharp
private void ResolveRealEntries(IReadOnlyList<FileSystemEntry> entries)
{
    if (!entries.Any(e => !e.IsVirtual)) return;  // 全虚拟则跳过
    _ = ResolveIconsInBackgroundAsync(entries);
    _ = ResolveThumbnailsInBackgroundAsync(entries);
}
```

**方式 B+ — AI 人脸顶层视图额外传递 onEntriesUpdated 回调**
```csharp
// HandleAiNavigationAsync 和 LoadAiTopLevelAsync 调用时：
onEntriesUpdated: () => OnPropertyChanged(nameof(Entries))
```
此回调通过 `HandleAiNavigationAsync` → `LoadAiTopLevelAsync` → `ResolveFaceThumbnailsAsync` 传递，
确保人脸缩略图异步解析完成后能通知 `FileGridView` 重新渲染。

**方式 C — 不解析（压缩包）**
```csharp
entries => ApplyEntries(entries)  // 仅应用，不触发后台解析
```

## 关键文件索引

| 文件 | 职责 |
|------|------|
| `Models/FileSystemEntry.cs` | 数据模型，含 IconKey/IconUrl/ThumbnailUrl |
| `Services/Impl/FileIconResolver.cs` | 扩展名 → IconKey 静态映射 |
| `Models/FileIconRenderer.cs` | IconKey → SVG 渲染（含文件夹、AI 图标） |
| `ViewModels/FileListViewModel.cs` | 协调器：条目加载、后台解析调度 |
| `Components/FileList/FileGridView.razor` | UI 渲染：GetIconSvg 分发 + BlobURL 管理 |
| `Platforms/MacCatalyst/Services/MacFileService.cs` | 文件枚举 + JXA 应用图标提取 |
| `Platforms/MacCatalyst/Services/MacThumbnailService.cs` | CGImageSource 缩略图/人脸裁剪生成 |
| `ViewModels/AiViewModel.cs` | AI 视图条目加载 + 人脸缩略图解析 |
| `ViewModels/CollectionViewModel.cs` | 收藏夹条目加载 |
| `Indexing/SqliteFileIndex.cs` | 索引缓存 + Bundle IconKey 解析 |
