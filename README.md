<p align="center">
  <img src="Assets/appicon.svg" width="128" alt="Mac Explorer">
</p>

<h1 align="center">Mac Explorer</h1>
<p align="center">macOS 文件管理器 &nbsp;|&nbsp; macOS File Manager</p>

<p align="center">
  <a href="https://github.com/3egirlsdream/Mac-Explorer/releases"><img src="https://img.shields.io/github/v/release/3egirlsdream/Mac-Explorer?color=3b82f6&label=Download" alt="Release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/3egirlsdream/Mac-Explorer?color=2f9e64" alt="License"></a>
  <img src="https://img.shields.io/badge/platform-macOS%2015.0%2B-8b94a3" alt="Platform">
  <img src="https://img.shields.io/badge/runtime-.NET%2010%20Self--Contained-512bd4" alt="Runtime">
</p>

---

<details open>
<summary><b>中文</b></summary>

Mac Explorer 是为 macOS 打造的现代文件管理器。它把本地浏览、全文搜索、AI 图片理解、SFTP 远程管理、压缩解压、Git 状态和 Quick Look 整合进一个优雅高效的工作台。

## 为什么选 Mac Explorer

macOS Finder 够用，但不够好用。Mac Explorer 补上了 Windows 文件管理器里那些 macOS 缺失已久的能力——多标签页、Git 集成、实时全文搜索、内建压缩预览——同时保留了 Mac 的毛玻璃质感和流畅体验。

## 核心功能

| 功能 | 说明 |
|------|------|
| 🗂️ **多标签页** | 像浏览器一样管理文件夹，每个标签独立维护历史、排序和视图 |
| 🔍 **FTS5 全文搜索** | SQLite FTS5 实时索引，中文分词 + 拼音匹配，毫秒级结果 |
| 🤖 **AI 图片分析** | Apple Vision 本地识别面孔、场景、地点，完全离线 |
| ⎇ **Git 状态标记** | 文件图标叠加 Git 状态——新增/修改/未跟踪一目了然 |
| 🌐 **SFTP 远程管理** | SSH.NET 安全连接，像操作本地文件一样管理远程服务器 |
| 📦 **压缩解压** | ZIP/TAR/GZ/7Z，内浏览免解压，分卷 + 密码保护 |
| 👁️ **Quick Look** | 空格键快速预览图片、文档、视频 |
| ⭐ **文件评分** | 1–5 星评分，持久化存储，支持按评分排序 |
| 🖱️ **右键菜单** | NSMenu 原生 + 自定义双通道，快捷键标注清晰 |
| 🪟 **毛玻璃主题** | 亮/暗自动切换，侧边栏 + 菜单 + 面板全透明 |

## 快速安装

从 [Releases](https://github.com/3egirlsdream/Mac-Explorer/releases) 下载 `MacExplorer-x.x.x-macos.dmg`：

1. 双击打开 DMG
2. 拖入 Applications 文件夹
3. 首次启动：**右键 → 打开**（自签名 app 需手动信任一次）

无需安装 .NET 运行时，129MB 自包含打包，Apple Silicon 原生。

</details>

<details>
<summary><b>English</b></summary>

Mac Explorer is a modern file manager built for macOS. It combines local browsing, full-text search, AI-powered image understanding, SFTP remote access, archive management, Git status, and Quick Look into one elegant, efficient workspace.

## Why Mac Explorer

macOS Finder works, but it could be better. Mac Explorer brings the capabilities Windows file managers have had for years — multi-tab browsing, Git integration, real-time full-text search, built-in archive preview — while preserving the frosted glass aesthetic and fluid feel of macOS.

## Core Features

| Feature | Description |
|---------|-------------|
| 🗂️ **Multi-Tab** | Browse folders like browser tabs — independent history, sort, and view per tab |
| 🔍 **FTS5 Full-Text Search** | SQLite FTS5 real-time index, CJK tokenization + pinyin matching, millisecond results |
| 🤖 **AI Image Analysis** | On-device Apple Vision for face, scene, and landmark recognition — fully offline |
| ⎇ **Git Status** | File icon overlays for Git status — added/modified/untracked at a glance |
| 🌐 **SFTP Remote** | SSH.NET secure connections, manage remote servers like local folders |
| 📦 **Archives** | ZIP/TAR/GZ/7Z, browse without extracting, multi-volume + password protection |
| 👁️ **Quick Look** | Press Space to preview images, documents, and video instantly |
| ⭐ **File Rating** | 1–5 star ratings, persistent storage, sort by rating |
| 🖱️ **Context Menu** | Dual-channel — native NSMenu + custom overlay with shortcut labels |
| 🪟 **Frosted Glass** | Light/Dark auto-switch, all panels use macOS vibrancy + acrylic materials |

## Quick Install

Download `MacExplorer-x.x.x-macos.dmg` from [Releases](https://github.com/3egirlsdream/Mac-Explorer/releases):

1. Open the DMG
2. Drag Mac Explorer into Applications
3. First launch: **Right-click → Open** (one-time trust for self-signed app)

No .NET runtime required — 129 MB self-contained, native Apple Silicon.

</details>

---

## 技术栈 &nbsp;·&nbsp; Tech Stack

| 技术 | 用途 Purpose |
|------|-------------|
| .NET 10 | 运行时 · Runtime |
| Avalonia 12 | 原生 UI 框架 · Native UI (Metal GPU) |
| CommunityToolkit.Mvvm | MVVM 源码生成器 · Source Generators |
| Microsoft.Data.Sqlite | SQLite FTS5 全文索引 · Full-Text Index |
| SharpCompress | 压缩解压 · Archive Handling |
| SSH.NET | SFTP 远程 · Remote File Access |
| Svg.Skia | SVG 图标渲染 · Icon Rendering |
| Apple Vision | AI 图像分析 · Image Analysis |

## 项目结构 &nbsp;·&nbsp; Structure

```
MacExplorer/
├── App.axaml / App.axaml.cs       应用入口、DI、生命周期
├── Assets/                        appicon.svg/icns、主题、样式
├── Controls/                      自定义控件（毛玻璃窗口、标题栏、卡片）
├── Views/                         XAML 视图（主窗口、文件列表、侧边栏…）
├── ViewModels/                    MVVM 视图模型
├── Models/                        33 个数据模型
├── Services/Impl/                 服务实现（搜索、压缩、Git、SFTP…）
├── Indexing/                      SQLite FTS5 索引引擎
├── Platforms/MacCatalyst/         18 项 ObjC 桥接平台服务
├── Platforms/MacOS/               Swift/ObjC 原生代码（Vision、拖拽、窗口）
├── doc/                           开发文档
└── Tests/                         单元测试
```

## 开发 &nbsp;·&nbsp; Development

环境要求：**.NET 10 SDK** + **Xcode** (Command Line Tools) + **macOS 15.0+**

```bash
dotnet restore
dotnet build -c Debug
open "bin/Debug/net10.0/Mac Explorer.app"
```

构建文档：[doc/BUILD.md](doc/BUILD.md)（打包、签名、公证、App Store 上架）

## License

[MIT](LICENSE) © 2026 Mac Explorer
