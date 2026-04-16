# 面包屑导航方案

## 架构概览

### 职责划分

| 组件 | 职责 |
|------|------|
| `NavigationViewModel` | 面包屑生成、视图状态管理（IsArchiveView / IsAiView / IsCollectionView）、导航历史 |
| `FileListViewModel` | 路径分发（根据哨兵路径类型分发到 Archive / AI / 普通目录）、内容加载、协调子 ViewModel |
| `BreadcrumbBar.razor` | 面包屑渲染、点击导航、路径输入编辑模式 |

### 面包屑生成逻辑

`NavigationViewModel.UpdateBreadcrumbs()` 根据当前视图状态生成面包屑段：

| 视图类型 | 面包屑结构 |
|---------|-----------|
| 普通文件系统 | 标准路径层级，每段可点击导航 |
| 压缩文件视图 (`IsArchiveView`) | 外部文件系统路径 + 压缩文件 + 内部路径 |
| 收藏夹视图 (`IsCollectionView`) | "收藏夹 > 收藏夹名称" |
| AI 视图 (`IsAiView`) | 由 `UpdateBreadcrumbsForAi()` 单独处理 |

### 导航流程

用户点击面包屑时：

1. `BreadcrumbBar` 调用 `FileListViewModel.NavigateToAsync(path)`
2. `FileListViewModel` 根据路径类型分发：
   - `__archive:` 哨兵路径 -> `NavigateToArchiveAsync()`
   - `__ai:` 哨兵路径 -> `HandleAiNavigationAsync()`
   - 普通路径 -> `NavigationViewModel.NavigateToAsync()`（内部统一重置特殊视图标志 + 更新历史 + 更新面包屑）
3. 编辑模式下直接使用 `ViewModel.CurrentPath`（真实文件系统路径）

### 关键数据模型

```csharp
public class BreadcrumbSegment
{
    public string Name { get; init; }        // 原始文件名
    public string DisplayName { get; init; }  // 本地化显示名
    public string FullPath { get; init; }     // 完整路径（哨兵路径或真实路径）
    public bool HasDropdown { get; init; }
    public IReadOnlyList<BreadcrumbSegment>? Siblings { get; set; }
}
```

## 路径本地化

### 方案

通过 macOS 原生 API `NSFileManager.displayName(atPath:)` 获取本地化文件夹名称，面包屑和侧边栏统一使用。

```
IDisplayNameService (接口)
    └── MacDisplayNameService (Mac Catalyst 实现)
            - NSFileManager.DefaultManager.DisplayName(path)
            - ConcurrentDictionary 缓存
            - 哨兵路径 (__archive: 等) 直接取文件名，不走系统 API
```

### 本地化规则

| 路径类型 | DisplayName 来源 |
|---------|-----------------|
| 文件系统路径段 | `NSFileManager.DisplayName()` 本地化名称 |
| 压缩文件内部路径 | 原始文件名（压缩内路径无本地化） |
| 收藏夹 / AI 等应用概念 | 原始名称 |

### 消费方

- **NavigationViewModel**: `Localize(fullPath, fallback)` 辅助方法，构建 `BreadcrumbSegment` 时填充 `DisplayName`
- **BreadcrumbBar.razor**: 渲染时优先使用 `DisplayName`，为空则回退到 `Name`
- **FinderSidebar.razor**: `OnInitialized()` 中预取各系统目录本地化名称，替代硬编码中文

### DI 注册

```csharp
// MauiProgram.cs
builder.Services.AddSingleton<IDisplayNameService,
    Platforms.MacCatalyst.Services.MacDisplayNameService>();
```

## 疑难问题与解决方案

### 1. NSFileManager.DisplayName() 始终返回英文

**现象**: 系统语言为中文，但 `NSFileManager.DefaultManager.DisplayName("/Users/xxx/Desktop")` 返回 "Desktop" 而非 "桌面"。

**根因**: `displayName(atPath:)` 根据应用 `Info.plist` 中 `CFBundleLocalizations` 声明的语言列表来决定返回语言。未声明此键时，macOS 将应用视为仅支持英语。

**解决**: 在 `Info.plist` 中声明 `CFBundleLocalizations`，包含 26 种语言（zh-Hans、zh-Hant、en、ja、ko、fr、de、es 等）。

### 2. MAUI 构建剥离 Info.plist 自定义键

**现象**: 在源文件 `Platforms/MacCatalyst/Info.plist` 中添加 `CFBundleLocalizations`，构建后产物中该键不存在。

**根因**: MAUI 构建管线在生成最终 Info.plist 时，只合并已知的键，自定义键会被丢弃。

**解决**: 在 `MacExplorer.csproj` 的构建后目标 `ReplaceMacCatalystIcon` 中，使用 PlistBuddy 注入：

```xml
<Exec Command="/usr/libexec/PlistBuddy -c 'Delete :CFBundleLocalizations' '$(_AppBundlePath)/Contents/Info.plist'"
      IgnoreExitCode="true" />
<Exec Command="/usr/libexec/PlistBuddy -c 'Add :CFBundleLocalizations array' '$(_AppBundlePath)/Contents/Info.plist'"
      IgnoreExitCode="true" />
<Exec Command="for lang in zh-Hans zh-Hant en ja ko fr de es pt it ru ar nl sv da nb fi pl tr uk th vi id ms hi he; do /usr/libexec/PlistBuddy -c &quot;Add :CFBundleLocalizations: string $lang&quot; '$(_AppBundlePath)/Contents/Info.plist'; done"
      IgnoreExitCode="true" />
```

注入后已有的 `codesign --force --sign -` 步骤会重新签名，避免启动时 -54 错误。

### 3. PlistBuddy 多参数崩溃

**现象**: 单条 PlistBuddy 命令携带 27+ 个 `-c` 参数时崩溃 "Abort trap: 6"。

**解决**: 拆分为三条命令：Delete（清理旧数据）+ Add array（创建空数组）+ shell for 循环逐个追加元素。Delete 使用 `IgnoreExitCode="true"` 保证首次构建（键不存在时）不报错。

### 4. 重复构建导致数组条目叠加

**现象**: 多次 `dotnet build` 后，`CFBundleLocalizations` 数组中每种语言出现多次。

**根因**: 增量构建时 Info.plist 已包含上次注入的数组，再次 Add 会追加而非覆盖。

**解决**: 注入前先执行 `Delete :CFBundleLocalizations`（幂等，键不存在时 PlistBuddy 返回非零但被 `IgnoreExitCode` 忽略）。

### 5. .NET 绑定方法名与 ObjC 不一致

**现象**: `NSFileManager.DefaultManager.DisplayNameAtPath(path)` 编译报 CS1061。

**根因**: .NET MAUI 对 `displayNameAtPath:` 的绑定方法名是 `DisplayName(string)`，而非直译的 `DisplayNameAtPath`。

**解决**: 使用 `NSFileManager.DefaultManager.DisplayName(path)`。

### 6. 视图标志位重置顺序

**现象**: 从压缩文件/AI 视图点击面包屑导航到普通目录后，`CurrentArchivePath` 等关联状态残留，导致后续面包屑生成异常。

**根因**: `FileListViewModel` 在调用 `NavigationViewModel.NavigateToAsync()` 之前提前将 `IsArchiveView` 等标志清零，导致 `NavigationViewModel` 内部的 `if (IsArchiveView || IsAiView || IsCollectionView)` 判断为 false，跳过关联状态清理。

**解决**: 标志位重置统一由 `NavigationViewModel.NavigateToAsync()` 内部处理，`FileListViewModel` 不提前干预。

## 涉及文件

| 文件 | 说明 |
|------|------|
| `Services/IDisplayNameService.cs` | 本地化显示名称服务接口 |
| `Platforms/MacCatalyst/Services/MacDisplayNameService.cs` | Mac 平台实现 |
| `Models/BreadcrumbSegment.cs` | 面包屑段模型（含 `DisplayName` 属性） |
| `ViewModels/NavigationViewModel.cs` | 面包屑生成 + 导航状态管理 |
| `ViewModels/FileListViewModel.cs` | 路径分发协调器 |
| `Components/Breadcrumb/BreadcrumbBar.razor` | 面包屑 UI 渲染 |
| `Components/Sidebar/FinderSidebar.razor` | 侧边栏（动态本地化名称） |
| `MauiProgram.cs` | DI 注册 |
| `MacExplorer.csproj` | 构建后 PlistBuddy 注入 `CFBundleLocalizations` |
