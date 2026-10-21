# GitHub Pages 官网

正式地址：<https://3egirlsdream.github.io/Mac-Explorer/>

Pages 继续使用 `main` 分支的 `/docs` 目录。页面是纯静态 HTML、CSS 和 JavaScript，部署无需 Node.js、Python 或构建步骤。新版采用亮色细网格背景，首屏使用产品名称、简短定位、下载按钮和完整的文件列表及右键菜单界面；下滑后按四个分区介绍 16 项特色功能。底部使用居中介绍与胶囊入口，链接至 GitHub、插件 SDK、插件发布和问题反馈。

## 内容与生成

- `Tools/Website/build.py`：共享页面模板、真实界面的 HTML 复刻和元数据生成；只使用 Python 标准库。
- `docs/locales/{zh-CN,zh-Hant,en,fr}.json`：简体中文、繁体中文、英文、法语文案；所有语言必须包含相同的键。
- `docs/index.html`、`docs/zh-Hant/index.html`、`docs/en/index.html`、`docs/fr/index.html`：提交到仓库的完整静态页面，不依赖运行时翻译，爬虫与关闭 JavaScript 的浏览器都可读取。
- `docs/landing.css` / `docs/script.js`：首页样式、按视口剩余高度等比缩放、语言与芯片下载菜单键盘操作。网站始终为亮色。
- `docs/Assets/showcase/`：1.0.60 最新隔离应用截图中的文件图标；窗口、侧栏、标签、布局与文字全部由 HTML/CSS 组成。
- `docs/Assets/social-preview.png`：1200 × 630 分享图片，从最终页面导出。
- `docs/sitemap.xml`：四个语言主页、隐私政策与开发者中心的入口。
- `docs/.nojekyll`：直接发布静态文件。

修改模板或语言文件后运行并提交生成结果：

```sh
python3 Tools/Website/build.py
python3 Tools/Website/build.py --check
```

已有 `styles.css` 留给隐私政策页使用；旧 `scenarios.css` / `scenarios.js` 不再由首页加载。隐私政策与开发者中心保留现有路由。

## 本地预览与验证

```sh
python3 -m http.server 4317 --bind 127.0.0.1 --directory docs
```

访问 <http://127.0.0.1:4317/>。通过 HTTP 预览，保证本地 SVG 图标集正常加载。正文中的拉丁字母和数字使用本地托管的 Montserrat 可变字体（100–900 字重），中文回退系统字体；软件界面展示保留系统字体。字体及 OFL 许可位于 `docs/Assets/fonts/`，采用 `font-display: swap` 并预加载 Latin 字体。图标来自 Microsoft Fluent System Icons，无运行时外部字体、CDN 或分析请求。

复用已有 Playwright 和 axe-core，或将测试工具安装至临时目录：

```sh
npm install --prefix /tmp/mac-explorer-pages-tools playwright axe-core
/tmp/mac-explorer-pages-tools/node_modules/.bin/playwright install chromium
NODE_PATH=/tmp/mac-explorer-pages-tools/node_modules \
  node Tools/Testing/verify-pages.mjs http://127.0.0.1:4317/ /tmp/mac-explorer-pages-qa
```

验证覆盖四种语言 × 1440、1366、1280、768、390、320px 宽度，包含 900、768、720px 桌面高度，系统深色偏好下保持亮色、页面无横向溢出、首屏完整显示与等比缩放、16 项功能介绍、FAQ、语言和下载菜单与 Escape 焦点、链接与 Fluent 图标、SEO 元数据与结构化数据、站点地图，以及无 JavaScript 内容和下载入口。报告与截图写入指定输出目录。

无障碍审计检查网站文案与操作控件。文件列表及右键菜单画面按带本地化说明的 `role="img"` 提供，在颜色对比度审计中作为图像展示排除；这不代表桌面应用原界面的所有像素都满足 WCAG。减少动态效果偏好会关闭过渡与平滑滚动。

## 界面来源与演示边界

参考画面为 2026-10-21 重新构建的 1.0.60 文件列表，原始尺寸 1280 × 800。列表按截图等比重现，右键菜单复用当前源码的菜单结构、快捷操作栏及主题规则。素材出处见 `docs/Assets/showcase/README.md`。

页面正文只介绍产品功能，不显示 HTML、复刻、示例等实现说明。演示不读取访问者磁盘或执行实际文件操作。所有下载入口指向官方 `releases/latest`，芯片选项进入发布页后选择对应安装包。

## SEO 与上线

- 中英文标题和正文明确说明产品名称 **Mac Explorer** 及 **Mac 文件资源管理器 / Mac file manager**；简体页面的软件别名包含无空格的“Mac文件资源管理器”。不堆叠隐藏关键词。
- 每种语言都有独立、自指向的 canonical，以及互相对应的四种语言 `hreflang` 与 `x-default`。
- `WebSite`、`WebPage`、`SoftwareApplication` JSON-LD 描述网站及官网免费版；App Store 版的订阅差异在 FAQ 明确说明。不编造评价、评分或下载量。
- 生成 Open Graph / Twitter 分享信息、图像说明与尺寸，正文是可直接抓取的 HTML。
- 项目站位于 `/Mac-Explorer/`。有效的 robots.txt 必须位于域名根路径 `/robots.txt`，本仓库内的 `/Mac-Explorer/robots.txt` 不会控制抓取，因此不添加无效的项目级 robots 文件。页面通过 robots meta 允许索引。

上线顺序：

1. 运行生成一致性检查和网页验证，提交相关文件并推送 `main`。
2. 确认 GitHub Pages 构建成功，再检查正式地址、四种语言路由和 sitemap 返回 200。
3. 在已验证所有权的 Google Search Console 中添加 `https://3egirlsdream.github.io/Mac-Explorer/` URL 前缀资源，提交 `https://3egirlsdream.github.io/Mac-Explorer/sitemap.xml`，对主页执行 URL 检查并请求编入索引。使用 Bing Webmaster Tools 时提交同一 sitemap。
4. 后续查看“mac explorer”“Mac文件资源管理器”的展现与收录情况。SEO 改善抓取和理解，不能保证某个时间收录或具体排名。

[Google SEO 入门指南](https://developers.google.com/search/docs/fundamentals/seo-starter-guide) · [多语言页面说明](https://developers.google.com/search/docs/specialty/international/localized-versions) · [robots.txt 放置规则](https://developers.google.com/search/docs/crawling-indexing/robots/intro)

本地修改、推送成功、Pages 发布成功和搜索引擎收录是四个独立状态，交付时分别说明。

## 重新生成分享图

```sh
NODE_PATH=/tmp/mac-explorer-pages-tools/node_modules \
  node Tools/Testing/render-pages-social.mjs http://127.0.0.1:4317/ docs/Assets/social-preview.png
```

工具复用首页品牌、标题和文件列表与右键菜单画面，不运行桌面应用或读取用户文件。
