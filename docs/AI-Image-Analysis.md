# MacExplorer AI 图像分析功能设计文档

## 概述

MacExplorer 的 AI 图像分析功能利用 Apple Vision Framework 对用户浏览的图片进行自动后台分析，提取人脸、场景分类、文字 (OCR)、EXIF 元数据（GPS 定位、拍摄日期、相机型号）等信息，并以标签形式持久化到 SQLite 数据库中。用户可以通过 AI 视图按人物、分类、地点、日期、文字等维度浏览和搜索图片。

---

## 技术栈

| 层级 | 技术 |
|------|------|
| 平台 | .NET 10 MAUI + Blazor WebView, Mac Catalyst |
| 图像分析 | Apple Vision Framework (VNDetectFaceRectanglesRequest, VNRecognizeTextRequest, VNClassifyImageRequest, VNGenerateImageFeaturePrintRequest) |
| EXIF 提取 | CGImageSource + CGImageProperties |
| 地理编码 | CLGeocoder (GPS -> 地名反向解析) |
| 数据存储 | SQLite (WAL 模式) + FTS5 全文搜索 |
| 状态管理 | CommunityToolkit.Mvvm ([ObservableProperty], [RelayCommand]) |
| UI | Blazor 组件 + CascadingParameter |
| 缩略图 | CGImageSource + CGBitmapContext (双层缓存) |

---

## 架构总览

```
用户浏览目录
    |
    v
FileListViewModel.LoadDirectoryContentsAsync()
    |
    v
TriggerImageAnalysisAsync(entries)          <-- 后台触发，不阻塞 UI
    |
    +-- 过滤图像文件 (ImageExtensionsForAi)
    +-- GetUnanalyzedFilesAsync() 批量检查状态
    +-- 清理已删除文件的孤立 AI 数据
    |
    v
并发分析 (SemaphoreSlim, 最多 3 个)
    |
    v
MacImageAnalysisService.AnalyzeImageAsync()
    |
    +-- VNDetectFaceRectanglesRequest     --> 人脸检测 + bounding box
    +-- VNGenerateImageFeaturePrintRequest --> 人脸特征向量 (128D)
    +-- VNRecognizeTextRequest            --> OCR 文字识别
    +-- VNClassifyImageRequest            --> 场景/物品/动物分类
    +-- CGImageSource EXIF                --> GPS / 日期 / 相机
    +-- CLGeocoder                        --> GPS 反向地理编码
    |
    v
AiTagService.SaveAnalysisResultAsync()
    |
    +-- DELETE 旧数据 (支持重分析)
    +-- INSERT ai_tags (分类/文字/地点/日期/相机)
    +-- INSERT face_observations (bounding box + feature print)
    +-- INSERT OR REPLACE ai_analysis_status (版本号)
    |
    v
AiTagService.RunClusteringAsync()
    |
    +-- 欧几里得距离聚类 (阈值 0.5)
    +-- 更新 face_clusters / face_observations.cluster_id
```

---

## 数据模型

### 核心模型

```csharp
// 图像分析结果 (运行时, 不持久化)
public class ImageAnalysisResult
{
    public List<DetectedFace> Faces { get; init; }              // 检测到的人脸
    public List<RecognizedText> RecognizedTexts { get; init; }  // OCR 文本
    public List<ClassificationLabel> Classifications { get; init; } // 场景分类
    public LocationInfo? Location { get; init; }                // GPS 位置
    public PhotoDateInfo? DateInfo { get; init; }               // 拍摄日期
    public string? CameraInfo { get; init; }                    // 相机型号
}

// 检测到的人脸
public class DetectedFace
{
    public float BoundingBoxX { get; init; }  // 归一化坐标 [0,1], 原点左下
    public float BoundingBoxY { get; init; }
    public float BoundingBoxW { get; init; }
    public float BoundingBoxH { get; init; }
    public byte[]? FeaturePrint { get; init; } // 128D 浮点特征向量
}

// OCR 识别文本
public class RecognizedText
{
    public string Text { get; init; }
    public float Confidence { get; init; }
    public List<string> Keywords { get; init; } // 正则分词提取的关键词
}

// 场景/物品分类标签
public class ClassificationLabel
{
    public string Identifier { get; init; }   // Vision 原始标识符
    public string TagType { get; init; }      // object / scene / animal
    public string DisplayName { get; init; }  // 映射后的中文/英文名
    public float Confidence { get; init; }
}

// GPS 位置信息
public class LocationInfo
{
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string? PlaceName { get; init; }   // 反向地理编码后的地名
}

// 拍摄日期信息
public class PhotoDateInfo
{
    public DateTime TakenAt { get; init; }
    public string YearMonth { get; init; }    // "2024-03"
    public string Day { get; init; }          // "2024-03-15"
}
```

### 持久化模型

```csharp
// AI 标签 (持久化到 ai_tags 表)
public class AiTag
{
    public int Id { get; init; }
    public string FilePath { get; init; }
    public string TagType { get; init; }   // face/object/scene/animal/text/text_summary/location/date/date_day/camera
    public string TagValue { get; init; }
    public float Confidence { get; init; }
    public DateTime CreatedAt { get; init; }
}

// AI 分类聚合 (虚拟, 由 GROUP BY 查询生成)
public class AiCategory
{
    public string TagType { get; init; }
    public string TagValue { get; init; }
    public int FileCount { get; init; }
}

// 人脸聚类
public class FaceCluster
{
    public int Id { get; init; }
    public string? DisplayName { get; set; }         // 用户自定义名称
    public int? RepresentativeFaceId { get; set; }   // 代表人脸 ID
    public string? RepresentativeFacePath { get; set; }
    public float BoundingBoxX/Y/W/H { get; init; }  // 代表人脸 bounding box
    public int FaceCount { get; init; }
    public string? FaceThumbnailUrl { get; set; }    // 裁剪后的 data:image/png;base64 URL
}

// AI 视图模式
public enum AiViewMode { People, Categories, Locations, Dates, TextSearch }

// 虚拟文件夹条目 (FileSystemEntry 扩展字段)
// IsVirtual = true, VirtualFolderType = "face"/"scene"/"object"/..., VirtualItemCount = 文件数
// FullPath = AI 哨兵路径, 如 "__ai:face:42"
```

---

## 数据库 Schema (v6)

```sql
-- AI 分析状态追踪 (逐文件粒度, 支持断点续传)
CREATE TABLE ai_analysis_status (
    file_path TEXT PRIMARY KEY,
    file_modified_at INTEGER NOT NULL,     -- 文件修改时间 (Ticks)
    analyzed_at INTEGER NOT NULL,          -- 分析完成时间 (Ticks)
    analysis_version INTEGER NOT NULL DEFAULT 1  -- 分析算法版本号
);

-- AI 标签 (所有 AI 检测结果)
CREATE TABLE ai_tags (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    file_path TEXT NOT NULL,
    tag_type TEXT NOT NULL,                -- 标签类型 (10 种)
    tag_value TEXT NOT NULL,               -- 标签值
    confidence REAL NOT NULL DEFAULT 0,    -- 置信度 [0, 1]
    created_at INTEGER NOT NULL
);
CREATE INDEX idx_ai_tags_file_path ON ai_tags(file_path);
CREATE INDEX idx_ai_tags_type_value ON ai_tags(tag_type, tag_value);

-- FTS5 全文搜索 (用于 BreadcrumbBar 搜索建议)
CREATE VIRTUAL TABLE ai_tags_fts USING fts5(
    tag_value,
    content='ai_tags',
    content_rowid='id',
    tokenize='unicode61'  -- 降级: trigram -> unicode61 -> porter
);
-- 触发器自动同步 INSERT/DELETE/UPDATE

-- 人脸观测 (原始检测数据)
CREATE TABLE face_observations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    file_path TEXT NOT NULL,
    cluster_id INTEGER,                    -- 外键 -> face_clusters.id
    bounding_box_x REAL NOT NULL,          -- 归一化坐标 [0,1]
    bounding_box_y REAL NOT NULL,
    bounding_box_w REAL NOT NULL,
    bounding_box_h REAL NOT NULL,
    feature_print BLOB,                    -- 128D 浮点特征向量
    created_at INTEGER NOT NULL
);
CREATE INDEX idx_fo_file_path ON face_observations(file_path);
CREATE INDEX idx_fo_cluster_id ON face_observations(cluster_id);

-- 人脸聚类 (命名分组)
CREATE TABLE face_clusters (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    display_name TEXT,                     -- 用户为人物设置的名称
    representative_face_id INTEGER,        -- 代表人脸 (face_observations.id)
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL
);
```

### 标签类型 (tag_type) 一览

| tag_type | 说明 | 来源 | 示例 |
|----------|------|------|------|
| `object` | 物品 | VNClassifyImageRequest | laptop, cup, book |
| `scene` | 场景 | VNClassifyImageRequest | beach, mountain, office |
| `animal` | 动物 | VNClassifyImageRequest | cat, dog, bird |
| `text` | OCR 原文 | VNRecognizeTextRequest | "Hello World" |
| `text_summary` | 关键词 | 正则分词 | "Hello", "World" |
| `location` | 地点 | EXIF GPS + CLGeocoder | "北京市朝阳区" |
| `date` | 年月 | EXIF DateTimeOriginal | "2024-03" |
| `date_day` | 完整日期 | EXIF DateTimeOriginal | "2024-03-15" |
| `camera` | 相机 | EXIF Make + Model | "Apple iPhone 15 Pro" |
| `face` | 人脸标记 | 聚类命名时生成 | (用户命名) |

---

## 服务接口

### IImageAnalysisService

```csharp
public interface IImageAnalysisService
{
    Task<ImageAnalysisResult> AnalyzeImageAsync(string filePath, CancellationToken ct = default);
    float ComputeFaceDistance(byte[] print1, byte[] print2);
}
```

**实现**: `MacImageAnalysisService` (Platforms/MacCatalyst/Services/)

- `AnalyzeImageAsync`: 综合调用 Vision Framework 的 4 种请求 + EXIF 提取 + 地理编码
- `ComputeFaceDistance`: 计算两个 128D 特征向量的欧几里得距离
- Vision bounding box 使用 Core Graphics 坐标系 (原点在左下角, 归一化 [0,1])
- GPS 反向地理编码使用内存缓存 (按经纬度整数格子去重)
- 关键词提取使用 `Regex.Split(@"[\s\p{P}\p{S}]+")` 替代 NLTagger (避免 .NET 绑定兼容性问题)

### IAiTagService

```csharp
public interface IAiTagService : IDisposable
{
    // 分析状态
    Task<bool> IsFileAnalyzedAsync(string filePath, long fileModifiedTicks);
    Task<IReadOnlyList<(string Path, long ModifiedTicks)>> GetUnanalyzedFilesAsync(...);
    Task<IReadOnlyList<string>> GetAnalyzedPathsInDirectoryAsync(string parentPath);

    // 保存与删除
    Task SaveAnalysisResultAsync(string filePath, long fileModifiedTicks, ImageAnalysisResult result);
    Task DeleteAnalysisForFileAsync(string filePath);
    Task DeleteAnalysisForFilesAsync(IReadOnlyList<string> filePaths);

    // 标签查询
    Task<IReadOnlyList<AiTag>> GetTagsForFileAsync(string filePath);
    Task<IReadOnlyList<string>> SearchByTagAsync(string tagValue, string? tagType = null, int limit = 200);
    Task<IReadOnlyList<AiCategory>> SearchCategoriesAsync(string query, int limit = 5);
    Task<IReadOnlyList<AiCategory>> GetPopularTextTagsAsync(int limit = 40, int minLength = 2);

    // 人脸聚类
    Task<IReadOnlyList<FaceCluster>> GetAllFaceClustersAsync();
    Task<IReadOnlyList<string>> GetFilePathsForClusterAsync(int clusterId);
    Task SetClusterNameAsync(int clusterId, string name);
    Task MergeClustersAsync(int targetClusterId, int sourceClusterId);
    Task RunClusteringAsync(float distanceThreshold = 0.5f);

    // 路径更新 (重命名时同步)
    Task UpdateFilePathAsync(string oldPath, string newPath);

    // 分类
    Task<IReadOnlyList<AiCategory>> GetCategoriesByTypeAsync(string tagType);
    Task<IReadOnlyList<string>> GetAllTagTypesAsync();
    Task<IReadOnlyList<string>> GetFilePathsForCategoryAsync(string tagType, string tagValue);
}
```

**实现**: `AiTagService` (Services/Impl/)

- SQLite WAL 模式 + `busy_timeout=5000`
- `SaveAnalysisResultAsync` 在事务中先 DELETE 旧数据再 INSERT 新数据
- `RunClusteringAsync` 使用凝聚层次聚类: 未分配人脸与现有聚类的代表人脸比较欧几里得距离, 低于阈值则归入, 否则创建新簇
- `SearchCategoriesAsync` 使用 `LIKE %query%` 匹配 tag_value, 按文件数排序
- `GetPopularTextTagsAsync` 聚合 `text` + `text_summary` 标签, 按关联文件数降序, 过滤短于 minLength 的噪音值, 用于文字搜索页的热门词元展示

### IThumbnailService

```csharp
public interface IThumbnailService
{
    Task<byte[]?> GetThumbnailAsync(string filePath, int maxPixelSize, CancellationToken ct = default);
    Task<byte[]?> GetFaceCropAsync(string filePath, float bx, float by, float bw, float bh,
                                    int maxPixelSize = 128, CancellationToken ct = default);
    bool IsImageFile(string extension);
    void EvictFromCache(string filePath);
}
```

**实现**: `MacThumbnailService` (Platforms/MacCatalyst/Services/)

- 双层缓存: 内存 (ConcurrentDictionary, 最多 500 条) + 磁盘 (SHA256 哈希命名 .png)
- `GetFaceCropAsync`: 根据 Vision bounding box 裁剪人脸区域 (含 30% 边距扩展), Y 轴翻转 (Vision 左下 -> CG 左上), 缩放到 maxPixelSize

---

## AI 哨兵路径架构

AI 视图导航采用**哨兵路径 (Sentinel Path)** 模式，与归档浏览的 `__archive:` 模式一致。所有 AI 路径以 `__ai:` 前缀标识，统一通过 `NavigateToAsync` 拦截和分发。

### AiPathHelper (Services/AiPathHelper.cs)

```csharp
public static class AiPathHelper
{
    private const string Prefix = "__ai:";

    public static bool IsAiPath(string path);            // 判断是否为 AI 哨兵路径
    public static string GetTopLevelPath(AiViewMode mode); // 生成顶层路径
    public static AiPathInfo Parse(string sentinelPath);   // 解析路径为结构化信息
    public static string GetParentPath(string sentinelPath); // 获取父路径 (用于 NavigateUp)
    public static string GetModeName(AiViewMode mode);     // 获取模式中文名
}

public readonly record struct AiPathInfo(
    bool IsTopLevel,          // 是否为顶层视图 (人物列表 / 分类列表)
    AiViewMode Mode,          // 视图模式
    bool IsFaceDetail = false, // 是否为人脸详情
    int? FaceClusterId = null, // 人脸聚类 ID
    string? TagType = null,    // 标签类型 (scene/object/animal/location/date)
    string? TagValue = null);  // 标签值
```

### 路径格式

| 路径 | 含义 | 解析结果 |
|------|------|----------|
| `__ai:people` | 人物列表 (顶层) | `IsTopLevel=true, Mode=People` |
| `__ai:categories` | 分类列表 (顶层) | `IsTopLevel=true, Mode=Categories` |
| `__ai:locations` | 地点列表 (顶层) | `IsTopLevel=true, Mode=Locations` |
| `__ai:dates` | 日期列表 (顶层) | `IsTopLevel=true, Mode=Dates` |
| `__ai:textsearch` | 文字搜索 (顶层) | `IsTopLevel=true, Mode=TextSearch` |
| `__ai:face:42` | 人脸聚类 #42 详情 | `IsFaceDetail=true, FaceClusterId=42, Mode=People` |
| `__ai:object:laptop` | 物品标签 "laptop" 详情 | `TagType="object", TagValue="laptop", Mode=Categories` |
| `__ai:scene:beach` | 场景标签 "beach" 详情 | `TagType="scene", TagValue="beach", Mode=Categories` |
| `__ai:location:北京` | 地点 "北京" 详情 | `TagType="location", TagValue="北京", Mode=Locations` |
| `__ai:date:2024-03` | 日期 "2024-03" 详情 | `TagType="date", TagValue="2024-03", Mode=Dates` |

### 虚拟文件夹条目

顶层 AI 视图将数据呈现为虚拟文件夹 (`FileSystemEntry.IsVirtual = true`)，复用 `FileGridView` 渲染:

```csharp
// 人脸聚类 → 虚拟条目
private static FileSystemEntry CreateVirtualEntry(FaceCluster cluster) => new()
{
    FullPath = $"__ai:face:{cluster.Id}",    // 哨兵路径作为 FullPath
    Name = cluster.DisplayName ?? "未命名",
    IsDirectory = true,
    IconKey = "ai-face",
    ThumbnailUrl = cluster.FaceThumbnailUrl,  // 人脸裁剪缩略图
    Size = cluster.FaceCount,
    IsVirtual = true,
    VirtualFolderType = "face",
    VirtualItemCount = cluster.FaceCount,
};

// AI 分类 → 虚拟条目
private static FileSystemEntry CreateVirtualEntry(AiCategory category) => new()
{
    FullPath = $"__ai:{category.TagType}:{category.TagValue}",
    Name = category.TagValue,
    IsDirectory = true,
    IconKey = $"ai-{category.TagType}",
    Size = category.FileCount,
    IsVirtual = true,
    VirtualFolderType = category.TagType,
    VirtualItemCount = category.FileCount,
};
```

### 导航流程

```
用户操作 (侧边栏点击 / 双击虚拟文件夹 / 面包屑点击 / 前进后退)
    |
    v
NavigateToAsync(path)                     <-- 统一入口
    |
    +-- IsArchivePath? --> NavigateToArchiveAsync()
    +-- IsAiPath?      --> HandleAiNavigationAsync()     <-- AI 路径拦截
    +-- Otherwise      --> 文件系统导航 (清除 IsAiView)
    |
    v
HandleAiNavigationAsync(sentinelPath)
    |
    +-- AiPathHelper.Parse()              --> 解析路径
    +-- 重复导航守卫 (CurrentPath == sentinelPath)
    +-- 清除互斥状态 (IsHomePage, IsCollectionView, IsArchiveView, IsSearchMode)
    +-- IsAiView = true, AiViewMode = info.Mode
    +-- 推入历史栈 (_historyStack + _historyIndex)
    +-- CurrentPath = sentinelPath
    |
    +-- IsTopLevel?
    |       +-- LoadAiTopLevelAsync(mode)  --> 加载虚拟文件夹列表
    +-- IsFaceDetail?
    |       +-- LoadFaceClusterEntriesAsync(clusterId) --> 加载人脸照片
    +-- Otherwise
    |       +-- LoadAiCategoryEntriesAsync(tagType, tagValue) --> 加载分类照片
    |
    +-- UpdateBreadcrumbs()               --> "AI 智能 > 人物 > 张三"
    +-- IsLoading = false
```

### 导航支持

| 功能 | 实现方式 |
|------|----------|
| 前进/后退 | `_historyStack` + `_historyIndex`，AI 哨兵路径与文件系统路径统一存储 |
| 向上导航 | `AiPathHelper.GetParentPath()`: 详情页 → 顶层页，顶层页 → GoHome() |
| 面包屑 | `UpdateBreadcrumbs()` AI 分支: "AI 智能 > 模式名 > 详情名" |
| 刷新 | `RefreshAsync()` 根据 `AiPathHelper.Parse(CurrentPath)` 重新加载当前视图 |
| 双击进入 | `OpenEntryAsync`: `entry.IsVirtual` → `NavigateToAsync(entry.FullPath)` |

---

## UI 组件

### AI 视图组件 (`Components/AiView/`)

| 组件 | 功能 |
|------|------|
| `AiViewHost.razor` | AI 视图容器, 监听 `AiViewMode` 变化, 切换 `FileGridView` / `TextSearchView` |
| `TextSearchView.razor` | OCR 文字搜索输入框 + 热门词元 (chips) + 结果文件列表 |

> **注意**: 之前的 `PeopleGrid.razor`、`CategoriesGrid.razor`、`LocationsGrid.razor`、`DatesTimeline.razor` 已被移除，统一由 `FileGridView` 通过虚拟文件夹条目渲染。

### AiViewHost 结构

```razor
@implements IDisposable

<div class="ai-view-host">
    <div class="ai-view-content">
        @if (ViewModel.AiViewMode == AiViewMode.TextSearch)
        {
            <TextSearchView />
        }
        else
        {
            <FileGridView />    @* 复用已有的文件网格, 渲染虚拟文件夹 *@
        }
    </div>
</div>

@code {
    [CascadingParameter] public FileListViewModel ViewModel { get; set; } = null!;
    // 仅订阅 AiViewMode 变化以切换 FileGridView / TextSearchView
}
```

### 组件通信模式

```
Home.razor (CascadingValue: ViewModel)
    |
    +-- @if (!ViewModel.IsHomePage)
    |       +-- FinderNavBar        --> 包含 BreadcrumbBar (AI 面包屑)
    |       +-- FinderToolbar       --> 前进/后退/向上按钮
    |
    +-- @if (ViewModel.IsHomePage)
    |       +-- BreadcrumbBar + HomeView
    +-- @else if (ViewModel.IsAiView)
    |       +-- AiViewHost.razor
    |               |
    |               +-- AiViewMode == TextSearch --> TextSearchView.razor
    |               +-- Otherwise                --> FileGridView.razor (虚拟文件夹)
    +-- @else
    |       +-- FileGridView.razor (文件系统)
    |
    +-- FinderSidebar.razor
            |
            +-- "AI 智能" 区域 (5 个导航项 + 启用/禁用开关)
            +-- @onclick → ViewModel.NavigateToAiViewAsync(mode) → NavigateToAsync("__ai:xxx")
```

### 侧边栏 AI 区域

- 5 个导航项: 人物 / 分类 / 地点 / 日期 / 文字搜索
- 每项有独立颜色的 SVG 图标 (#EC4899 / #8B5CF6 / #EF4444 / #F59E0B / #3B82F6)
- 标题栏右侧有 toggle 开关控制 `IsAiAnalysisEnabled`
- active 状态与当前 `IsAiView && AiViewMode` 联动

### TextSearchView 热门词元

进入文字搜索页面时，搜索框下方展示**热门文字词元 (Text Token Chips)**，让用户快速了解可搜索的内容。

**数据来源**: `GetPopularTextTagsAsync` 查询 `ai_tags` 表中 `tag_type IN ('text', 'text_summary')` 的标签, 按 `COUNT(DISTINCT file_path)` 降序, 过滤 `LENGTH(tag_value) < 2` 的噪音, 默认返回前 40 条。

**UI 状态机**:

```
TextSearchView 四种显示状态:
1. _hasSearched && Entries.Count == 0  --> 搜索无结果 (空状态 + 提示)
2. _hasSearched                        --> 搜索有结果 (FileGridView)
3. TextTokens.Count > 0               --> 热门词元 (chips 列表)
4. else                                --> 空状态提示 (无 AI 数据)
```

**词元交互**:

| 用户操作 | 系统响应 |
|----------|----------|
| 进入文字搜索 | `LoadAiTopLevelAsync(TextSearch)` 加载词元到 `TextTokens` |
| 点击词元 | 搜索框填入文字 → 执行 `SearchAiTagsCommand` → 显示结果 |
| 按 Enter 搜索 | 隐藏词元, 显示搜索结果 |
| 点击清除按钮 | `_hasSearched = false`, 词元重新显示 |
| 刷新页面 | 重新加载词元 |
| 离开 AI 视图 | `TextTokens.Clear()` 释放内存 |

**组件生命周期**: TextSearchView 实现 `IDisposable`, 在 `OnInitialized` 中订阅 `TextTokens.CollectionChanged` 以响应异步数据加载, 在 `Dispose` 中取消订阅。

**CSS 样式**: 词元采用 pill 胶囊按钮样式 (`.ai-text-token`), `border-radius: var(--radius-full)`, flex-wrap 自动换行, hover 时使用 accent 色调高亮, 长文本 ellipsis 截断 (`max-width: 200px`)。每个词元右侧显示关联文件数 (`.ai-text-token-count`)。

### BreadcrumbBar 集成

**面包屑显示**: AI 视图下面包屑显示层级结构:

```
AI 智能 > 人物                  (顶层, 人物列表)
AI 智能 > 人物 > 张三           (详情, 张三的照片)
AI 智能 > 分类 > beach          (详情, beach 分类的照片)
AI 智能 > 地点 > 北京市朝阳区    (详情, 某地点的照片)
```

- 非末级面包屑段可点击导航 (如点击 "人物" 回到人物列表)
- AI 视图下禁用路径输入框 (点击面包屑不会激活输入模式)

**搜索集成**: 路径输入框在输入时执行 3 层搜索策略:

1. **路径匹配**: 输入以 `/` 或 `~` 开头时列出目录内容
2. **FTS5 索引**: 从文件名索引搜索匹配项
3. **AI 标签搜索**: 输入 >= 2 字符且非路径时, 调用 `SearchCategoriesAsync` 搜索标签类别

AI 建议以分割线隔开, 显示标签类型颜色图标 + 标签值 + 匹配文件数。点击后导航到对应 AI 分类视图。

### MacSearchService 集成

常规搜索 (`SearchAsync`) 在 FTS5 索引搜索之后, 追加 AI 标签匹配的文件:

```
FTS5 文件名搜索 --> yield 结果 (记录已返回路径)
        |
        v
AI 标签搜索 (SearchByTagAsync) --> 去重后 yield 额外匹配文件
```

---

## ViewModel AI 导航 (FileListViewModel)

### 核心属性

```csharp
[ObservableProperty] private bool _isAiView;                    // 当前是否在 AI 视图
[ObservableProperty] private AiViewMode _aiViewMode;             // AI 视图模式
[ObservableProperty] private int? _currentFaceClusterId;         // 当前人脸聚类 ID
[ObservableProperty] private string? _currentAiContextLabel;     // 当前 AI 上下文标签 (详情名)
[ObservableProperty] private bool _isAiAnalysisEnabled = true;   // AI 分析开关
public ObservableCollection<FaceCluster> FaceClusters { get; }   // 人脸聚类缓存
public ObservableCollection<AiCategory> AiCategories { get; }    // 分类缓存
public ObservableCollection<AiCategory> TextTokens { get; }      // 文字搜索热门词元
```

### 薄包装方法

```csharp
[RelayCommand]
public async Task NavigateToAiViewAsync(AiViewMode mode)
    => await NavigateToAsync(AiPathHelper.GetTopLevelPath(mode));

[RelayCommand]
public async Task NavigateToFaceClusterAsync(int clusterId)
    => await NavigateToAsync($"__ai:face:{clusterId}");

public async Task NavigateToAiCategoryAsync(string tagType, string tagValue)
    => await NavigateToAsync($"__ai:{tagType}:{tagValue}");
```

### 数据加载方法

| 方法 | 触发条件 | 数据来源 | 设置 |
|------|----------|----------|------|
| `LoadAiTopLevelAsync(People)` | 顶层人物视图 | `GetAllFaceClustersAsync()` → 虚拟条目 | `GroupField=None` |
| `LoadAiTopLevelAsync(Categories)` | 顶层分类视图 | `GetCategoriesByTypeAsync("scene"/"object"/"animal")` → 虚拟条目 | `GroupField=Type` (按类型分组) |
| `LoadAiTopLevelAsync(Locations)` | 顶层地点视图 | `GetCategoriesByTypeAsync("location")` → 虚拟条目 | `GroupField=None` |
| `LoadAiTopLevelAsync(Dates)` | 顶层日期视图 | `GetCategoriesByTypeAsync("date")` → 虚拟条目 | `GroupField=None` |
| `LoadAiTopLevelAsync(TextSearch)` | 顶层文字搜索视图 | `GetPopularTextTagsAsync()` → TextTokens 热门词元 | 清空 Entries, 加载词元 |
| `LoadFaceClusterEntriesAsync(id)` | 人脸详情 | `GetFilePathsForClusterAsync(id)` → 真实文件条目 | `GroupField=None` |
| `LoadAiCategoryEntriesAsync(type, value)` | 分类详情 | `GetFilePathsForCategoryAsync(type, value)` → 真实文件条目 | `GroupField=None` |

### _rawEntries 同步机制

`GroupField` 属性变化会自动触发 `OnGroupFieldChanged` → `ApplySortAndGroup()`，该方法从 `_rawEntries` 重建 `Entries` 和 `Groups`。所有 AI 数据加载方法必须在设置 `GroupField` **之前**更新 `_rawEntries`，否则 `ApplySortAndGroup` 会用旧数据覆盖 `Entries`。

```csharp
// 正确顺序 (以 People 为例):
var peopleEntries = clusters.Select(CreateVirtualEntry).ToList();
_rawEntries = peopleEntries;              // 1. 先更新 _rawEntries
GroupField = GroupField.None;              // 2. 再设置 GroupField (可能触发 ApplySortAndGroup)
Entries = new ObservableCollection<>(peopleEntries); // 3. 显式设置 Entries (GroupField 未变时保底)
SelectedEntries.Clear();
```

---

## 关键机制

### 1. 断点续传

- `ai_analysis_status` 表逐文件记录分析状态
- `GetUnanalyzedFilesAsync` 批量对比文件路径 + 修改时间 + 版本号
- 每个文件分析完成后立即 `SaveAnalysisResultAsync`, 中途中断不影响已完成的文件
- 下次浏览同一目录时自动跳过已分析的文件

### 2. 版本升级

- `AiTagService` 中定义 `CurrentAnalysisVersion = 1` 常量
- `IsFileAnalyzedAsync` 检查 `version >= CurrentAnalysisVersion`
- `GetUnanalyzedFilesAsync` 检查 `version < CurrentAnalysisVersion`
- `SaveAnalysisResultAsync` 先 `DELETE` 旧数据再写入新数据 (含新版本号)
- **升级方式**: 只需将 `CurrentAnalysisVersion` 递增, 所有文件下次浏览时自动重新分析

### 3. 人脸聚类算法

```
1. 加载所有 cluster_id IS NULL 且有 feature_print 的 face_observations
2. 加载现有 face_clusters 的代表人脸特征向量
3. 对每个未分配人脸:
   a. 与所有聚类代表计算欧几里得距离
   b. 距离 < threshold (0.5) --> 归入最近的聚类
   c. 距离 >= threshold --> 创建新聚类, 该人脸作为代表
4. 更新 face_observations.cluster_id
5. 更新 face_clusters.representative_face_id
```

### 4. 人脸裁剪缩略图

```
FaceCluster.RepresentativeFacePath + BoundingBox
    |
    v
MacThumbnailService.GetFaceCropAsync()
    |
    +-- CGImageSource 加载原始图像
    +-- Vision bbox (归一化, 左下原点) --> 像素坐标 (左上原点)
    +-- 扩展 30% 边距
    +-- CGImage.WithImageInRect() 裁剪
    +-- CGBitmapContext 缩放到 128px
    +-- 输出 PNG bytes
    |
    v
FaceThumbnailUrl = "data:image/png;base64,..."
```

### 5. 启用/禁用控制

- `FileListViewModel.IsAiAnalysisEnabled` (默认 true)
- 持久化到 `app_settings` 表 (key: "IsAiAnalysisEnabled")
- `TriggerImageAnalysisAsync` 开头检查此开关
- 侧边栏 "AI 智能" 标题旁的 toggle 开关控制
- 关闭后停止自动分析, 但已分析的数据仍可浏览

### 6. 状态互斥

AI 视图进入时清除所有互斥状态:

```csharp
IsHomePage = false;
IsCollectionView = false;
IsArchiveView = false;
CurrentArchivePath = null;
CurrentArchiveInternalPath = "";
IsSearchMode = false;
IsAiView = true;
```

退出 AI 视图 (导航到文件系统路径) 时清除 AI 状态:

```csharp
IsAiView = false;
CurrentFaceClusterId = null;
CurrentAiContextLabel = null;
TextTokens.Clear();
```

**所有导航方法的互斥状态清除**:

| 导航方法 | 清除 AI 状态 | 清除收藏夹状态 | 清除归档状态 | 清除搜索状态 |
|----------|:---:|:---:|:---:|:---:|
| `NavigateToAsync` (文件系统) | ✅ | ✅ | ✅ | - |
| `HandleAiNavigationAsync` | 设置 AI | ✅ | ✅ | ✅ |
| `NavigateToCollectionAsync` | ✅ | 设置收藏夹 | ✅ | ✅ |
| `NavigateToArchiveAsync` | ✅ | ✅ | 设置归档 | ✅ |

### 7. AI 视图重命名

AI 视图中的重命名支持两种条目:

**虚拟文件夹 (人脸聚类) 重命名**:
- 右键菜单 → "重命名" → `RequestRename(entry)` → 内联编辑
- `CommitRename` → `RenameEntryAsync` → 检测 `entry.IsVirtual && VirtualFolderType == "face"`
- 调用 `RenameFaceClusterAsync` → 数据库更新 + 内存中替换条目 (FileSystemEntry.Name 为 init-only)

**真实文件重命名 (AI 分类详情页)**:
- `RenameEntryAsync` → `IsAiView` 分支 → 磁盘重命名 + `UpdateFilePathAsync` 更新数据库路径 + 内存替换条目
- `UpdateFilePathAsync` 在事务中更新 `ai_tags`、`face_observations`、`ai_analysis_status` 三张表的 `file_path`

**重命名焦点保护机制**:

WKWebView 中, 后台任务 (`ResolveFaceThumbnailsAsync`、`ResolveThumbnailsInBackgroundAsync`、`ResolveIconsInBackgroundAsync`) 会触发 `OnPropertyChanged(nameof(Entries))`, 导致 `StateHasChanged` 重建 DOM, 输入框失焦 → `onblur` → `CommitRename`。

FileGridView 在以下三处添加重命名保护, 跳过 `_renamingEntry != null` 期间的重渲染:

```csharp
// 1. Entries 属性变更 (后台缩略图/图标加载)
else if (e.PropertyName == nameof(ViewModel.Entries))
{
    if (_renamingEntry != null) return;
    // ...
}

// 2. 通用属性变更 (IsLoading, GroupField 等)
if (e.PropertyName is nameof(ViewModel.IsLoading) or ...)
{
    if (_renamingEntry != null) return;
    InvokeAsync(StateHasChanged);
}

// 3. 选中项变更
private async void OnSelectionChanged(...)
{
    if (_renamingEntry != null) return;
    await InvokeAsync(StateHasChanged);
}
```

`OnRenameRequested` 仅调用 `StartRename(entry)`, 不额外调用 `StateHasChanged` 和 `scrollToSelected`, 避免与 `FocusRenameInput` 中的 `focusAndSelect` JS 调用竞争导致 WKWebView 焦点丢失。

### 8. AI 视图右键菜单

| 条目类型 | 右键菜单项 |
|----------|-----------|
| 虚拟文件夹 (人脸) | 打开、重命名 |
| 虚拟文件夹 (其他) | 打开 |
| 真实文件 (AI 分类详情) | 标准文件菜单 |
| 空白区域 | 不显示菜单 (同收藏夹行为) |

---

## 文件清单

### 模型 (Models/)

| 文件 | 说明 | 状态 |
|------|------|------|
| `ImageAnalysisResult.cs` | 分析结果聚合 | 新增 |
| `DetectedFace.cs` | 人脸检测数据 (bbox + 特征向量) | 新增 |
| `RecognizedText.cs` | OCR 文本 + 关键词 | 新增 |
| `ClassificationLabel.cs` | 场景分类标签 | 新增 |
| `LocationInfo.cs` | GPS 位置信息 | 新增 |
| `PhotoDateInfo.cs` | 拍摄日期 | 新增 |
| `AiTag.cs` | 持久化标签 | 新增 |
| `AiCategory.cs` | 标签分类聚合 | 新增 |
| `FaceCluster.cs` | 人脸聚类 (含 bbox + 缩略图 URL) | 新增 |
| `AiViewMode.cs` | 视图模式枚举 | 新增 |
| `FileSystemEntry.cs` | 文件条目 (增加 IsVirtual, VirtualFolderType, VirtualFolderKey, VirtualItemCount 字段) | 修改 |
| `FileIconRenderer.cs` | 文件图标渲染 (增加 AI 虚拟文件夹图标) | 修改 |
| `ImageMetadata.cs` | 图像元数据 (增加 AI 标签字段) | 修改 |
| `ContextMenuAction.cs` | 右键菜单动作 (AI 相关菜单项) | 修改 |
| `PinnedFolder.cs` | 固定文件夹模型 | 新增 |

### 服务接口 (Services/)

| 文件 | 说明 | 状态 |
|------|------|------|
| `IImageAnalysisService.cs` | 图像分析引擎接口 | 新增 |
| `IAiTagService.cs` | AI 标签数据访问接口 | 新增 |
| `IThumbnailService.cs` | 缩略图服务接口 (增加 GetFaceCropAsync) | 修改 |
| `IFileService.cs` | 文件服务接口 (增加方法) | 修改 |
| `AiPathHelper.cs` | AI 哨兵路径工具类 (解析/构建/父路径) | 新增 |
| `INativeContextMenuService.cs` | 原生右键菜单接口 | 新增 |
| `IPinnedFolderService.cs` | 固定文件夹接口 | 新增 |
| `FileTemplateProvider.cs` | 文件模板服务 | 新增 |

### 服务实现

| 文件 | 说明 | 状态 |
|------|------|------|
| `Services/Impl/AiTagService.cs` | SQLite 实现, 聚类算法 | 新增 |
| `Services/Impl/PinnedFolderService.cs` | 固定文件夹实现 | 新增 |
| `Platforms/MacCatalyst/Services/MacImageAnalysisService.cs` | Vision Framework 实现 | 新增 |
| `Platforms/MacCatalyst/Services/MacThumbnailService.cs` | CGImage 缩略图 + 人脸裁剪 | 修改 |
| `Platforms/MacCatalyst/Services/MacSearchService.cs` | 搜索服务 (含 AI 标签扩展) | 修改 |
| `Platforms/MacCatalyst/Services/MacFileService.cs` | 文件服务 (增加方法) | 修改 |
| `Platforms/MacCatalyst/Services/MacMetadataService.cs` | 元数据服务 (AI 标签展示) | 修改 |
| `Platforms/MacCatalyst/Services/MacContextMenuService.cs` | 右键菜单 (AI 相关项) | 修改 |
| `Platforms/MacCatalyst/Services/MacNativeContextMenuService.cs` | 原生 NSMenu 实现 | 新增 |
| `Platforms/MacCatalyst/Handlers/ContextMenuHelper.cs` | 右键菜单辅助 | 新增 |

### UI 组件

| 文件 | 说明 | 状态 |
|------|------|------|
| `Components/AiView/AiViewHost.razor` | AI 视图容器 (FileGridView / TextSearchView 切换) | 新增 |
| `Components/AiView/TextSearchView.razor` | 文字搜索 | 新增 |
| `Components/Sidebar/FinderSidebar.razor` | 侧边栏 (AI 智能区域 + 5 个导航项) | 修改 |
| `Components/Breadcrumb/BreadcrumbBar.razor` | 面包屑 (AI 搜索建议 + AI 路径输入保护) | 修改 |
| `Components/FileList/FileGridView.razor` | 文件网格 (虚拟文件夹渲染) | 修改 |
| `Components/Toolbar/FinderToolbar.razor` | 工具栏 (AI 相关操作) | 修改 |
| `Components/Pages/Home.razor` | 主页面 (AI 视图路由 + PropertyChanged 订阅) | 修改 |
| `Components/_Imports.razor` | 命名空间导入 (AI 模型) | 修改 |

### 其他

| 文件 | 说明 | 状态 |
|------|------|------|
| `Indexing/SqliteSchema.cs` | 数据库 v6 迁移 (AI 表 + FTS5 + 触发器) | 修改 |
| `Indexing/SqliteFileIndex.cs` | 文件索引 (AI 清理接口) | 修改 |
| `ViewModels/FileListViewModel.cs` | AI 属性 + 哨兵路径导航 + 数据加载 + 面包屑 + 历史栈 | 修改 |
| `wwwroot/css/app.css` | AI 视图 CSS 样式 (虚拟文件夹图标等) | 修改 |
| `MauiProgram.cs` | DI 注册 (IImageAnalysisService, IAiTagService 等) | 修改 |

---

## 支持的图像格式

```
.jpg .jpeg .png .gif .bmp .tiff .tif
.webp .heic .heif
.dng .cr2 .cr3 .nef .arw
```

---

## 已知限制

1. **CLGeocoder** 在 maccatalyst 26.0 标记为废弃 (建议迁移到 MapKit), 目前仍可用
2. **NLTagger** 的 .NET 绑定在 net10.0-maccatalyst 下不可用, 关键词提取改用正则分词
3. **VN*Request** 构造函数在 .NET 10 需要传入 `null` completionHandler
4. 人脸聚类为简单的凝聚层次算法, 大量人脸时可能需要优化
5. FTS5 可用性取决于 SQLite 编译选项, 不可用时降级为 LIKE 查询

---

## 已修复的问题

### _rawEntries 状态覆盖 (导航定位不正确)

**现象**: 点击侧边栏人物/分类/地点切换时，右侧文件列表显示错误内容；双击不同虚拟文件夹都导航到同一个分类的内容。

**根因**: `GroupField` 属性变化触发 `OnGroupFieldChanged` → `ApplySortAndGroup()`，该方法从 `_rawEntries` 重建 `Entries`。但多个 AI 数据加载方法 (`LoadAiTopLevelAsync` 的 People/Locations/Dates 分支、`LoadFaceClusterEntriesAsync`、`LoadAiCategoryEntriesAsync`) 在设置 `GroupField` 前未更新 `_rawEntries`，导致 `ApplySortAndGroup` 使用旧的 `_rawEntries` 覆盖了正确的 `Entries`。

**典型复现路径**: 分类视图 (`_rawEntries=catEntries, GroupField=Type`) → 人物视图 → `Entries=peopleEntries` → `GroupField=None` 触发 `ApplySortAndGroup()` → 用 catEntries 覆盖 Entries。

**修复**: 在所有 AI 数据加载方法中，确保在 `GroupField` 赋值前设置 `_rawEntries`。

### AiViewHost Tab 栏和详情头部冗余

**现象**: AI 视图顶部显示了分类 Tab 栏和详情页返回按钮+名称，与工具栏的前进/后退按钮功能重复。

**修复**: 移除 AiViewHost 的 Tab 切换和详情头部 UI，AI 导航统一通过工具栏前进/后退按钮和面包屑完成。AiViewHost 简化为仅切换 FileGridView 和 TextSearchView。

### AI 视图状态残留

**现象**: 从 AI 视图导航到文件系统路径时，`IsAiView` 未清除。

**修复**: 在 `NavigateToAsync` 的文件系统分支中清除 AI 状态 (`IsAiView=false, CurrentFaceClusterId=null, CurrentAiContextLabel=null`)。

### AI 视图切换到收藏夹/归档时状态未清除

**现象**: 在 AI 文件夹中浏览后，点击收藏夹的虚拟文件夹无效，UI 仍停留在 AI 视图。

**根因**: `NavigateToCollectionAsync` 和 `NavigateToArchiveAsync` 没有清除 AI 状态 (`IsAiView`、`CurrentFaceClusterId`、`CurrentAiContextLabel`、`TextTokens`)，导致 `IsAiView` 仍为 `true`，UI 渲染走 AI 分支。

**修复**: 在 `NavigateToCollectionAsync` 和 `NavigateToArchiveAsync` 中添加完整的互斥状态清除，与 `NavigateToAsync` 和 `HandleAiNavigationAsync` 保持一致。

### AI 视图重命名不刷新

**现象**: 在 AI 视图中重命名文件或人脸聚类后，名称没有立即更新，需要手动刷新才能看到新名称。

**根因**: 两个子问题:
1. `RenameFaceClusterAsync` 调用 `NavigateToAiViewAsync(People)` 重新加载人物列表，但 `HandleAiNavigationAsync` 的重复导航守卫 (`CurrentPath == sentinelPath && Entries.Count > 0`) 判定为重复导航直接返回
2. `RenameEntryAsync` 对真实文件调用 `LoadDirectoryContentsAsync(forceRefresh: true)`，但 `CurrentPath` 是 AI 哨兵路径而非文件系统目录，加载失败

**修复**:
- `RenameFaceClusterAsync`: 改为直接内存更新 — 更新 `FaceClusters` 中的 `DisplayName`，在 `Entries` 中替换对应虚拟条目 (FileSystemEntry.Name 为 init-only，必须创建新对象)
- `RenameEntryAsync`: 添加 `IsAiView` 分支 — 磁盘重命名 + `UpdateFilePathAsync` 更新数据库中三张表的 `file_path` + 内存替换条目
- 新增虚拟条目守卫: 非 face 类型的虚拟条目直接返回
- `IAiTagService` 新增 `UpdateFilePathAsync` 方法，在事务中更新 `ai_tags`、`face_observations`、`ai_analysis_status` 三张表

### AI 视图重命名输入框焦点丢失 (首次修复)

**现象**: 在 AI 视图中重命名时，点击文本框直接就确定了，无法编辑。

**根因**: `ResolveFaceThumbnailsAsync` 作为 fire-and-forget 后台任务运行，每加载一个缩略图就调用 `OnPropertyChanged(nameof(Entries))`。这触发 FileGridView 的 `OnViewModelPropertyChanged` → `InvokeAsync(StateHasChanged + JS scroll)` → WKWebView 重建 DOM 导致输入框失焦 → `onblur` → `CommitRename()`。

**修复**: 在 `OnViewModelPropertyChanged` 的 `Entries` 处理分支添加守卫: `if (_renamingEntry != null) return;`，跳过重命名期间的重渲染。

### AI 视图重命名输入框焦点丢失 (全面修复)

**现象**: 首次修复后，人脸聚类重命名仍然存在点击输入框直接退出编辑的问题。

**根因**: 除了 `Entries` PropertyChanged 外，还有其他 `StateHasChanged` 触发源未被保护:
1. `OnViewModelPropertyChanged` 中 `IsLoading`/`CurrentPath`/`ViewMode`/`Groups`/`GroupField`/`CutPaths` 属性变化触发 `StateHasChanged` — 无重命名守卫
2. `OnSelectionChanged` (SelectedEntries.CollectionChanged) 触发 `StateHasChanged` — 无重命名守卫
3. `OnRenameRequested` 中冗余的 `StateHasChanged()` 和 `scrollToSelected` JS 调用与 `FocusRenameInput` 中的 `focusAndSelect` 竞争

**修复**:
- `OnViewModelPropertyChanged` 通用属性分支添加 `if (_renamingEntry != null) return;`
- `OnSelectionChanged` 添加 `if (_renamingEntry != null) return;`
- `OnRenameRequested` 简化为仅调用 `StartRename(entry)`，移除多余的 `StateHasChanged()` 和 `scrollToSelected`

### AI 文件夹空白处右键菜单

**现象**: 在 AI 视图的空白区域右键出现了标准文件系统的右键菜单 (新建文件夹等)，不合适。

**修复**: 在 `ShowBackgroundContextMenuAsync` 中将 `IsCollectionView` 判断扩展为 `IsCollectionView || IsAiView`，AI 视图空白处右键直接返回不显示菜单。

### 重命名菜单项缺少 SF Symbol 图标

**现象**: 右键菜单中 "重命名" 选项左侧没有图标。

**根因**: `ContextMenuHelper.MapMenuLabelToSFSymbol` (用于普通菜单项) 中缺少 "重命名" 的映射。虽然 `MapLabelToSFSymbol` (用于快捷操作栏) 有 "重命名" → "pencil" 映射，但两个方法独立。

**修复**: 在 `MapMenuLabelToSFSymbol` 中添加 `"重命名" => "pencil"` 映射。
