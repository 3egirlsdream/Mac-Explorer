# Glass Demo：侧栏材质对照

独立 Avalonia 窗口，不加载主程序的服务、数据库或用户配置。
默认 **选定样式**：浅色采用低饱和柔光，深色采用无亮边的中性灰半透明面板。
这套应用内材质已接入主界面的左侧卡片；右侧区域不使用玻璃。

## 运行

在仓库根目录执行：

```sh
dotnet build Tools/GlassDemo/GlassDemo.csproj
open 'Tools/GlassDemo/bin/Debug/net10.0/osx-arm64/Glass Demo.app'
```

面向 macOS arm64，需要 .NET 10 与 Xcode SDK。浅色原生对照在 macOS 26+ 使用
NSGlassEffectView，旧系统回退 NSVisualEffectView。深色原生对照使用普通半透明 NSView，
不会通过 NSGlassEffectView 绘制亮边；页面对此有明确标注。

## 选定的搭配

- **浅色**：柔白底，极淡的蓝灰与暖米色柔光；玻璃保持透明填充与细薄高光。
- **深色**：中性灰半透明面板，关闭折射、白色亮边、内阴影与外阴影。
- **深色交互**：hover 和选中状态使用低透明度的白色填充；键盘焦点保留细蓝线。
- **范围**：主界面只有左侧卡片及其局部交互使用这些样式。

`Controls/SidebarAppearance.cs` 由主程序和 Demo 共用，统一背景生成和材质参数。
`Controls/SidebarBackdrop.cs` 在主程序中作为玻璃的同级背景供组件库采样；
只在尺寸或主题变化时重新生成位图，背景本身不播放动画。

## 其他对照按钮

- **柔和弧面 / 低饱和柔光 / 极淡细纹**：保留三组设计候选，左右使用同一份 PNG 数据。
- **纯色 / 测试条纹**：观察缺少背景变化和存在明确纹理时的差别。
- **桌面 / 背后窗口**：外部背景实验。独立测试窗口不接收鼠标、不成为主窗口，切换模式后隐藏，关闭 Demo 时销毁。
- **浅色 / 深色**：只改变 Demo 的主题。
- **Clear / Regular**：只改变浅色原生玻璃的样式。
- **原生底材**：只在桌面 / 背后窗口模式添加 behindWindow 系统毛玻璃。
- **移动测试条纹**：移动诊断图案；设计背景保持静止。
- **玻璃开 / 关**：保留文字与背景，用于材质前后对照。

## 验证记录（2026-09-14，macOS 26.5.2）

- Demo 实机窗口验证了浅深色搭配、无亮边的深色外观、透明 hover 与 Tab 焦点。
- 主项目编译通过，相关 sidebar / workspace 测试 **14 / 14** 通过。
- 主界面控制树使用 Skia Headless 渲染，验证宽侧栏、窄侧栏和浅→深→浅切换。
- 深色按钮的高光、折射、背景采样透明度均为 0；hover 填充为 `#0CFFFFFF`，选中填充为 `#16FFFFFF`。
- 深色卡片的阴影关闭；浅色卡片四周阴影仍能扩散。滤镜失败为 0。
- 主界面预览使用测试数据，不是用户真实目录截图。

截图：

- `artifacts/liquidglass-demo/chosen-light.png`、`chosen-dark.png`：选定方案的原生 Demo 截图。
- `artifacts/sidebar-appearance/Light-1280.png`、`Dark-1280.png`：实际主界面控件的宽侧栏渲染。
- `artifacts/sidebar-appearance/Light-1000.png`、`Dark-1000.png`：窄侧栏渲染。
- 原有三组候选和条纹 / 跨窗口实验截图仍保留在 `artifacts/liquidglass-demo/`。

此前的跨窗口桌面通透实验没有验证通过。当前迁移的是用户选定的**应用内柔光背景与深色面板**，
没有将原生窗口玻璃实验迁移到主程序，也不声称已实现对桌面的折射。

## 资料

- Apple NSGlassEffectView：https://developer.apple.com/documentation/appkit/nsglasseffectview
- Apple behindWindow：https://developer.apple.com/documentation/appkit/nsvisualeffectview/blendingmode-swift.enum/behindwindow
- LiquidGlassAvaloniaUI：https://github.com/KaranocaVe/LiquidGlassAvaloniaUI
