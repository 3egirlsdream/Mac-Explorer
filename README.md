<p align="center">
  <img src="Assets/appicon.svg" width="128" alt="Mac Explorer">
</p>

<h1 align="center">Mac Explorer</h1>
<p align="center">多工作区 macOS 文件管理器 &nbsp;|&nbsp; A multi-pane file manager for macOS</p>

<p align="center">
  <a href="https://github.com/3egirlsdream/Mac-Explorer/releases/latest"><img src="https://img.shields.io/github/v/release/3egirlsdream/Mac-Explorer?color=3b82f6&label=Download" alt="Release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/3egirlsdream/Mac-Explorer?color=2f9e64" alt="License"></a>
  <img src="https://img.shields.io/badge/platform-macOS%20Apple%20Silicon-8b94a3" alt="macOS Apple Silicon">
  <img src="https://img.shields.io/badge/runtime-.NET%2010%20Self--Contained-512bd4" alt="Runtime">
</p>

<p align="center">
  <a href="https://3egirlsdream.github.io/Mac-Explorer/">官网 · Website</a> ·
  <a href="https://github.com/3egirlsdream/Mac-Explorer/releases/latest">下载 · Download</a> ·
  <a href="https://github.com/3egirlsdream/Mac-Explorer/releases">更新记录 · Releases</a> ·
  <a href="https://github.com/3egirlsdream/Mac-Explorer/issues">反馈 · Issues</a>
</p>

---

<details open>
<summary><b>中文</b></summary>

Mac Explorer 是为 macOS 打造的文件管理器。在一个窗口里并排浏览多个目录，用筛选、搜索和 Finder 标签整理文件，按空格预览文件夹与压缩包，并直接连接 SFTP 服务器。支持列表与网格视图、浅色与深色主题，以及 macOS 原生拖放、菜单和 Quick Look 集成。

## 最近更新

当前功能介绍对应 **v1.0.40**，完整版本记录见 [GitHub Releases](https://github.com/3egirlsdream/Mac-Explorer/releases)。

- **统一 Finder 标签**：侧边栏、文件列表和信息面板共用标签，支持自定义标签、固定标签及按标签浏览文件；原有收藏集自动迁移为标签。
- **表头筛选与面包屑导航**：按名称、类型、修改日期、大小组合筛选；在面包屑下拉中搜索子目录并直接跳转。
- **新版文件列表**：默认启用自绘虚拟化列表，支持列表与网格、分组、框选、拖放和行内重命名，按可见区域加载缩略图。
- **多工作区与超级预览**：支持最多四个窗格及多种分屏布局；空格打开窗口内预览，继续浏览文件夹、压缩包及嵌套压缩包。

## 核心功能

| 功能 | 说明 |
|------|------|
| **多标签与分屏** | 标签页独立维护导航历史、排序和视图；支持横向、纵向、四宫格及主次窗格布局 |
| **列表、网格与筛选** | 切换列表/网格，按类型、日期或大小分组；表头支持多选筛选，同列选项取并集，不同列条件取交集 |
| **路径导航** | 面包屑逐级跳转、子目录下拉搜索、路径输入，以及置顶文件夹与最近访问入口 |
| **Finder 标签** | 为文件添加或移除标签，在侧边栏按标签聚合浏览，并写入 macOS Finder 标签；添加标签不移动或复制文件 |
| **文件搜索** | 当前目录搜索与全局搜索，结合 SQLite FTS5 文件名索引、已生成的 OCR 文本和 AI 标签；支持搜索结果预览 |
| **AI 图片分析** | 使用 Apple Vision 在设备上识别人脸、图中文字和场景分类；结合照片元数据整理日期与地点，地点名称解析可能需要联网 |
| **超级预览** | 空格预览图片、PDF、文本及系统支持的文档/媒体；文件夹和压缩包可继续浏览，具体格式效果取决于 macOS Quick Look 支持 |
| **压缩包管理** | 浏览和解压 ZIP、TAR、7Z 等格式；创建 ZIP、TAR.GZ、TAR.BZ2，支持 ZIP 密码保护，以及解压到当前目录或独立目录 |
| **SFTP 远程管理** | 保存服务器连接，浏览、上传、下载和管理远程文件；通过本地应用编辑远程文件后自动回传修改 |
| **批量重命名** | 查找替换、前后缀、序号、日期和大小写转换，执行前预览结果 |
| **Git 与文件操作** | 显示 Git 状态，支持复制、移动、拖放、废纸篓操作和可撤销的文件操作 |
| **外观与更新** | 浅色/深色主题、毛玻璃效果、标准/紧凑/舒适排版密度、交互颜色设置，以及应用内更新检查与下载 |

### 标签与文件位置

从旧版本升级时，收藏集名称及其文件关联会迁移为标签，文件保留在原位置。拖入标签或向标签粘贴只添加标签关联；删除标签只移除该标签及关联，不删除文件。Finder 标签写入失败时会保留待同步记录，并在下次启动时重试。

## 安装

推荐使用 **macOS 15 或更新版本、Apple Silicon Mac**。当前发布包为 `osx-arm64` 自包含版本，无需另外安装 .NET 运行时。

1. 从 [最新发布](https://github.com/3egirlsdream/Mac-Explorer/releases/latest) 下载 `MacExplorer-<版本>-macos.dmg`。
2. 打开 DMG，将 **Mac Explorer** 拖入 **Applications** 文件夹。
3. 从 Applications 启动应用。发布页也提供 ZIP 包。

## 常用快捷键

| 快捷键 | 操作 |
|--------|------|
| `⌘ T` / `⌘ W` | 新建 / 关闭标签页；关闭最后一个标签页时关闭窗口 |
| `Control Tab` / `Control Shift Tab` | 切换到下一个 / 上一个标签页 |
| `⌘ L` | 聚焦路径输入 |
| `⌘ F` | 切换当前工作区搜索 |
| `⌘ K` / `⌘ Shift F` | 打开全局搜索 |
| `Space` | 预览单个选中的文件或文件夹 |
| `Esc` | 退出预览或关闭当前弹出界面 |
| `⌘ Z` | 撤销最近一次支持撤销的文件操作 |

</details>

<details>
<summary><b>English</b></summary>

Mac Explorer is a file manager built for macOS. Browse several folders side by side, organize files with filters, search and Finder tags, press Space to explore folders and archives, and connect to SFTP servers from the same window. It includes list and grid views, light and dark themes, native drag and drop, context menus, and Quick Look integration.

## Recent Updates

This overview reflects **v1.0.40**. See [GitHub Releases](https://github.com/3egirlsdream/Mac-Explorer/releases) for the full release history.

- **Unified Finder tags**: the sidebar, file list and information panel share custom tags, pinned tags and tag-based browsing. Existing collections migrate to tags automatically.
- **Column filters and breadcrumb navigation**: combine name, type, modification date and size filters; search subfolders inside breadcrumb dropdowns and navigate directly.
- **Updated file views**: a custom virtualized file list is enabled by default, with list/grid views, grouping, selection rectangles, drag and drop, inline renaming, and thumbnails loaded for visible items.
- **Multiple panes and Super Preview**: use up to four panes in several layouts; press Space to open an in-window preview and explore folders, archives and nested archives.

## Core Features

| Feature | Description |
|---------|-------------|
| **Tabs and split panes** | Independent navigation history, sorting and view per tab; horizontal, vertical, four-pane grid and main/secondary layouts |
| **List, grid and filters** | Switch views, group by type/date/size, and select column filters; options within a column use OR, while different columns use AND |
| **Path navigation** | Clickable breadcrumbs, searchable subfolder dropdowns, direct path entry, pinned folders and recent locations |
| **Finder tags** | Apply or remove tags, browse tagged files from the sidebar, and write tags to macOS Finder; tagging keeps files in place |
| **File search** | Current-folder and global search using SQLite FTS5 filename indexing, generated OCR text and AI tags, with previews in search results |
| **AI image analysis** | On-device Apple Vision for face detection, OCR and scene classification; photo metadata supplies dates and locations, while place-name lookup may require a network connection |
| **Super Preview** | Press Space to preview images, PDFs, text and system-supported documents/media, or browse folders and archives; format coverage depends on macOS Quick Look support |
| **Archive management** | Browse and extract ZIP, TAR, 7Z and other formats; create ZIP, TAR.GZ and TAR.BZ2 archives, protect ZIPs with passwords, and extract into the current or a separate folder |
| **SFTP remote access** | Save connections, browse, upload, download and manage remote files; edit through a local application and automatically upload changes |
| **Batch rename** | Preview find/replace, prefixes, suffixes, sequences, dates and case conversions before applying changes |
| **Git and file operations** | Git status indicators, copy, move, drag and drop, Trash actions, and undo for supported file operations |
| **Appearance and updates** | Light/dark themes, frosted glass, standard/compact/comfortable typography, configurable interaction colors, and in-app update checks and downloads |

### Tags Keep Files in Place

Upgrading migrates collection names and file associations to tags without relocating files. Dropping or pasting files onto a tag adds the tag only. Deleting a tag removes that tag and its associations without deleting files. Failed Finder tag writes remain pending and are retried on the next launch.

## Installation

An **Apple Silicon Mac running macOS 15 or later** is recommended. Current releases are self-contained `osx-arm64` builds and do not require a separate .NET runtime.

1. Download `MacExplorer-<version>-macos.dmg` from the [latest release](https://github.com/3egirlsdream/Mac-Explorer/releases/latest).
2. Open the DMG and drag **Mac Explorer** into **Applications**.
3. Launch the app from Applications. A ZIP package is also available on the release page.

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| `⌘ T` / `⌘ W` | Open / close a tab; closing the last tab closes the window |
| `Control Tab` / `Control Shift Tab` | Next / previous tab |
| `⌘ L` | Focus path input |
| `⌘ F` | Toggle search in the active workspace |
| `⌘ K` / `⌘ Shift F` | Open global search |
| `Space` | Preview one selected file or folder |
| `Esc` | Exit preview or dismiss the current popup |
| `⌘ Z` | Undo the most recent supported file operation |

</details>

---

## 技术栈 · Tech Stack

| 技术 Technology | 用途 Purpose |
|-----------------|--------------|
| .NET 10 | 自包含桌面运行时 · Self-contained desktop runtime |
| Avalonia 12 | 桌面 UI 与自绘文件列表 · Desktop UI and custom file views |
| CommunityToolkit.Mvvm | MVVM 与源码生成器 · MVVM and source generators |
| Microsoft.Data.Sqlite / SQLite FTS5 | 文件索引、标签与设置存储 · File indexing, tags and settings |
| SharpCompress / DotNetZip | 压缩包读取、写入与 ZIP 加密 · Archives and encrypted ZIP creation |
| SSH.NET | SFTP 远程文件访问 · Remote file access |
| Svg.Skia / Fluent UI System Icons | SVG 渲染与界面图标 · SVG rendering and UI icons |
| Apple Vision / Quick Look / AppKit | 图片分析、预览与原生系统集成 · Image analysis, previews and native integration |

## 项目结构 · Structure

```text
Mac-Explorer/
├── App.axaml / App.axaml.cs   应用入口、依赖注入与生命周期
├── Assets/                   应用图标、主题、排版与组件样式
├── Controls/                 自绘文件列表、窗口与自定义控件
├── Views/                    工作区、文件列表、侧边栏、预览与设置
├── ViewModels/               标签页、导航、筛选、标签等视图模型
├── Models/                   文件、标签、筛选与操作数据模型
├── Services/                 服务接口与实现：文件、标签、压缩、Git、SFTP
├── Indexing/                 SQLite 索引、数据库结构与迁移
├── Platforms/MacCatalyst/    macOS 平台服务与 Objective-C 桥接
├── Platforms/MacOS/          Swift / Objective-C++ 原生辅助程序
├── Tests/MacExplorer.Tests/  单元测试与 Avalonia 无头交互测试
├── Tools/Performance/        文件列表性能测量工具
├── doc/                     构建与开发文档
├── docs/                    GitHub Pages 官网与设计文档
└── .github/workflows/       自动发布流程
```

## 开发 · Development

开发环境：**.NET 10 SDK、Xcode / Command Line Tools、macOS 15+**。原生辅助程序的构建需要 Swift 编译器及 macOS SDK。

Development requires **.NET 10 SDK, Xcode / Command Line Tools, and macOS 15+**. Native helpers use the Swift compiler and macOS SDK.

```bash
git clone https://github.com/3egirlsdream/Mac-Explorer.git
cd Mac-Explorer
dotnet restore MacExplorer.csproj
dotnet build MacExplorer.csproj -c Debug
open "bin/Debug/net10.0/osx-arm64/Mac Explorer.app"
```

运行测试 · Run tests:

```bash
dotnet test Tests/MacExplorer.Tests/MacExplorer.Tests.csproj -p:SkipMacOSReleaseDMG=true
```

构建 Release 应用与 DMG · Build the Release app and DMG:

```bash
dotnet build MacExplorer.csproj -c Release
```

产物位于 `bin/Release/net10.0/osx-arm64/`。更多打包、签名与公证说明见 [构建文档 · Build guide](doc/BUILD.md)。

Artifacts are written to `bin/Release/net10.0/osx-arm64/`. See the [build guide](doc/BUILD.md) for packaging, signing and notarization.

## License

This project is licensed under the [GNU General Public License v3.0 or later](LICENSE) (GPL-3.0-or-later).
