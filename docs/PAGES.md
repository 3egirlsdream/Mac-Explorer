# GitHub Pages 官网

官网地址：https://3egirlsdream.github.io/Mac-Explorer/

GitHub Pages 使用 `main` 分支的 `/docs` 目录发布。页面是静态 HTML、CSS 和 JavaScript，无需安装依赖或执行构建。

## 本地预览

在仓库根目录运行：

```sh
python3 -m http.server 4317 --bind 127.0.0.1 --directory docs
```

访问 `http://127.0.0.1:4317/`。请通过 HTTP 预览，确保本地 SVG 图标集正常加载。

## 文件

- `index.html`：页面内容、静态演示结构与分享信息。
- `styles.css`：明暗主题、响应式布局与交互样式。
- `script.js`：演示视图、示例文件搜索、超级预览、布局选择和外观切换。
- `Assets/icons/`：本地 Fluent System Icons 图标集、来源说明及 MIT 许可证。
- `Assets/alpine-lake.jpg`：沿用项目演示素材的压缩配图。
- `Assets/social-preview.png`：1200 × 630 分享预览图。

交互演示使用示例文件，不读取访问者的磁盘，也不是应用实机截图。功能介绍基于当前应用实现；下载按钮始终指向 GitHub `releases/latest`，避免将源码版本误写为已发布版本。

## 更新内容时核对

- 多窗格：`ViewModels/MainWindowViewModel.cs`。
- 高性能列表：`Views/FileListView.FastList.cs`、`docs/FAST_FILE_LIST_IMPLEMENTATION_2026-09-08.md`。
- 超级预览：`Views/MainWindow.axaml.cs`、`CHANGELOG.md`。
- 搜索：`Platforms/MacCatalyst/Services/MacSearchService.cs`、`Services/Impl/SearchOmniboxProvider.cs`。不要将文件名和图片文字检索扩大描述为任意文档全文或拼音搜索。
- 视觉：`Assets/ThemeTokens.axaml`、`Assets/TypographyTokens.axaml`。

本次改版保留现有发布设置。推送官网文件到 `main` 后，现有 GitHub Pages 流程才会更新线上页面。
