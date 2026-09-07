# UI 视觉改造实施与验收记录

日期：2026-09-07。对应方案：[UI_VISUAL_REDESIGN_AI_PLAN_2026-09-07.md](/Users/jiangxinji/Documents/FKFinder/docs/UI_VISUAL_REDESIGN_AI_PLAN_2026-09-07.md)。

本地实现已完成，最终构建通过，251 项自动回归全部通过。主要页面已在 macOS 实机检查；原生预览的完整多页／播放交互、实际上下文菜单及部分人工操作仍未完成验收，详见下文。本次没有升级版本、提交、推送或生成发布安装包。

## 1. 本次变更

| 范围 | 最终行为 |
|---|---|
| 主题与焦点 | 浅色／深色使用方案规定的中性颜色；保留用户自定义交互配置及旧配置迁移。Selected 不再覆盖 FocusRing，焦点使用独立轮廓。 |
| 按钮与输入 | 主要／危险按钮的 hover、pressed 颜色落实到 Fluent 的实际 ContentPresenter；普通工具按钮禁用时保持透明背景。焦点不改变布局尺寸。 |
| 主工作区 | 侧栏、工具栏、内容区改为连续表面；灰色侧栏恢复四角圆角及内容裁切；普通侧栏 240，紧凑侧栏 220／48 与原断点保留。标题栏只调整配色，保留原有页签曲线与命中实现。 |
| 首页 | 隐藏文件工具栏、页面搜索和“0 项”状态；顶部导航整行隐藏且不保留占位，中央保留一个主输入。⌘L 和页面搜索快捷键在首页都聚焦中央输入。 |
| 紧凑与多窗格 | 解除地址栏本地 MinWidth 对紧凑样式的覆盖。展开搜索使用导航按钮以外的剩余空间；400 DIP 的单个窄窗格内搜索与导航均不越界。 |
| 工具栏 | 文件动作在左、查看动作在右；溢出菜单保留原操作并补齐可访问名称。剪切／复制／删除与选择数量同步，粘贴检查已有剪贴板服务；控件重新挂载时恢复订阅。 |
| 侧栏 | 常用位置、收藏夹、位置、AI、标签层级更清楚；操作图标统一为中性色，活动项保留 Accent。收藏创建入口常显，分组与远程项目支持现有行为的键盘触发。 |
| 文件呈现 | 列表正文 13、网格名称 12；元数据与表头统一，大小列及表头一起右对齐。保留标准行高 30、网格列宽 120、既有快照和缩略图管线。 |
| 信息面板 | 文件名与类型／大小摘要置于字段之前；无选择时隐藏空字段；操作按钮与标签具有明确名称，标签点 14、目标区域 24。预览失败显示真实原因及重试／默认应用入口。 |
| 状态呈现 | 区分空目录、无搜索结果、已知读取失败、搜索失败和未连接。搜索展示查询条件及实际范围，清除搜索同时清除旧搜索状态。重试复用原缩略图失效与预览请求路径。 |
| 外围界面 | 设置导航／字段、远程连接、删除确认、AI 空态及超级预览按钮保持同一视觉体系；远程主机必填提示紧邻输入框并返回焦点。其他弹窗继承共享按钮和输入样式。 |

## 2. 原有工作区改动与本次改动的边界

起点为 main，HEAD f6bdecf，版本 1.0.34。开始时已存在已暂存的 Chromium 页签、列表快照／数据管线、图标描述、缩略图与预览等改动；这些不是本次成果。本次在它们之上修改视觉消费点和少量状态／焦点路由，没有重写这些管线。

原始 Git 状态及补丁保存在 [baseline 目录](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/baseline)。交付前再次比较，暂存区补丁 SHA-256 仍为 362b8d6df1904d82df60bed9088e80907fb452085cb46d233aa8a3690ab65bde，与开始时完全一致。本次改动保持未暂存。

为使现有回归与已暂存的列表实现一致，另外调整了两处原有测试：异步文件操作测试使用 Avalonia 调度上下文；图标通知断言包含已存在的 GridIconSource 通知。这两处问题已用改造前的程序集复现，证据见 baseline/performance-test.log 与 baseline/create-test.log。没有为通过测试修改列表快照实现。

## 3. 构建与自动回归

| 检查 | 结果与证据 |
|---|---|
| 应用本地构建 | 通过，0 错误。独立输出 [VisualRedesignLocal](/Users/jiangxinji/Documents/FKFinder/bin/VisualRedesignLocal)，使用正常入口，无 QA 启动替身。日志：[production-build.log](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/production-build.log)。 |
| 测试程序集构建 | 通过。使用独立输出，避免覆盖其他运行实例。 |
| 全量 xUnit 回归 | 251 项，失败 0，跳过 0。结果：[tests-final.xml](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/tests-final.xml)，[完整日志](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/tests-final.log)。 |
| 差异格式 | git diff --check 通过。 |

新增行为测试覆盖：首页单输入焦点和目录工具栏恢复、400 DIP 窄窗格导航／搜索边界、读取失败与空态区别、退出搜索后的状态清理、工具栏在重新挂载后的选择状态同步，以及明暗主题下主要／危险按钮实际模板的 hover／pressed 颜色和焦点尺寸稳定性。原有字体配置、交互配置迁移、ComboBox、响应式布局、选择／框选、列宽、刷新锚点、缩略图和预览回归也均通过。

本机的 dotnet test 与 Microsoft.Testing.Platform 集成入口停留在子进程等待，因此最终使用构建生成的 xUnit 可执行程序运行全量测试；没有把集成入口的等待误记为通过。可复现命令：

~~~sh
dotnet build MacExplorer.csproj --no-restore -p:OutputPath=bin/VisualRedesignLocal/
dotnet build Tests/MacExplorer.Tests/MacExplorer.Tests.csproj --no-restore -p:OutputPath=bin/VisualReviewTests4/
Tests/MacExplorer.Tests/bin/VisualReviewTests4/MacExplorer.Tests -noColor -reporter verbose
git diff --check
~~~

应用构建仍有 13 条现有警告，包括依赖安全公告、Swift 弃用提示和现有 ViewModel 字段用法。此次没有修改依赖版本，也没有宣称构建无警告或性能有所提升。

## 4. 实机验收范围

| 场景 | 实际结果 |
|---|---|
| 首页浅／深，1280×800 | 已检查：顶部导航整行隐藏、一个中央输入、无重复文件操作或“0 项”；⌘L 聚焦中央输入。 |
| 列表与网格浅／深，1280×800 | 已检查中文、英文、长文件名、选中状态和缩略图显示；完整名称可通过提示及可访问名称取得。 |
| 信息面板 | 已检查无选择、PNG 选择、字段与操作区域滚动；损坏 PNG 显示失败与重试入口，更换为可读文件后原有文件监听使预览恢复。此恢复没有被误记为重试按钮触发的结果。 |
| 1000×680 紧凑窗口 | 已检查导航、页面搜索、溢出菜单；输入无结果查询后显示查询与范围，清除后恢复目录的 9 项状态。Tab／Shift+Tab 能回到搜索输入。 |
| 1600×800 多窗格 | 已检查左右双窗格、四列最密横向布局；四列中导航、刷新和搜索入口均可见，展开搜索保持在当前窗格内。 |
| 页签 | 已检查单页签、多页签、创建至末尾滚动、⌘W 关闭与相邻页签切换；曲线衔接无明显白缝。拖拽命中另有已有自动测试，未完成实际鼠标拖拽验收。 |
| 字体预设 | Small／Standard／Large 在实机设置中切换；检查了 1600×800 的列表和侧栏，未见裁切。最终 QA 恢复 Standard。未覆盖每种字号与所有窗口组合。 |
| 输入与弹层 | 设置 ComboBox 可选择并实时生效；远程表单空主机点击连接只显示必填提示，未连接服务器；Esc 可关闭表单和超级预览。 |
| 原生相关预览 | PDF 首页、DOCX 文档缩略预览、HTML／Markdown 文本分支、视频预览入口已检查；系统 Quick Look 的独立进程启动已确认。完整分页、WebKit 渲染和 AVPlayer 播放不能据此判为通过。 |

### 截图与环境

截图全部来自实际 macOS QA 应用，没有生成概念图。QA 使用独立数据库；MACEXPLORER_DB_PATH 本身并不隔离已有远程服务器 JSON，因此仅在仓库外的 QA 入口中清空启动时的内存服务器列表，未写入或删除真实服务器配置。此替身不参与正常应用构建，也未改变 AI 开关、索引规则或连接凭据。

RenderScaling 在本机运行日志中为 1。CUA 返回的截图会缩小到工具展示尺寸，因此 1280×800 DIP 的 PNG 通常为 1229×768；1600×800 的 PNG 为 1536×768，不应把 PNG 像素宽度当成窗口 DIP。字号和主题按文件名标注，除 Small／Large 两组外均为 Standard；采用默认交互颜色，已有位置显示名称及挂载卷名称保持原样。

首次实施的基础前后对比（后续三处反馈的更新截图见第 6 节）：

| 场景 | 改造前 | 改造后 |
|---|---|---|
| 浅色首页 | [前](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/baseline/home-light.png) | [后](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/after/home-light.png) |
| 深色首页 | [前](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/baseline/home-dark.png) | [后](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/after/home-dark.png) |
| 浅色列表 | [前](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/baseline/list-light.png) | [后](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/after/list-light.png) |
| 深色列表 | [前](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/baseline/list-dark.png) | [后](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/after/list-dark.png) |
| 浅色网格 | [前](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/baseline/grid-light.png) | [后](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/after/grid-light.png) |
| 深色网格 | [前](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/baseline/grid-dark.png) | [后](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/after/grid-dark.png) |
| 信息面板 | [前](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/baseline/info-light.png) | [后](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/after/info-light.png) |

其他实机证据：[紧凑窗口](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/after/compact-1000-light.png)、[四列布局](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/after/four-panes-1600-dark.png)、[窄窗格搜索](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/after/four-panes-search-dark.png)、[预览失败](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/after/preview-failure-dark.png)、[远程表单](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/after/remote-dialog-dark.png)、[截图清单](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/screenshot-manifest.json)。

## 5. 尚未完成的验收与限制

- Quick Look 独立窗口的多页滚动、完整 Office 分页、WebKit 页面交互、AVPlayer 播放和进度拖动未取得充分实机证据；当前截图不能替代这些验收。远程本地化仅由现有测试替身覆盖，未连接真实服务器。
- 两次 CUA 右键操作只取得文件选择状态，没有得到可确认的弹出菜单画面。当前文件菜单源码走 Avalonia ContextMenu，主题修改已落在该模板及 Popup 样式；实际菜单 hover、子菜单和滚动仍待人工检查。
- 行高、网格列宽、框选、列宽拖动、刷新锚点等自动回归通过；本次未完成完整实机长列表滚动、列拖拽、文件拖放、页签拖拽及批量重命名／压缩操作验收。
- 工具栏禁用状态使用现有选择和剪贴板能力；没有新增远程写权限推断。某些远程失败分支只有旧状态字符串，不能据此分类成权限、网络或认证错误。
- 普通读取失败展示已增加最小状态并通过界面测试，尚未做真实文件权限故障注入。VoiceOver 完整朗读与系统 Reduce Motion 未验证；没有新增动态效果或宣称已适配这些系统能力。

方案第 10 节中未勾选的综合验收项对应上述范围；本地实现不等于全部人工验收已完成。

## 6. 后续反馈修正（2026-09-07）

- 左侧灰色区域恢复 10 DIP 四角圆角，并裁切内部滚动内容。
- 右上角窗格布局和设置统一为 30×30 按钮、16×16 图标；后台任务图标同步尺寸。
- 首页隐藏顶部整行导航，不再显示后退、前进、刷新、首页定位文字，也不留下 40 DIP 空白。目录页导航与首页中央输入快捷键保留。
- 本地构建通过，251 项测试全部通过；首页导航行在首页高度为 0、进入目录恢复为 40 的行为已加入现有测试。浅色实机已核对圆角、图标尺寸、导航隐藏／恢复，以及 ⌘L、⌘F 的首页焦点。
- [修正后的首页截图](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/feedback/home-light.png)，[进入目录后的截图](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/feedback/list-light.png)，[验证记录](/Users/jiangxinji/Documents/FKFinder/artifacts/ui-visual-review-2026-09-07/feedback)。
