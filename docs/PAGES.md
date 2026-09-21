# GitHub Pages 官网

官网地址：https://3egirlsdream.github.io/Mac-Explorer/

GitHub Pages 使用 `main` 分支的 `/docs` 目录发布。官网以完整产品能力为主线，按浏览、整理、查找、查看与处理、连接与取用、扩展与个性化组织内容。页面为静态 HTML、CSS 和 JavaScript，无需安装运行时依赖或执行构建。

## 本地预览

在仓库根目录运行：

```sh
python3 -m http.server 4317 --bind 127.0.0.1 --directory docs
```

访问 `http://127.0.0.1:4317/`。请通过 HTTP 预览，确保本地 SVG 图标集正常加载。

## 页面与素材

- `index.html`：完整功能介绍、可直接阅读的示例初始画面、导航及分享元数据。
- `styles.css`：基础主题、首屏文件工作台、多窗格布局、预览和下载样式。
- `script.js`：主题与移动导航、首屏列表／网格／双窗格、文件名与类型组合过滤、示例预览。
- `scenarios.css` / `scenarios.js`：收藏卡片、分类搜索、Markdown 示例、文件速递与 SFTP 分步演示、脚本菜单；按独立场景管理状态。
- `Assets/icons/`：本地 Fluent System Icons 图标集、来源说明及 MIT 许可证。
- `Assets/alpine-lake.jpg`：项目演示素材，贯穿页面各个工作场景。
- `Assets/social-preview.png`：1200 × 630 分享图，从最终页面的产品窗口和文案构图导出。

## 演示边界

演示只使用预置文件，不读取访问者磁盘，不运行脚本、不建立 SFTP 连接、不执行实际转换或文件传输；浏览器不是桌面应用。Markdown 示例仅渲染标题、段落和列表，输入始终作为文本插入，不解析 HTML，也不保存。

- 收藏卡片支持切换、拖动／方向键／按钮调整大小、展开及分页。两个收藏中出现的同名照片表示同一个文件的多个标签归属。
- 文件名、图片／PDF 文字、图片分类使用各自的示例集合。应用中的图片和 PDF 文字需先开启分析并浏览文件夹建立索引，不应扩大描述为任意文档全文搜索。
- 文件速递与远程流程支持播放、暂停、重播；离开可见区域、切换场景或后台标签页时停止计时。减少动态效果模式通过按钮逐步展示。
- 首屏文件与收藏文件可以打开示例预览；弹层支持 Esc 关闭并回归触发控件。
- JavaScript 不可用时保留产品文案、静态场景和下载链接。主题偏好保存在浏览器本地，存储不可用时仍可使用当前页面。

## 功能介绍核对

- 多窗格与标签：`ViewModels/MainWindowViewModel.cs`。
- 文件列表：`Views/FileListView.FastList.cs`；表头筛选、分组和面包屑按真实功能介绍，不展示性能比较倍率。
- 首页收藏与脚本：`docs/HOME_WORKSPACE_IMPLEMENTATION.md`。收藏不移动文件；首页脚本命令交给默认终端，不等于后台任务管理。
- 文件速递：`docs/FILE_DELIVERY.md`。支持本地目录与收藏，只复制拖出；不声称文件同步、远程目录入口或浏览器上传实测兼容性。
- 搜索与分析：`Platforms/MacCatalyst/Services/MacSearchService.cs`、`ViewModels/AiViewModel.cs`、`Services/Impl/PdfAnalysisService.cs`。区分文件名、图片文字、PDF 文字与图片分类；地点名称解析可能联网。
- 预览与编辑：`Views/SuperPreviewView.axaml.cs`、`Views/MarkdownEditorView.axaml.cs`。系统格式支持取决于可用预览组件。
- 转换：`Plugins/FileConversion/plugin.json`。只描述实际支持的输入／输出组合，避免暗示所有图片格式双向互转。
- 远程文件：`Services/SftpFileService.cs`、`Services/Impl/RemoteFileEditService.cs`。远程协议明确为 SFTP。
- 插件文档使用 `developers/index.html` 和 `developers/sdk.html`，由开发者中心承接详细安装、开发与发布步骤。

## 自动验证

复用机器上已有的 Playwright 和 axe-core，或安装到临时目录，不向官网添加运行时依赖：

```sh
npm install --prefix /tmp/mac-explorer-pages-tools playwright axe-core
/tmp/mac-explorer-pages-tools/node_modules/.bin/playwright install chromium
NODE_PATH=/tmp/mac-explorer-pages-tools/node_modules \
  node Tools/Testing/verify-pages.mjs http://127.0.0.1:4317/ /tmp/mac-explorer-pages-qa
```

脚本依赖已运行的本地 HTTP 服务。验证 1440、768、390、320px 宽度及深浅主题、WCAG A/AA 检查、链接／图标、组合筛选、空状态、预览及焦点、收藏分页和缩放、三类搜索、Markdown 文本插入、流程暂停与减少动态效果、主题持久化、无 JavaScript 的基本内容。截图、无障碍审计及检查结果写入指定输出目录，不提交测试产物。

## 重新生成分享图

沿用上面安装的 Playwright 和已运行的 HTTP 服务：

```sh
NODE_PATH=/tmp/mac-explorer-pages-tools/node_modules \
  node Tools/Testing/render-pages-social.mjs http://127.0.0.1:4317/ docs/Assets/social-preview.png
```

导出工具复用当前首页的标题、品牌和文件工作台，固定浅色主题与 1200 × 630 尺寸，不执行应用或读取用户文件。

## 发布检查

下载按钮始终指向 GitHub `releases/latest`，不将源码版本当作已发布版本。发布官网前核对正式安装包已包含页面描述的功能；若尚未发布对应应用版本，先完成应用发布，再推送官网。

本次实现不更改 Pages 发布设置。提交到本地、推送 `main`、Pages 构建成功和线上内容确认是四个独立交付状态，分别核实。
