# Mac Catalyst 窗口透明实现方案

## 问题背景

在 .NET MAUI Mac Catalyst 应用中实现窗口透明（毛玻璃效果，能看到桌面背景），遇到了窗口反复变回不透明的问题。

## 尝试过的方案（均失败）

### 1. UIKit 层直接设置（失败）

```csharp
platformWindow.BackgroundColor = UIColor.Clear;
rootView.BackgroundColor = UIColor.Clear;
```

**失败原因**：仅设置 UIWindow 不够，Mac Catalyst 中 UIWindow 底下有 NSWindow，必须设置 NSWindow 才能穿透到桌面。

### 2. ObjC 运行时设置 NSWindow（部分成功，不稳定）

通过 `objc_msgSend` 设置 `NSWindow.isOpaque = false` 和 `NSWindow.backgroundColor = clearColor`。

**失败原因**：MAUI 布局引擎会在之后反复重置背景色，导致静止时又变回不透明。

### 3. MAUI Mapper.ModifyMapping 拦截（部分成功，不稳定）

```csharp
PageHandler.Mapper.ModifyMapping(nameof(IContentView.Background), ...);
ContentViewHandler.Mapper.ModifyMapping(...);
WindowHandler.Mapper.ModifyMapping(...);
```

拦截 PageHandler、ContentViewHandler、WindowHandler 的 Background 映射，跳过默认逻辑，强制透明。

**失败原因**：MAUI Mapper 只能拦截 UIKit 层的设置。Mac Catalyst 桥接层（Catalyst bridge）自身也会在某些时机重置 NSWindow 属性，Mapper 管不到。

### 4. KVO 观察 NSWindow 属性变化（失败）

监听 NSWindow 的 `backgroundColor` 和 `opaque` 属性变化，变化时立即改回透明。

**失败原因**：存在竞态条件，系统改完 → KVO 触发 → 改回来，中间有短暂闪烁。且有些重置不触发 KVO。

### 5. NSTimer 定时器高频强制执行（失败）

每 0.1 秒强制重新设置 NSWindow 透明属性。

**失败原因**：性能开销大，且仍然无法完全防止闪烁。

### 6. ObjC Method Swizzling（失败）

替换 NSWindow 的 `setBackgroundColor:` 和 `setOpaque:` 方法实现，拦截所有调用。

**失败原因**：Catalyst 桥接层可能不通过标准 setter 修改属性，或在更底层操作。

### 7. CommunityToolkit.Maui（不支持）

CommunityToolkit.Maui 不包含任何窗口透明相关 API，主要提供 Popup、Snackbar 等 UI 控件。

### 8. UIKit 官方文档（无相关 API）

Apple UIKit 文档中 Mac Catalyst 相关 API 仅有标题栏/工具栏配置，没有窗口透明度的官方 API。

## 最终成功方案：AppKit 层 NSVisualEffectView

参考 [Steven Troughton-Smith 的 CatalystEffectViewChrome](https://github.com/steventroughtonsmith/CatalystEffectViewChrome)。

### 核心思路

**在 AppKit 层操作，而非 UIKit 层。** MAUI 只控制 UIKit 视图树，对 AppKit 层的 NSView 完全无感知，因此不会重置。

### 关键步骤

1. **监听 `UISBHSDidCreateWindowForSceneNotification`** — NSWindow 创建完成的精确时机
2. **通过 `hostWindowForSceneIdentifier:` 获取 NSWindow** — 最可靠的获取方式
3. **设置 `NSWindow.isOpaque = false`**
4. **创建 `NSVisualEffectView`（AppKit 原生）** 并插入到 `NSWindow.contentView`（NSView）的最底层
5. **配置毛玻璃参数**：`material = .hudWindow`，`blendingMode = .behindWindow`
6. **确保 UIKit 层透明** — `UIWindow` 和 `rootView` 的 `backgroundColor = .clear`

### 实现代码

#### VibrancyHelper.cs（核心）

```csharp
// 注册通知（在 App 构造函数中调用）
public static void Register()
{
    NSNotificationCenter.DefaultCenter.AddObserver(
        new NSString("UISBHSDidCreateWindowForSceneNotification"),
        OnWindowCreatedForScene);
}

// NSWindow 创建时触发
private static void OnWindowCreatedForScene(NSNotification notification)
{
    var nsWindow = FindNSWindow(...);
    ConfigureNSWindow(nsWindow);
}

// 核心配置
private static void ConfigureNSWindow(IntPtr nsWindow)
{
    // 1. NSWindow 设为非不透明
    objc_msgSend_void_bool(nsWindow, Selector.GetHandle("setOpaque:"), false);

    // 2. 获取 contentView（NSView）
    var contentView = objc_msgSend(nsWindow, Selector.GetHandle("contentView"));

    // 3. 创建 NSVisualEffectView（AppKit 层！）
    var effectView = /* alloc + init NSVisualEffectView */;

    // 4. 配置毛玻璃
    // material = .hudWindow (13) — 较透明的材质
    objc_msgSend_void_nint(effectView, Selector.GetHandle("setMaterial:"), 13);
    // blendingMode = .behindWindow (0)
    objc_msgSend_void_nint(effectView, Selector.GetHandle("setBlendingMode:"), 0);
    // alphaValue = 0.85 — 额外增加透明度
    objc_msgSend_void_double(effectView, Selector.GetHandle("setAlphaValue:"), 0.85);

    // 5. 插入到 contentView 最底层
    objc_msgSend_addSubview_positioned(contentView, ..., effectView, -1, IntPtr.Zero);
}
```

#### App.xaml.cs

```csharp
public App()
{
    InitializeComponent();
#if MACCATALYST
    // 尽早注册通知
    VibrancyHelper.Register();
#endif
}

// 窗口激活后设置 UIKit 层透明
window.Activated += (s, e) =>
{
    VibrancyHelper.MakeUIKitLayerTransparent(platformWindow);
};
```

#### MauiProgram.cs 中的 Mapper 拦截（辅助）

```csharp
// 拦截 MAUI 默认的背景色设置，防止 UIKit 层被设为不透明
PageHandler.Mapper.ModifyMapping("Background", (handler, view, action) => {
    // 跳过默认逻辑，强制透明
    vcView.BackgroundColor = UIColor.Clear;
});
```

### NSVisualEffectMaterial 可选值

| 值 | 名称 | 透明程度 |
|----|------|---------|
| 7 | `.sidebar` | 较不透明 |
| 12 | `.windowBackground` | 标准 |
| 21 | `.underWindowBackground` | 较透明 |
| 13 | `.hudWindow` | 更透明（当前使用） |
| 15 | `.fullScreenUI` | 最透明 |

可通过 `setAlphaValue:` 进一步调节（0.0 = 完全透明，1.0 = 不透明）。

### NSWindow 获取策略（按可靠性排序）

1. `NSApp.delegate.hostWindowForSceneIdentifier:` — 最可靠
2. `UIWindowScene` KVC `_nsWindow` — 备选
3. `NSApplication.sharedApplication.keyWindow` — 最后手段

### 为什么这个方案能稳定工作

| 层级 | 操作者 | 我们的操作位置 |
|------|--------|--------------|
| **AppKit 层**（NSWindow → NSView → NSVisualEffectView） | macOS 系统 | ✅ 我们在这里插入毛玻璃 |
| **Catalyst 桥接层** | Apple 桥接代码 | — |
| **UIKit 层**（UIWindow → UIView） | MAUI 框架 | MAUI 只能控制这里 |

MAUI 的布局引擎只能操作 UIKit 层，完全无法触及 AppKit 层的 NSVisualEffectView，所以毛玻璃效果永远不会被重置。

## 参考资源

- [CatalystEffectViewChrome](https://github.com/steventroughtonsmith/CatalystEffectViewChrome) — AppKit NSVisualEffectView 方案（本方案基础）
- [CatalystTransparentChrome](https://github.com/steventroughtonsmith/CatalystTransparentChrome) — UISplitViewController sidebar 方案
- [Apple UIKit Mac Catalyst 文档](https://developer.apple.com/documentation/uikit/mac-catalyst) — 无透明窗口 API
- [MAUI PageHandler 源码](https://github.com/dotnet/maui/blob/main/src/Core/src/Handlers/Page/PageHandler.iOS.cs) — MapBackground 实现
