# 设为默认文件浏览器 — 功能总结

## 1. 概述

MacExplorer 提供"设为默认文件浏览器"功能，允许用户将 MacExplorer 注册为 macOS 系统中 `public.folder` 类型的默认处理程序。该功能通过 macOS LaunchServices API（`LSSetDefaultRoleHandlerForContentType`）实现注册，并在 `Info.plist` 中声明 `CFBundleDocumentTypes` 以支持文件夹类型。

核心挑战在于：当系统通过 URL 启动或唤醒 MacExplorer 时，需要在三种不同的进程状态下正确接收并导航到目标文件夹路径。

## 2. macOS 系统限制

- **桌面双击文件夹是 Finder 内部导航行为**，不经过 LaunchServices 分发机制。所有第三方文件管理器（Path Finder、ForkLift、QSpace 等）都受此限制，无法拦截桌面双击事件。
- `LSSetDefaultRoleHandlerForContentType` 对 `public.folder` 的设置**仅在特定场景下生效**（见第 3 节）。
- 该 API 已被 Apple 标记为废弃（deprecated），但在当前 macOS 版本中仍可正常调用。

## 3. 生效场景

将 MacExplorer 设为默认文件夹处理程序后，以下场景会使用 MacExplorer 打开文件夹：

| 场景 | 说明 |
|------|------|
| 终端 `open /path/to/folder` | 通过 LaunchServices 分发到默认处理程序 |
| 系统文件对话框中点击文件夹 | 部分系统对话框会调用默认处理程序 |
| 其他应用通过 `NSWorkspace` 打开文件夹 | 例如从其他 App 中"在文件管理器中显示" |

**不生效的场景**：桌面双击文件夹、Finder 内部导航（这些始终由 Finder 处理）。

## 4. 三种启动场景的 URL 处理

### 4.1 冷启动（进程完全退出后启动）

**调用链**：系统 → `scene:willConnectToSession:options:` → Home.razor.OnInitialized

1. 系统调用 `SceneDelegate.WillConnect`，URL 在 `connectionOptions.UrlContexts` 中。
2. **关键**：必须在 `base.WillConnect()` 之前提取 URL，否则 MAUI 基类 `MauiUISceneDelegate` 可能消费 `connectionOptions`，导致 URL 丢失。
3. 调用 `base.WillConnect()` 完成 MAUI 初始化。
4. 将路径存储到：
   - `SceneDelegate._coldStartPath`（静态字段，最终后备）
   - `NavigationBridge.PendingNavigationPath`（如果 DI 容器已就绪）
5. `Home.razor.OnInitialized` 读取 `PendingNavigationPath`（或回退到 `_coldStartPath`），执行导航。

### 4.2 暖启动（窗口已关闭，进程仍在后台）

**调用链**：系统 → `WillConnect` / `OpenUrlContexts` / `AppDelegate.OpenUrls` → Home.razor.OnInitialized

暖启动时，系统可能通过多条路径传递 URL：

- `SceneDelegate.WillConnect`（重建窗口时）
- `SceneDelegate.OpenUrlContexts`（Scene 级别）
- `AppDelegate.OpenUrls`（Application 级别后备）

**关键设计**：

- `PendingNavigationPath` 必须**同步立即设置**，不能放在 `BeginInvokeOnMainThread` 的异步回调中，否则 `Home.razor.OnInitialized` 可能在回调执行前就已读取该属性，导致路径丢失。
- `_coldStartPath` 始终同步设置作为最终后备。
- `PendingNavigationPath` 只有一个消费者（`Home.razor.OnInitialized`），避免竞争条件。

### 4.3 热启动（有活跃窗口）

**调用链**：系统 → `scene:openURLContexts:` → `NavigationBridge.NavigateAsync`

1. 系统调用 `SceneDelegate.OpenUrlContexts`。
2. 同步设置 `PendingNavigationPath` 作为后备。
3. 通过 `MainThread.BeginInvokeOnMainThread` 异步调用 `NavigationBridge.NavigateAsync`。
4. `NavigateAsync` 找到已注册的活跃 ViewModel，直接执行 `NavigateToCommand` 导航到目标文件夹。
5. 导航成功后清除 `PendingNavigationPath`，避免 Home 页面重复导航。

## 5. 关键设计决策

| 决策 | 原因 |
|------|------|
| `PendingNavigationPath` 单一消费者模式 | 只在 `Home.razor.OnInitialized` 中消费，避免多处竞争导致路径丢失 |
| `_coldStartPath` 静态字段作为最终后备 | DI 容器在冷启动时可能尚未就绪，静态字段不依赖 DI |
| URL 提取在 `base.WillConnect()` 之前执行 | MAUI 基类可能消费 `connectionOptions.UrlContexts` |
| 同步设置路径属性，异步尝试即时导航 | 确保路径在 `OnInitialized` 读取前已可用，异步导航仅用于热启动场景 |
| `NavigateAsync` 成功后清除 `PendingNavigationPath` | 防止 Home 页面重新初始化时重复导航到同一路径 |

## 6. 涉及的文件

| 文件 | 职责 |
|------|------|
| `Platforms/MacCatalyst/SceneDelegate.cs` | 处理冷启动和暖/热启动的 URL 接收，提取文件夹路径并存储 |
| `Platforms/MacCatalyst/AppDelegate.cs` | Application 级别的 URL 后备处理（`application:openURLs:`） |
| `Services/NavigationBridge.cs` | 桥接平台层与 ViewModel，管理 `PendingNavigationPath` 和活跃 VM 导航 |
| `Platforms/MacCatalyst/Services/MacDefaultAppService.cs` | 调用 LaunchServices API 注册/取消默认处理程序，含 lsregister 备用方案 |
| `Services/IDefaultAppService.cs` | 默认应用服务接口定义 |
| `Components/Dialogs/SettingsDialog.razor` | 设置界面，提供"设为默认文件管理器"开关 |
| `Components/Pages/Home.razor` | 页面初始化时消费 `PendingNavigationPath`，执行导航 |
| `Platforms/MacCatalyst/Info.plist` | 声明 `CFBundleDocumentTypes` 支持 `public.folder` 类型 |

## 7. 踩过的坑

### `base.WillConnect()` 消费 URL

MAUI 基类 `MauiUISceneDelegate.WillConnect` 内部可能处理 `connectionOptions.UrlContexts`，导致冷启动时 URL 被"吞掉"。解决方案：在调用 `base.WillConnect()` 之前提取 URL。

### `BeginInvokeOnMainThread` 导致时序问题

暖启动时，如果将 `PendingNavigationPath` 的赋值放在 `MainThread.BeginInvokeOnMainThread` 回调中，由于回调是异步调度的，`Home.razor.OnInitialized` 可能在回调执行之前就已运行，读到空路径。解决方案：同步直接赋值，不经过主线程调度。

### 多消费者竞争 `PendingNavigationPath`

早期实现中 `NavigationBridge.Register` 和 `Home.razor.OnInitialized` 都会消费 `PendingNavigationPath`，导致路径被先注册的消费者清除，后续消费者读到空值。解决方案：`Register` 方法不消费路径，统一由 `OnInitialized` 作为唯一消费者。

### `LSSetDefaultRoleHandlerForContentType` 已废弃

该 API 被 Apple 标记为 deprecated，但目前无替代方案，且在当前 macOS 版本中仍可正常工作。实现中增加了 `lsregister` 备用方案，以及手动设置的用户指引作为兜底。

### 桌面双击文件夹不走 LaunchServices

这是 macOS 系统架构层面的限制。桌面实际上是 Finder 进程渲染的一个特殊窗口，双击文件夹是 Finder 的内部导航行为，不会触发 LaunchServices 的默认应用分发。所有第三方文件管理器都无法改变这一行为。设置成功后的提示信息中已明确告知用户此限制。
