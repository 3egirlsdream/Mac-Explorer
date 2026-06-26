# Mac Explorer

基于 .NET 10 Avalonia 的 macOS 文件资源管理器。

## 环境要求

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

## 打包命令

### 一、本地安装版本（直接运行，无需上架）

适用于开发测试或直接分发给用户安装的场景。不需要 Apple 开发者账号。

```bash
# 1. Release 构建（自动生成 .app 和 .dmg）
dotnet publish -c Release

# 构建产物位于:
# .app 包:  bin/Release/net10.0/Mac Explorer.app
# .dmg 包:  bin/Release/net10.0/MacExplorer-1.0.16-macos.dmg

# 2. 安装：打开 .dmg，将 Mac Explorer 拖入 Applications 文件夹即可
```

### 二、发布到 App Store

需要有效的 Apple 开发者账号和相应证书/描述文件。

#### 前置准备

1. 在 Apple Developer Portal 创建 App ID：`com.macexplorer.app`
2. 创建 Mac Catalyst 分发证书（Apple Distribution）和描述文件（Provisioning Profile）
3. 在 App Store Connect 创建应用记录

#### 打包命令

```bash
# 1. 发布构建
dotnet publish -c Release

# 2. 使用 xcrun notarytool 公证（推荐）
xcrun notarytool submit bin/Release/net10.0/MacExplorer-1.0.16-macos.dmg \
  --apple-id "your-apple-id@example.com" \
  --password "app-specific-password" \
  --team-id "TEAM_ID" \
  --wait

# 3. 使用 Transporter 或 xcrun 上传到 App Store Connect
xcrun altool --upload-app \
  -f bin/Release/net10.0/MacExplorer-1.0.16-macos.dmg \
  -t macos \
  -u "your-apple-id@example.com" \
  -p "app-specific-password"
```

## 项目结构

```
MacExplorer/
├── App.axaml               # 应用配置与资源注册
├── App.axaml.cs            # 应用启动与 DI 配置
├── Program.cs              # 入口点
├── ViewLocator.cs          # ViewModel-First 视图定位器
├── MacExplorer.csproj      # 项目文件（Avalonia + macOS 原生构建）
├── Assets/                 # 主题、样式、图标资源
│   ├── ThemeTokens.axaml   # 语义化设计 Token（颜色、字号、圆角）
│   ├── Styles.axaml        # 全局样式
│   ├── ComponentStyles.axaml # 组件样式
│   └── Icons.cs            # SVG 图标常量
├── Controls/               # 自定义控件
│   ├── AppWindow           # 透明毛玻璃窗口
│   ├── WindowTitleBar      # 自定义标题栏
│   ├── DialogHost          # 弹窗宿主容器
│   └── SurfaceCard         # 卡片容器
├── Views/                  # Avalonia XAML 视图
│   ├── MainWindow          # 主窗口
│   ├── HomeView            # 首页
│   ├── FileListView        # 文件列表
│   ├── FinderSidebarView   # 侧边栏
│   ├── FinderToolbar       # 工具栏
│   ├── BreadcrumbBar       # 面包屑导航
│   ├── ContextMenuView     # 右键菜单
│   ├── InfoPanelView       # 信息面板
│   ├── AiView              # AI 搜索视图
│   └── Dialogs/            # 对话框（设置、压缩、删除确认等）
├── ViewModels/             # 视图模型（CommunityToolkit.Mvvm）
├── Models/                 # 数据模型
├── Services/               # 业务服务接口与实现
│   └── Impl/               # 服务实现
├── Indexing/               # SQLite FTS5 全文文件索引
├── Converters/             # 值转换器
├── Platforms/              # 平台特定代码
│   ├── MacCatalyst/        # macOS 平台服务（18 项）
│   │   └── Services/       # ObjCBridge 及各平台服务实现
│   └── MacOS/              # 原生代码
│       ├── ImageAnalysisHelper.swift  # Vision 框架 AI 图像分析
│       ├── NativeFileDrag.mm          # 原生文件拖拽
│       └── MacWindowChrome.cs         # 窗口外观控制
└── Tests/                  # 单元测试项目
```

## 技术栈

| 技术 | 用途 |
|------|------|
| .NET 10 | 运行时 |
| Avalonia 12 | UI 框架 |
| CommunityToolkit.Mvvm | MVVM 模式 |
| Microsoft.Data.Sqlite | 文件索引持久化（FTS5） |
| SharpCompress | 归档压缩（zip/tar/gz） |
| SSH.NET | SFTP 远程文件操作 |
| Svg.Skia | SVG 图标渲染 |

## 核心功能

- **文件浏览**：列表/网格视图、排序、分组、过滤
- **文件操作**：新建、重命名、复制、剪切、粘贴、删除
- **导航**：面包屑导航、前进/后退、收藏夹、固定文件夹
- **搜索**：FTS5 全文索引、AI 智能搜索
- **AI 分析**：图片人物识别、场景分类、地点提取
- **右键菜单**：Web 端 + macOS 原生 NSMenu 双通道
- **归档**：zip/tar/gz 压缩与解压
- **远程连接**：SFTP 服务器连接与文件编辑
- **Quick Look**：macOS 原生快速预览
- **主题**：浅色/深色模式、毛玻璃透明效果
