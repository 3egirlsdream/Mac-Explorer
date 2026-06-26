# Mac Explorer

基于 .NET 10 Avalonia 的 macOS 文件资源管理器。

## 下载安装（用户）

从 [GitHub Releases](https://github.com/3egirlsdream/Mac-Explorer/releases) 下载最新 `MacExplorer-x.x.x-macos.dmg`。

1. 双击打开 DMG，将 `Mac Explorer` 拖入 `Applications` 文件夹
2. 首次启动：**右键 → 打开**（自签名 app 需要手动信任一次，之后永久信任）
3. 无需安装 .NET 运行时，129MB 自包含发布

## 环境要求（开发者）

- .NET 10 SDK
- Xcode（含 Command Line Tools）
- macOS 15.0+

## 快速开始

```bash
# 还原依赖
dotnet restore

# Debug 构建并运行
dotnet build -c Debug
open "bin/Debug/net10.0/Mac Explorer.app"

# 清理构建产物
dotnet clean
```

## 打包发布

### 一、自包含 DMG（推荐，无需 .NET 运行时）

```bash
# Release 构建（自动生成 .app 和 .dmg）
dotnet publish -c Release

# 构建产物:
# .app:  bin/Release/net10.0/osx-arm64/Mac Explorer.app
# .dmg:  bin/Release/net10.0/osx-arm64/MacExplorer-$(Version)-macos.dmg
```

- `RuntimeIdentifier=osx-arm64` + `SelfContained=true`
- .NET 10 运行时、Avalonia 原生库全部打包进 .app，用户无需安装任何依赖
- DMG 内包含 `/Applications` 快捷方式，拖拽即可安装

### 二、签名公证后分发 App Store

需要 Apple Developer 账号（$99/年）。

```bash
# 1. 构建
dotnet publish -c Release

# 2. 公证
xcrun notarytool submit bin/Release/net10.0/osx-arm64/MacExplorer-*-macos.dmg \
  --apple-id "your-apple-id@example.com" \
  --password "app-specific-password" \
  --team-id "TEAM_ID" \
  --wait
```

## 项目结构

```
MacExplorer/
├── App.axaml                 # 应用入口 XAML
├── App.axaml.cs              # 应用启动、DI、生命周期
├── Program.cs                # 程序入口
├── ViewLocator.cs            # ViewModel-First 视图定位
├── MacExplorer.csproj        # 项目文件（self-contained + DMG 打包）
├── global.json               # .NET SDK 版本锁定
├── Assets/
│   ├── appicon.svg           # SVG 源图标（全出血，macOS 自动圆角）
│   ├── appicon.icns          # macOS 图标（浏览器渲染 + iconutil 打包）
│   ├── appicon.ico           # Windows 图标
│   ├── ThemeTokens.axaml     # 语义化设计 Token
│   ├── Styles.axaml          # 全局样式
│   ├── ComponentStyles.axaml # 组件样式
│   └── Icons.cs              # SVG 图标常量
├── Controls/
│   ├── AppWindow             # 透明毛玻璃窗口
│   ├── WindowTitleBar        # 自定义标题栏
│   ├── DialogHost            # 弹窗宿主容器
│   └── SurfaceCard           # 卡片容器
├── Views/
│   ├── MainWindow            # 主窗口
│   ├── HomeView              # 首页搜索
│   ├── FileListView          # 文件列表（网格/详情双视图）
│   ├── FinderSidebarView     # Finder 风格侧边栏
│   ├── FinderToolbar         # 工具栏
│   ├── BreadcrumbBar         # 面包屑导航
│   ├── ContextMenuView       # 右键菜单
│   ├── InfoPanelView         # 元数据预览面板
│   ├── AiView                # AI 智能搜索
│   └── Dialogs/              # 设置、压缩、删除确认等对话框
├── ViewModels/               # MVVM 视图模型
├── Models/                   # 数据模型（文件条目、侧边栏、AI 标签等 33 个）
├── Services/
│   ├── Impl/                 # 服务实现（搜索、压缩、Git、SFTP 等）
│   └── I*.cs                 # 服务接口
├── Indexing/                 # SQLite FTS5 全文文件索引
├── Platforms/
│   ├── MacCatalyst/Services/ # macOS ObjC 桥接（18 项平台服务）
│   └── MacOS/
│       ├── ImageAnalysisHelper.swift  # Vision 框架 AI 图像分析
│       ├── NativeFileDrag.mm          # 原生文件拖拽
│       └── MacWindowChrome.cs         # 窗口外观控制
└── Tests/                    # 单元测试
```

## 技术栈

| 技术 | 用途 |
|------|------|
| .NET 10 | 运行时（self-contained 分发） |
| Avalonia 12 | 原生 UI 框架（Metal GPU 渲染） |
| CommunityToolkit.Mvvm | MVVM 源码生成器 |
| Microsoft.Data.Sqlite | SQLite FTS5 全文索引 |
| SharpCompress | zip/tar/gz/7z 压缩解压 |
| SSH.NET | SFTP 远程文件管理 |
| Svg.Skia | SVG 图标渲染 |

## 核心功能

- **多标签页**：独立导航历史、排序状态和视图模式
- **文件浏览**：网格图标 / 详细信息列表双视图，列宽拖拽调整
- **AI 智能分析**：Apple Vision 框架本地识别面孔、场景、地点，全程离线
- **全文搜索**：FTS5 实时索引，中文分词、拼音匹配、词云可视化
- **Git 状态**：文件图标叠加 Git 状态（新增/修改/未跟踪）
- **SFTP 远程**：SSH.NET 安全连接，在线编辑、拖放上传
- **Quick Look**：空格快速预览图片/文档
- **归档**：zip/tar/gz/7z 压缩解压，支持内浏览和密码保护
- **后台任务**：悬浮面板实时进度，多任务并行
- **文件评分**：1–5 星评分，持久化存储，按评分排序
- **主题**：亮/暗自动切换，毛玻璃 + 亚克力材质
- **右键菜单**：NSMenu 原生 + Web 双通道
- **面包屑导航**：点击跳转、地址栏编辑、搜索建议
