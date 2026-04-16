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
// 可通过设置 UI 配置的静态属性（重启生效）
public static bool Enabled { get; set; } = true;
public static double Alpha { get; set; } = 0.85;

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
    // 1. 毛玻璃相关（仅 Enabled 时执行）
    if (Enabled)
    {
        objc_msgSend_void_bool(nsWindow, Selector.GetHandle("setOpaque:"), false);
        var contentView = objc_msgSend(nsWindow, Selector.GetHandle("contentView"));

        var effectView = /* alloc + init NSVisualEffectView */;
        objc_msgSend_void_nint(effectView, Selector.GetHandle("setMaterial:"), 13);
        objc_msgSend_void_nint(effectView, Selector.GetHandle("setBlendingMode:"), 0);
        objc_msgSend_void_double(effectView, Selector.GetHandle("setAlphaValue:"), Alpha);

        objc_msgSend_addSubview_positioned(contentView, ..., effectView, -1, IntPtr.Zero);
    }

    // 2. 窗口通用配置（始终执行，不受 Enabled 影响）
    objc_msgSend_void_bool(nsWindow, Selector.GetHandle("setHasShadow:"), true);
    var currentFrame = objc_msgSend_ret_CGRect(nsWindow, Selector.GetHandle("frame"));
    var newFrame = new CGRect(currentFrame.X, currentFrame.Y, 1372, 849);
    objc_msgSend_setFrame(nsWindow, Selector.GetHandle("setFrame:display:"), newFrame, true);
    objc_msgSend(nsWindow, Selector.GetHandle("center"));
}
```

> **注意**：窗口尺寸设置（步骤 2）必须始终执行，不能包含在 `if (Enabled)` 块内。
> 否则关闭毛玻璃后窗口尺寸会异常（不再执行 NSWindow 级别的 setFrame）。

#### App.xaml.cs

```csharp
public App(ISettingsService settingsService)
{
    InitializeComponent();
#if MACCATALYST
    // 从 SQLite 读取毛玻璃配置（重启生效）
    VibrancyHelper.Enabled = settingsService.Get("vibrancy_enabled", true);
    VibrancyHelper.Alpha = settingsService.Get("vibrancy_alpha", 0.85);
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

可通过 `setAlphaValue:` 进一步调节（0.0 = 完全透明，1.0 = 不透明）。当前默认 0.85，可在设置 UI 中调整（0.3~1.0，重启生效）。

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

## 用户可配置化

毛玻璃效果支持通过设置 UI（设置 → 外观 Tab）进行配置，修改后**重启生效**。

### 配置项

| 设置项 | 设置键 | 类型 | 默认值 | UI 控件 |
|--------|--------|------|--------|---------|
| 启用毛玻璃 | `vibrancy_enabled` | bool | `true` | 开关 |
| 透明度 | `vibrancy_alpha` | double | `0.85` | 滑块 (0.3~1.0) |

### 数据流

```
启动时：
  App 构造函数 (ISettingsService 注入)
  ├─ settingsService.Get("vibrancy_enabled", true) → VibrancyHelper.Enabled
  ├─ settingsService.Get("vibrancy_alpha", 0.85)   → VibrancyHelper.Alpha
  └─ VibrancyHelper.Register()
       └─ 通知触发 → ConfigureNSWindow()
            ├─ if (Enabled): 插入 NSVisualEffectView + setAlphaValue(Alpha)
            └─ 始终执行: setHasShadow + setFrame(1372×849) + center

用户修改时：
  SettingsDialog 外观 Tab
  └─ SettingsService.Set() → SQLite 持久化
  └─ UI 显示"修改后需重启应用生效"
```

### 为什么需要重启

`VibrancyHelper.Register()` 在 App 构造函数中执行，`ConfigureNSWindow` 在 NSWindow 创建的系统通知中触发，均在应用启动早期完成。NSVisualEffectView 一旦插入 AppKit 层后无法安全移除或重新配置，因此配置变更需要重启应用才能生效。

### ConfigureNSWindow 分层注意事项

`ConfigureNSWindow` 方法中的逻辑分为两层：

- **毛玻璃层**（受 `Enabled` 控制）：setOpaque、NSVisualEffectView 创建/配置/插入
- **窗口通用层**（始终执行）：setHasShadow、setFrame(1372×849)、center

窗口尺寸和居中逻辑**不能**放在 `if (Enabled)` 块内，否则关闭毛玻璃后窗口尺寸会异常。

## 参考资源

- [CatalystEffectViewChrome](https://github.com/steventroughtonsmith/CatalystEffectViewChrome) — AppKit NSVisualEffectView 方案（本方案基础）
- [CatalystTransparentChrome](https://github.com/steventroughtonsmith/CatalystTransparentChrome) — UISplitViewController sidebar 方案
- [Apple UIKit Mac Catalyst 文档](https://developer.apple.com/documentation/uikit/mac-catalyst) — 无透明窗口 API
- [MAUI PageHandler 源码](https://github.com/dotnet/maui/blob/main/src/Core/src/Handlers/Page/PageHandler.iOS.cs) — MapBackground 实现

---

## 深色模式主题系统

### 概述

深色模式当前仅支持**跟随系统**模式，自动跟随 macOS 系统外观切换。UI 中暂不开放手动选择浅色/深色。

### 架构设计

```
┌─────────────────────────────────────────────────────────────────┐
│                      MacThemeService.cs                          │
│  ┌─────────────────┐    ┌─────────────────┐    ┌─────────────┐  │
│  │ DetectSystem    │    │ ApplyTheme()    │    │ ThemeChanged│  │
│  │ DarkMode()      │───→│ ("system")      │───→│ Event       │  │
│  │                 │    │                 │    │             │  │
│  └─────────────────┘    └─────────────────┘    └──────┬──────┘  │
│           ▲                                           │          │
│           │ NSSystemAppearanceChangedNotification     │          │
│           │                                           ▼          │
│  ┌────────┴────────┐                          ┌─────────────┐   │
│  │ OnSystem        │                          │ Home.razor  │   │
│  │ Appearance      │                          │ OnThemeChanged│  │
│  │ Changed()       │                          │             │   │
│  │ (DispatchQueue) │                          └──────┬──────┘   │
│  └─────────────────┘                                 │          │
└──────────────────────────────────────────────────────┼──────────┘
                                                       │
                                                       ▼
                                              ┌─────────────────┐
                                              │   theme.js      │
                                              │  ┌─────────────┐│
                                              │  │ setDarkMode ││
                                              │  │ applyTheme  ││
                                              │  │ matchMedia  ││
                                              │  │  (监听系统)  ││
                                              │  └─────────────┘│
                                              └────────┬────────┘
                                                       │
                                                       ▼
                                              ┌─────────────────┐
                                              │ document.body   │
                                              │ .dark-mode      │
                                              └─────────────────┘
```

### 核心实现

#### MacThemeService.cs（平台层）

```csharp
public class MacThemeService : IThemeService
{
    private readonly ISettingsService _settingsService;
    private bool _isDarkMode;
    private bool _systemDarkMode;

    public bool IsDarkMode => _isDarkMode;
    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

    public void Initialize()
    {
        _systemDarkMode = DetectSystemDarkMode();
        var themeMode = _settingsService.Get("theme_mode", "system");
        ApplyTheme(themeMode);

        NSNotificationCenter.DefaultCenter.AddObserver(
            new NSString("NSSystemAppearanceChangedNotification"),
            OnSystemAppearanceChanged);
    }

    public void SetThemeMode(string mode)
    {
        _settingsService.Set("theme_mode", mode);
        ApplyTheme(mode);
    }

    public string GetThemeMode()
    {
        return _settingsService.Get("theme_mode", "system");
    }

    private void OnSystemAppearanceChanged(NSNotification notification)
    {
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            var newSystemDarkMode = DetectSystemDarkMode();
            if (newSystemDarkMode != _systemDarkMode)
            {
                _systemDarkMode = newSystemDarkMode;
                var themeMode = _settingsService.Get("theme_mode", "system");
                if (themeMode == "system")
                {
                    ApplyTheme(themeMode);
                }
            }
        });
    }

    private void ApplyTheme(string themeMode)
    {
        bool newDarkMode = themeMode switch
        {
            "dark" => true,
            "light" => false,
            _ => _systemDarkMode
        };

        if (newDarkMode != _isDarkMode)
        {
            _isDarkMode = newDarkMode;
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs { IsDarkMode = _isDarkMode });
        }
    }

    private bool DetectSystemDarkMode()
    {
        var currentTraitCollection = UITraitCollection.CurrentTraitCollection;
        return currentTraitCollection.UserInterfaceStyle == UIUserInterfaceStyle.Dark;
    }
}
```

#### theme.js（Web 层）

```javascript
(function() {
    'use strict';

    function applyDarkMode(isDark) {
        if (isDark) {
            document.body.classList.add('dark-mode');
        } else {
            document.body.classList.remove('dark-mode');
        }
    }

    function getSystemDarkMode() {
        return window.matchMedia('(prefers-color-scheme: dark)').matches;
    }

    function initTheme(mode) {
        if (mode === 'system') {
            applyDarkMode(getSystemDarkMode());
        } else {
            applyDarkMode(mode === 'dark');
        }
    }

    // 监听系统主题变化（双重保障）
    if (window.matchMedia) {
        window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function(e) {
            if (window._themeMode === 'system') {
                applyDarkMode(e.matches);
            }
        });
    }

    // .NET Interop 接口
    window.applyTheme = function(mode) {
        window._themeMode = mode;
        initTheme(mode);
    };

    window.setDarkMode = function(isDark) {
        applyDarkMode(isDark);
    };

    window.getSystemDarkMode = getSystemDarkMode;

    // 注意：不自动初始化，由 Blazor OnAfterRenderAsync 统一调用 applyTheme()，
    // 避免在用户偏好从 SQLite 加载前用错误模式初始化导致闪烁。
})();
```

#### Home.razor（Blazor 层）

```csharp
protected override void OnInitialized()
{
    ThemeService.ThemeChanged += OnThemeChanged;
}

private async void OnThemeChanged(object? sender, ThemeChangedEventArgs e)
{
    try
    {
        await InvokeAsync(async () =>
        {
            await JSRuntime.InvokeVoidAsync("setDarkMode", e.IsDarkMode);
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Home] Error in OnThemeChanged: {ex}");
    }
}

protected override async Task OnAfterRenderAsync(bool firstRender)
{
    if (firstRender)
    {
        var themeMode = ThemeService.GetThemeMode();
        await JSRuntime.InvokeVoidAsync("applyTheme", themeMode);
    }
}

public async ValueTask DisposeAsync()
{
    ThemeService.ThemeChanged -= OnThemeChanged;
    // ...
}
```

### CSS 变量方案

深色模式通过 CSS 变量实现，所有组件使用变量而非硬编码颜色。

**关键约束**：侧边栏、工具栏、文件列表等主要区域的 `background` 必须保持 `transparent`（或不设置），否则会遮挡 AppKit 层的毛玻璃效果。仅文字颜色、边框、阴影等通过 CSS 变量切换。

```css
:root {
    --color-text-primary: #1b1b1b;
    --color-text-secondary: #616161;
    --color-text-tertiary: #8b8b8b;
    --color-text-quaternary: #ababab;
    --glass-effect-bg: rgba(255, 255, 255, 0.6);
    --glass-effect-border: rgba(255, 255, 255, 0.18);
    --glass-effect-shadow: rgba(142, 142, 142, 0.19) 0px 6px 15px 0px;
    /* ... */
}

.dark-mode {
    --color-text-primary: #f5f5f5;
    --color-text-secondary: #9a9a9a;
    --color-text-tertiary: #5c5c5c;
    --color-text-quaternary: #4a4a4a;
    --glass-effect-bg: rgba(40, 40, 40, 0.6);
    --glass-effect-border: rgba(255, 255, 255, 0.12);
    --glass-effect-shadow: rgba(0, 0, 0, 0.3) 0px 6px 15px 0px;
    /* ... */
}
```

### 关键设计约束

1. **UIKit 层必须透明**：`.finder-sidebar`、`.finder-navbar`、`.finder-toolbar`、`.file-grid`、`.file-list`、`.finder-content-main` 等主要布局元素**不能**设置不透明的 `background`（如 `#ffffff`、`var(--color-bg-content)`），否则会遮挡 NSVisualEffectView 的毛玻璃效果。深色/浅色的背景色由 AppKit 层的毛玻璃自动处理。

2. **UITraitCollection 延迟检测**：使用 `DispatchQueue.MainQueue.DispatchAsync` 确保系统主题变化后 trait collection 已更新。

3. **双重保障机制**：
   - 平台层：`NSSystemAppearanceChangedNotification` → `MacThemeService` → `ThemeChanged` 事件
   - Web 层：`matchMedia('prefers-color-scheme: dark')` 直接监听，无需经过 .NET

4. **保留模式设置**：使用 `setDarkMode` 而非 `applyTheme` 响应事件，避免覆盖 `"system"` 模式。

5. **不自动初始化**：`theme.js` 不在加载时自动调用 `initTheme()`，由 Blazor `OnAfterRenderAsync(firstRender)` 统一用正确的用户偏好初始化，避免闪烁。

6. **弹窗主题同步**：MDialog 使用 `fk-dialog-glass`/`fk-dialog-glass-dense` 类，通过 CSS 变量自动继承 `body.dark-mode` 的样式，无需单独的 `.dark-mode .fk-dialog-glass-dense` 覆盖规则。

### 配置项

| 设置项 | 设置键 | 类型 | 默认值 | UI 控件 |
|--------|--------|------|--------|---------|
| 主题模式 | `theme_mode` | string | `"system"` | 下拉选择（当前仅"跟随系统"） |

> 注：`IThemeService` 接口保留了 `SetThemeMode("light"/"dark")` 能力，未来可在 UI 中开放手动选择。

### 涉及文件

| 文件 | 职责 |
|------|------|
| `Platforms/MacCatalyst/Services/MacThemeService.cs` | Mac Catalyst 平台主题检测与切换 |
| `Services/IThemeService.cs` | 主题服务接口定义 |
| `wwwroot/js/theme.js` | Web 端主题切换逻辑（不自动初始化） |
| `wwwroot/css/variables.css` | CSS 变量定义（浅色/深色） |
| `wwwroot/css/app.css` | 组件样式使用 CSS 变量（布局元素保持 transparent） |
| `Components/Pages/Home.razor` | 订阅主题事件，调用 JS Interop，首次渲染初始化主题 |
| `Components/Dialogs/SettingsDialog.razor` | 主题模式下拉选择 UI |
| `App.xaml.cs` | 初始化 ThemeService |
