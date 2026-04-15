# MacExplorer 拖拽功能实现记录

## 项目背景

MacExplorer 是一个 macOS Finder 替代品，使用 .NET MAUI Blazor 在 Mac Catalyst 上构建（net10.0-maccatalyst）。UI 通过 WKWebView 中的 Blazor 组件渲染。

拖拽需求：
- **拖出**（MacExplorer → Finder/其他应用）：将文件拖到外部
- **拖入-外部**（Finder → MacExplorer）：从 Finder 拖文件进来
- **拖入-跨窗口**（MacExplorer 窗口 A → 窗口 B）：在不同 MacExplorer 窗口间移动文件
- **拖入-内部**（同窗口内文件 → 子文件夹）：在同一窗口内移动文件

约束：仅在文件列表区域生效（不包括侧边栏），操作始终为移动（非拷贝）。

---

## 当前状态

| 场景 | 状态 | 实现方式 |
|------|------|----------|
| 拖出（→ Finder/其他应用） | **已实现** | HTML5 `text/uri-list` + `text/plain`（`native-drag.js`） |
| 拖入-内部（同窗口文件 → 子文件夹） | **已实现** | HTML5 DnD + Blazor 事件处理 |
| 拖入-外部（Finder → MacExplorer） | **已实现** | AppKit NSView 覆盖层 + NSDraggingDestination（`DropOverlayHelper.cs`） |
| 拖入-跨窗口（MacExplorer → MacExplorer） | **已实现** | 覆盖层拦截 + NSWindow 场景匹配 + 源目录刷新 |

---

## Mac Catalyst 拖拽管线（核心发现）

通过 `log stream --process MacExplorer --level debug` 系统日志分析，确认了 Mac Catalyst 上拖拽的完整内部管线：

```
AppKit NSDragging (鼠标拖拽事件)
  ↓
UIKitMacHelper Bridge (Mac Catalyst 桥接层)
  ↓  将 AppKit 拖拽转换为 UIKit UIDropSession
  ↓
WebKit 内部 Drop Handler (WKWebView 的私有处理)
  ↓  尝试创建 sandbox extension → 失败
  ↓
HTML5 drop 事件 → 不会触发（因为 sandbox 失败）
```

关键系统日志证据：

```
(AppKit) [DragAndDrop] 0x000000b0 Tracking message: 3
(UIKitMacHelper) [DragAndDrop] Drop session updated. New operation: Move
(UIKitMacHelper) [DragAndDrop] Asking handler to perform drop
(UIKitMacHelper) [DragAndDrop] DruidConnection: session perform
(WebKit) [DragAndDrop] Loading data from 1 item providers for session: 0x84e6d56e0
(WebKit) [Sandbox] WebContent[53590] Could not create a sandbox extension for '<private>'
(WebKit) [DragAndDrop] Finished performing drag controller operation (handled: 1)
(WebKit) [DragAndDrop] Drop session ended: 0x84e6d56e0 (performing operation: 1, began dragging: 0)
```

### 核心问题

1. **Mac Catalyst 桥接层在 AppKit 层面拦截拖拽**，在事件到达任何我们可以 hook 的公共 API 之前就完成了转换。
2. **WebKit 声称已处理该 drop**（`handled: 1`），但因为 **sandbox extension 创建失败**，实际上数据未成功传递给 JS 层。
3. HTML5 `drop` 事件因此永远不会被触发。

---

## 已尝试方案及结果

### 方案 1：移除内置交互 + 移除 HTML drag 属性

**思路**：移除 WKWebView 的 UIDragInteraction/UIDropInteraction，尝试完全接管。

**结果**：破坏了所有功能，包括已有的拖出和内部拖拽。WKWebView 的拖拽交互是不可分离的。

---

### 方案 2：添加 UIDropInteraction 到 WKWebView

**思路**：在 WKWebView 上添加新的 UIDropInteraction，用自定义 delegate 处理外部 drop。

**结果**：delegate 方法从未被调用。Mac Catalyst 桥接层在 UIKit interaction 层面之前就处理了事件。

---

### 方案 3：JS drop 事件 + WKScriptMessageHandler

**思路**：在 JS 层用 `document.addEventListener('drop', ...)` 捕获事件，通过 `window.webkit.messageHandlers.fkfinderDrop.postMessage(...)` 发送到 C#。

**实现**：`native-drag.js` 中注册了全面的 drop 监听器，尝试了 6 种数据源（`text/uri-list`, `text/plain`, `URL`, `public.file-url`, 遍历所有 types, `File.path`）。

**结果**：对外部拖入（Finder → MacExplorer），JS `drop` 事件**完全不会触发**。根因是 WebKit sandbox extension 失败，导致 WebKit 无法将外部拖拽数据转换为 HTML5 drop 事件。

---

### 方案 4：AppKit NSDraggingDestination（P/Invoke 注入到 contentView）

**思路**：通过 P/Invoke 在 NSWindow 的 contentView 上注入 NSDraggingDestination 方法（`draggingEntered:`, `performDragOperation:` 等），注册 `public.file-url` 拖拽类型，直接在 AppKit 层面拦截。

**实现**：
- 使用 `UISBHSDidCreateWindowForSceneNotification` 监听窗口创建
- 通过 `class_replaceMethod` 在 contentView 的类上注入 4 个 NSDraggingDestination 方法
- 通过 `registerForDraggedTypes:` 注册文件 URL 类型

**结果**：注入的方法**从未被调用**。Mac Catalyst 桥接层在 AppKit 拖拽事件到达 contentView 的 NSDraggingDestination 方法之前就拦截了。

---

### 方案 5：Swizzle WKWebView UIDropInteraction delegate 的 performDrop

**思路**：既然 Mac Catalyst 桥接层最终通过 UIDropInteraction delegate 调用 WebKit，swizzle 该 delegate 的 `dropInteraction:performDrop:` 方法来拦截。

**实现**：
- 遍历 `webView.Interactions` 和所有子视图查找 UIDropInteraction
- 获取 delegate，用 `class_replaceMethod` swizzle `dropInteraction:performDrop:` (type encoding `"v@:@@"`)
- 在 swizzled 方法中提取 NSItemProvider 文件路径

**结果**：UIDropInteraction 在 WKWebView 及其子视图上**不存在**。重试 31 次（约 15 秒）后仍未找到。Mac Catalyst 桥接层（UIKitMacHelper `DruidConnection`）不使用标准的 UIDropInteraction 对象，而是通过私有机制处理 drop。

文件日志证据：
```
[14:34:30.542] AttachToWebView: searching for UIDropInteraction...
[14:34:30.550] UIDropInteraction not found yet, retrying...
[14:34:45.395] Gave up finding UIDropInteraction after 31 attempts
```

---

### 方案 6：AppKit NSView 覆盖层拦截（最终方案 ✅）

**思路**：在 NSWindow.contentView 的最顶层添加一个透明的自定义 NSView（`FKDropOverlayView`），注册为 NSDraggingDestination。NSDragging 命中测试从最顶层子视图开始检查，覆盖层在桥接层的 UINSView 之前拦截拖拽事件，直接从 NSPasteboard 读取文件路径。

**与方案 4 的关键区别**：方案 4 是在 contentView 的**现有类**上注入方法（`class_replaceMethod`），桥接层的 UINSView 作为更深层子视图先处理了事件。方案 6 是**新增一个顶层子视图**，NSDragging 命中测试从最后子视图（最顶层）开始，覆盖层先于 UINSView 接收事件。

**视图层级**：
```
NSWindow
  └── contentView (NSView)
        ├── NSVisualEffectView (VibrancyHelper, index 0)
        ├── UINSView (Mac Catalyst 桥接层)
        └── FKDropOverlayView (覆盖层, 最顶层 → 最先命中)
```

**实现**：
- 通过 `objc_allocateClassPair` 创建 `FKDropOverlayView` 继承 NSView
- 添加 5 个 NSDraggingDestination 方法（`draggingEntered:`, `draggingUpdated:`, `draggingExited:`, `prepareForDragOperation:`, `performDragOperation:`）
- 注册 `public.file-url` 和 `NSFilenamesPboardType` 拖拽类型
- 监听 `UISBHSDidCreateWindowForSceneNotification` 为每个新窗口安装覆盖层
- 三重策略从 NSPasteboard 提取文件路径：
  1. `NSFilenamesPboardType` → `propertyListForType:` → 文件路径数组
  2. `public.file-url` → `stringForType:` → file:// URL → 解码为路径
  3. 遍历所有 types 查找 file:// URL
- 通过 JS `evaluateJavaScript` 调用 `fkfinderNativeDrag.getDropTargetAtPoint(x,y)` 确定目标文件夹
- 提取的路径通过 `IDragDropBridge.NotifyExternalDrop` 通知 ViewModel

**内部拖拽保护机制**：
- `native-drag.js` 在 `dragstart` 时通过 `fkfinderDragState` WKScriptMessageHandler 通知 C# 层
- C# 收到通知后隐藏发起拖拽窗口的覆盖层，HTML5 DnD 正常工作
- `dragend` 时恢复所有覆盖层可见
- 跨窗口拖拽：窗口 A 发起 → A 的覆盖层隐藏 → 拖到窗口 B → B 的覆盖层可见 → B 接收 drop ✓

**结果**：编译通过，覆盖层成功安装到 NSWindow.contentView 顶层。

---

## 技术细节

### 关键文件

| 文件 | 作用 |
|------|------|
| `Platforms/MacCatalyst/Handlers/NativeDragDropHelper.cs` | 原生拖拽处理核心（P/Invoke） |
| `Platforms/MacCatalyst/Handlers/TransparentWebViewHandler.cs` | 创建 WKWebView，调用 `AttachToWebView` |
| `Services/IDragDropBridge.cs` | Blazor ↔ 原生拖拽桥接接口 |
| `Platforms/MacCatalyst/Services/MacDragDropBridge.cs` | IDragDropBridge 实现，管理多窗口 ViewModel |
| `wwwroot/js/native-drag.js` | HTML5 拖拽处理（拖出 + 内部拖拽） |
| `Platforms/MacCatalyst/Handlers/DropOverlayHelper.cs` | AppKit 覆盖层核心（NSDraggingDestination + NSPasteboard） |
| `Components/Pages/Home.razor` | Blazor 主页面，注册窗口-ViewModel 映射（跨窗口拖拽修复） |
| `ViewModels/FileListViewModel.cs` | 文件列表 ViewModel，处理外部拖入文件移动 |

### P/Invoke 模式参考

项目中已有的 P/Invoke 模式（可复用）：
- `DockMenuHelper.cs`：`class_replaceMethod` 注入 ObjC 方法
- `ContextMenuHelper.cs`：`objc_allocateClassPair` 创建自定义 ObjC 类
- `VibrancyHelper.cs`：`UISBHSDidCreateWindowForSceneNotification` 监听窗口创建

### 关键技术点

- **NSLog P/Invoke 在 arm64 上不可靠**：`Console.WriteLine` 和 NSLog 都无法在 `open` 启动的 Mac Catalyst 应用的 `log stream` 中显示。使用 `File.AppendAllText("/tmp/fkfinder-drag.log", ...)` 进行调试。
- **ObjC type encodings**：`Q` = unsigned long (NSDragOperation), `@` = id, `:` = SEL, `v` = void, `B` = _Bool
- **UIDropSession.localDragSession**：为 nil 表示外部拖入，非 nil 表示应用内拖拽

---

## 跨窗口拖拽完整解决方案

### 问题回顾

跨窗口拖拽曾存在两个核心问题：
1. **双向拖拽不可用**：A窗口能向B窗口拖动，但B窗口无法向A窗口拖动
2. **源目录不刷新**：拖拽完成后，发起窗口的文件列表没有更新

### 根本原因

#### 问题1：UIWindow → NSWindow 映射错误

`NativeDragDropHelper.FindNSWindowForUIWindow` 方法原本直接返回 `keyWindow`，忽略了传入的 `uiWindow` 参数。

**日志证据**：
```
DropOverlayHelper 使用的 SceneIdentifier: FUScene|com.fkfinder.app(25880)|4B41BC86-0F1B-4BF6-966A-7668FF4509C7
session.PersistentIdentifier 返回的值:        4B41BC86-0F1B-4BF6-966A-7668FF4509C7
```

`session.PersistentIdentifier` 只返回 UUID 部分，而 `hostWindowForSceneIdentifier:` 需要完整的 `FUScene|bundleId|UUID` 格式，导致所有 WKWebView 都被错误映射到 keyWindow。

#### 问题2：源目录未刷新

`HandleExternalDrop` 只刷新了接收窗口，没有通知发起窗口刷新其文件列表。

### 解决方案

#### 修复1：通过 NSWindow 遍历匹配场景标识符

**文件**：`NativeDragDropHelper.cs` - `FindNSWindowForUIWindow` 方法

**思路**：遍历 NSApplication 的所有 NSWindow，比较每个 NSWindow 的 scene identifier 是否包含当前 UIWindow 的 scene persistent ID。

```csharp
private static IntPtr FindNSWindowForUIWindow(UIKit.UIWindow uiWindow)
{
    // 获取当前 UIWindow 的 scene persistent ID
    var windowScene = uiWindow.WindowScene;
    var scenePersistentId = windowScene?.Session?.PersistentIdentifier ?? "";
    
    // 遍历所有 NSWindow
    var windows = objc_msgSend(sharedApp, Selector.GetHandle("windows"));
    for (nuint i = 0; i < windowCount; i++)
    {
        var nsWindow = objc_msgSend_objectAtIndex(windows, ...);
        var windowSceneId = GetWindowSceneIdentifier(nsWindow);
        
        // 比较 scene ID（NSWindow 返回完整格式，包含 UUID）
        if (windowSceneId.Contains(scenePersistentId))
            return nsWindow;  // 找到匹配
    }
    
    return GetKeyWindow();  // fallback
}
```

#### 修复2：Register 时自动建立窗口映射

**文件**：`MacDragDropBridge.cs` - `Register` 方法

在 `Register()` 中自动调用 `RegisterViewModelForCurrentWindow()`，确保每个窗口初始化时建立正确的 NSWindow → ViewModel 映射。

#### 修复3：拖拽后刷新源目录

**文件**：`MacDragDropBridge.cs` - `NotifyExternalDrop` 方法

在通知目标窗口处理拖拽后，遍历所有已注册的 ViewModel，检查是否有文件从其当前目录移走，如有则触发刷新。

```csharp
public void NotifyExternalDrop(string[] sourcePaths, string targetDirectory)
{
    ExternalDropReceived?.Invoke(sourcePaths, targetDirectory);
    RefreshSourceDirectories(sourcePaths);  // 新增：刷新源目录
}

private void RefreshSourceDirectories(string[] movedPaths)
{
    // 收集被移动文件的源目录
    var sourceDirs = movedPaths.Select(Path.GetDirectoryName).ToHashSet();
    
    // 找到需要刷新的 ViewModel
    foreach (var vm in _viewModels)
    {
        if (sourceDirs.Contains(vm.CurrentPath))
        {
            vm.ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
            await vm.RefreshCommand.ExecuteAsync(null);
        }
    }
}
```

### 关键文件

| 文件 | 修改内容 |
|------|----------|
| `NativeDragDropHelper.cs` | `FindNSWindowForUIWindow` 改用 NSWindow 遍历匹配 |
| `MacDragDropBridge.cs` | `Register` 自动注册窗口映射；`NotifyExternalDrop` 刷新源目录 |
| `Home.razor` | 移除有问题的 `WindowCreated` 事件订阅 |

---

## 历史待探索方案（已归档）

以下方案在方案 6 成功实现后不再需要探索：
- 方案 A：透明 UIKit 覆盖视图（UIDropInteraction）
- 方案 B：Swizzle UIKitMacHelper 桥接层
- 方案 C：AppKit NSWindow 级别 NSDraggingDestination
- 方案 D：解决 Sandbox Extension 问题
- 方案 E：NSEvent 全局监控
