<p align="center"><a href="README.md">English</a> · <strong>简体中文</strong></p>

<p align="center">
  <img src="Assets/appicon.svg" width="112" alt="Mac Explorer 图标">
</p>

<h1 align="center">Mac Explorer</h1>
<p align="center">为 macOS 打造的文件工作台：浏览、查找、预览、整理与分享。</p>

<p align="center">
  <a href="https://github.com/3egirlsdream/Mac-Explorer/releases/latest"><img src="https://img.shields.io/github/v/release/3egirlsdream/Mac-Explorer?color=3b82f6&label=Download" alt="最新版本"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/3egirlsdream/Mac-Explorer?color=2f9e64" alt="许可证"></a>
  <img src="https://img.shields.io/badge/macOS-14%2B-8b94a3" alt="macOS 14 或更新版本">
  <img src="https://img.shields.io/badge/Apple%20Silicon%20%26%20Intel-supported-8b94a3" alt="支持 Apple Silicon 与 Intel">
</p>

<p align="center">
  <a href="https://3egirlsdream.github.io/Mac-Explorer/">官网</a> ·
  <a href="https://github.com/3egirlsdream/Mac-Explorer/releases/latest">下载</a> ·
  <a href="CHANGELOG.md">更新日志</a> ·
  <a href="https://3egirlsdream.github.io/Mac-Explorer/privacy/">隐私政策</a> ·
  <a href="https://github.com/3egirlsdream/Mac-Explorer/issues">反馈</a>
</p>

Mac Explorer 将日常文件工作集中在一个 macOS 应用里。并排浏览多个目录，在不移动原文件的情况下归集资料，搜索文件名与已分析的内容，先预览再打开，并在同一个工作台处理本地文件、压缩包和 SFTP 远程文件。

![Mac Explorer 工作台](Assets/readme-workspace-1.0.54.jpg)

*截图：v1.0.54，2026 年 10 月 10 日使用隔离环境与演示文件拍摄。*

## 功能介绍

### 多标签、分屏与路径导航

- 用多个标签页打开不同位置，每个标签独立保留导航历史、排序与视图。
- 支持横向、纵向、三窗格、四窗格及主次窗格布局，整理文件时可同时查看来源与目标目录。
- 通过面包屑、可搜索的子目录菜单或直接输入路径快速跳转，配合前进、后退与上一级导航。
- 从侧栏访问置顶目录、最近位置、本地磁盘与废纸篓。

### 灵活的文件视图

- 切换列表、图标网格与树形列表，在树形列表中直接展开本地文件夹。
- 按类型、日期、大小等属性排序或分组，选择文件优先、文件夹优先或不区分。
- 组合列筛选条件缩小范围；同一列的多选条件匹配任意一项，不同列的条件同时生效。
- 显示图片缩略图，可用文件夹内照片生成封面；系统无法解码部分受支持的相机 RAW 文件时，尝试备用缩略图方案。
- 在信息面板查看路径、大小、日期、图片尺寸、相机等照片元数据，并计算、复制本地文件的 SHA-256 哈希。

### 首页、收藏夹、标签与评分

- 首页集中展示最近使用的文件和文件夹、常用位置、收藏夹，以及主目录、桌面和磁盘快捷入口。
- 收藏夹以可调整大小的网格展示，内容较多时可展开浏览。
- 添加 Finder 标签，并从侧栏集中浏览带标签的文件。添加标签或收藏不移动原文件，移除标签或收藏不删除原文件。
- 为文件设置星级评分，辅助整理和查找重要资料。

### 搜索与本机图片分析

- 在当前工作区搜索，或按 `⌘ K` 打开全局搜索；选中结果后在右侧预览，也可直接定位到所在文件夹。
- 搜索已索引的文件名、已有 PDF 正文、图片 OCR 文字及分析结果。
- 使用设备上的图片分析，按人物、识别文字、场景分类、拍摄日期和地点浏览照片，并为人物分组命名。
- 浏览热门识别文字及包含这些文字的文件；照片地点名称解析可选开启，可能使用在线服务。
- 在设置中控制自动分析和搜索范围，内容搜索取决于已有索引或分析结果。

### 不离开窗口的预览与编辑

- 选中文件或文件夹，按 `Space` 在当前窗口预览。
- 预览图片、PDF、文本及受支持的文档和媒体；浏览文件夹与压缩包内容，继续进入子目录或嵌套压缩包。
- 查看 Markdown 的排版效果，或使用内置编辑器修改内容并查看渲染结果。
- 预览范围取决于文件格式与 macOS Quick Look 支持，RAW 预览也受相机格式限制。

### 文件操作与批量重命名

- 新建文件与文件夹、复制、移动、重命名、拖放、选择打开方式、复制路径及移到废纸篓；支持撤销部分文件操作。
- 在后台任务面板查看耗时操作的进度，对支持的任务执行取消。
- 使用批量重命名工作台处理跨目录选中的本地文件和文件夹，组合替换、正则表达式、插入、删除、大小写、清理、编号、日期、模板和扩展名规则。
- 调整规则顺序、保存预设、查看各步骤结果、对比新旧名称、排除个别项目或手动修改目标名称。
- 执行前检查冲突和处理重名，执行后查看结果，并整批撤销已完成的重命名。

### 压缩包与文件转换

- 浏览和解压 ZIP、TAR、7Z 等受支持格式，解压到当前目录或独立文件夹。
- 创建 ZIP、TAR.GZ、TAR.BZ2 压缩包，支持带密码的 ZIP。
- 将 Markdown 与受支持的文本文件转换为 Word 或 PDF，将 Word 文档转换为 PDF。
- 将 SVG、ICO、ICNS、WebP 转换为 PNG 或 JPG，并设置图片尺寸；可用转换选项随选中文件变化。

### SFTP 远程文件与 Git 状态

- 保存 SFTP 连接，使用密码或私钥认证，建立信任时核对服务器指纹。
- 浏览和管理远程文件、上传和下载，通过本地应用编辑远程文件后自动回传修改。
- 存在可用的 Git 安装和仓库时，在文件旁显示 Git 状态。

### LocalSend 文件收发

- 从右键菜单将选中的本地文件和文件夹发送给发现的 LocalSend 设备。
- 接收前核对发送设备与文件清单，并选择保存位置。
- 设置设备名称、接收目录和额外发现网段；自动发现不可用时可通过 IP 直接连接。
- 在任务面板查看发送、接收进度；设备发现需要网络可达。

### 菜单栏文件速递

- 从 macOS 菜单栏打开紧凑面板，通过下载、桌面及自定义目录或收藏夹页签取用文件。
- 使用列表或图标视图浏览、预览文件，并将文件复制拖到其他应用或位置。
- 每个入口记住浏览位置；主窗口关闭后仍可使用，直到退出应用。
- 删除面板入口不会删除原目录或收藏夹。

### Copilot 文件助手

- 用自然语言查找文件、查看元数据、整理选区、准备重命名规则、转换文件，或通过应用提供的能力总结已授权内容。
- 组合已有的名称、PDF／OCR、相机、地点、日期、人物、标签、评分与文件属性条件查找候选文件。
- 为对话附加文件或文件夹路径，回看本地会话历史，并使用内置或自定义技能处理重复流程。
- 修改文件或向模型发送文件正文前先查看并确认计划；重命名方案可交给工作台继续调整。

在 **设置 → Copilot** 中填写兼容 OpenAI 的 API 地址、模型名与 API Key，再从主窗口打开 Copilot。模型服务由用户自行配置，使用规则与费用以对应服务商为准。

### 外观、快捷键与扩展

- 支持中文与英文、浅色与深色主题、玻璃表面，以及标准、紧凑、舒适三种排版密度；可调整交互颜色。
- 自定义快捷键、检查冲突、恢复默认，长按 `⌘` 查看当前可用的快捷键提示。
- 官网版可为首页脚本配置具名命令、图标、Shell 与工作目录，并通过终端运行。
- 官网版支持通过插件市场或本地 `.mexplug` 包安装、管理文件处理扩展；开发者可使用 [插件 SDK](docs/plugins.md) 和 [开发者中心](https://3egirlsdream.github.io/Mac-Explorer/developers/) 开发并发布插件。

## 安装与版本差异

支持 **macOS 14 或更新版本**，适用于 **Apple Silicon 与 Intel**。下载包已包含运行环境，无需另外安装 .NET。

1. 打开 [最新发布页](https://github.com/3egirlsdream/Mac-Explorer/releases/latest)。
2. 根据 Mac 选择 Apple Silicon DMG（`macos`）或 Intel DMG（`macos-intel`）；发布页也会在提供时列出 ZIP 包。
3. 打开 DMG，将 **Mac Explorer** 拖入 **Applications（应用程序）**，然后启动。

本文介绍当前源码包含的功能，已安装版本的具体功能以 [更新日志](CHANGELOG.md) 与对应发布说明为准。

| 范围 | 官网版 | App Store 版 |
| --- | --- | --- |
| 文件访问 | 使用 macOS 文件权限 | 通过系统选择器授权目录，并在设置中管理授权 |
| 应用更新 | 应用内检查与下载更新 | 通过 App Store 更新 |
| 扩展与脚本 | 插件市场、外部插件、脚本与终端操作 | 保留内置文件转换，不提供外部插件、任意脚本与终端操作 |
| Git 状态 | 使用可用的系统 Git | Git 与仓库在授权范围内可访问时启用，部分仓库配置不支持 |

## 隐私与控制

文件索引、标签、评分、设置、分析结果及 Copilot 历史保存在本机，图片识别在设备上完成。可选的照片地点解析、用户配置的 AI 服务、SFTP 连接及 LocalSend 传输分别使用对应服务或选定设备。

Copilot 向 AI 分享文件信息前确认接收方，发送文件正文或修改文件前需要批准。保存的 Copilot API Key、SFTP 密码与私钥口令存储在本机应用数据库中，未额外加密。详细数据用途与控制入口见 [隐私政策](https://3egirlsdream.github.io/Mac-Explorer/privacy/)。

## 默认快捷键

| 快捷键 | 操作 |
| --- | --- |
| `⌘ T` / `⌘ W` | 新建／关闭标签页 |
| `Control Tab` / `Control Shift Tab` | 下一个／上一个标签页 |
| `⌘ L` | 聚焦路径输入 |
| `⌘ F` | 切换当前工作区搜索 |
| `⌘ K` / `⌘ Shift F` | 打开全局搜索 |
| `Space` | 预览选中的文件或文件夹 |
| `Esc` | 关闭预览或弹出界面 |
| `⌘ Z` | 撤销支持的操作 |
| 长按 `⌘` | 显示快捷键提示（需开启） |

可在 **设置 → 快捷键** 中调整支持自定义的命令。

## 开发与反馈

构建、打包、签名和公证请参阅 [构建文档](doc/BUILD.md)，隔离应用测试请参阅 [测试说明](AGENTS.md#自动测试)。遇到问题或希望增加功能，可在 [GitHub Issues](https://github.com/3egirlsdream/Mac-Explorer/issues) 提交反馈，并附上 macOS 版本、应用版本及复现步骤。

## 许可证

Mac Explorer 采用 [GNU GPL v3.0 或更新版本](LICENSE)。第三方许可声明见 [ThirdParty/Notices](ThirdParty/Notices/README.md)。
