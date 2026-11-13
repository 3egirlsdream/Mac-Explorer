# Commit 风格

- 标题简短概括修改；正文按 `1. `、`2. `、`3. ` 连续编号，用中文更新日志风格描述实际变化。
- 编号正文紧接标题后的空行，条目之间不空行，结束后再空一行；验证等补充说明放在其后，无补充时可直接结束提交。

```text
feat: 优化文件夹恢复

1. 新增页签恢复设置。
2. 修复恢复后选中位置错误。

验证：相关测试通过。
```

# 图标
- 统一使用，自动寻找合适图标： https://github.com/microsoft/fluentui-system-icons
- 右键菜单，不必每项都带图标，不合适的可以不带，只有明确语义化的才带图标
- 页签图标绑定 `FileListViewModel.CurrentLocationIcon`，随导航更新，优先复用侧栏对应位置的 Fluent 图标，没有专属图标时回退默认文件夹。
- 文件类型图标由 `FileKindNames` 与 `FileIconResolver` 统一选择，列表和首页共用 `FileIconRenderer` / `SvgIconCache`。Office、PDF 等固定图标按类型和尺寸复用，音视频、工程、归档等按短后缀绘制徽标。真实应用使用原生图标，已知工程包有类型图标并保留目录操作语义。

# 设计风格提示词：紧凑 macOS 设置

适用于配置、编辑和设置类对话框，以脚本命令编辑界面为参考。

> 参考 macOS 系统设置，设计克制、紧凑、清晰的苹果风格界面。标题栏透明，与窗口共用同一底层表面，不单独添加背景色或分割色带。只保留必要信息，用圆角分组、紧凑字段行和细分隔线组织内容，减少嵌套卡片、边框、装饰和大面积留白。优先复用项目全局组件与动态主题资源，兼顾浅色、深色和窄窗口。

- 标题只在标题栏出现一次，可附简短文件名；正文不重复标题、完整路径或大段操作说明。辅助说明放入悬停提示，必要的字段名称与错误信息仍应清晰可见。
- 每组内容按操作顺序排列：名称与图标、主要编辑内容、辅助配置。组内输入使用透明表面，不再为每个字段叠加独立背景；行间用主题细分隔线，分组间距约 8–12px，窗口外边距约 16px。
- 控件采用约 28–32px 的紧凑高度，保持文字基线与图标居中对齐；为图标、箭头及内边距分别预留空间，避免裁切。图标选择器收起时仅显示图标，下拉项同时显示图标与文字。
- 多行命令使用等宽字体、左上对齐和自动换行，设置合理的最小与最大高度，内容过长时在编辑框内滚动。
- 操作文案简短，使用“添加命令”“取消”“保存”；明确语义的局部操作可用 Fluent 图标，保留悬停提示、无障碍名称及键盘操作。主要操作置于右下角，添加操作置于左下角。
- 初始窗口高度随内容数量调整并设置上下限，少量内容不留大块空白，较多内容滚动显示，底部操作始终可见。
- 实现复用 `DialogWindow` / `AppWindow`、`SettingsStyles.axaml` 的分组与字段行，以及全局按钮、输入框和主题色；完成后检查浅色、深色、窄窗口、长文本和图标是否完整显示。

# 组件样式
- 分屏复用全局 `workspace-surface` / `workspace-content` / `workspace-shadow` / `workspace-inactive-overlay`：无边框，内容按 `RadiusLg` 裁切，激活显示零偏移轻阴影，深色额外使用柔和的 0.5px 外轮廓光；未激活以浅／深色淡灰蒙层淡化 30%。侧栏左侧内缩 4px，与玻璃表面上下间距一致；阴影独立于内容裁切，分屏内缩 12px 并结合主窗口 8px 外边距保留阴影空间，蒙层不拦截输入并排除玻璃采样。
- 商店高级功能通过 `FeatureAccessService` 按功能授权，仅在触发高级入口时使用 `AppWindow.SubscriptionHost` 与可关闭的 `SubscriptionView`，复用紧凑设置分组和全局按钮；应用体验与 Apple 权益独立管理，价格及实际订阅优惠来自 StoreKit。
- 官网版更新使用自有版本接口及下载安装流程；App Store 版通过 Apple Lookup 查询商店已发布版本，启动提示与关于页复用现有入口，点击“前往 App Store”打开应用详情页，由商店安装，不运行官网更新工作进程。
- 文件列表视图切换复用全局 `view-mode-segmented`；三项工具栏追加 `three` 样式和 `ViewModeTrackThreeGeometry`，保持统一高度、圆角及选中态。
- 文字搜索页使用紧凑间距；热门文字复用全局 `Button.tag-chip` 胶囊标签，`tag-label` 与 `tag-count` / `tag-count-text` 分别显示文字和文件数，内容双向居中，长标签省略并通过提示显示完整文字。
- 脚本命令编辑复用 `SettingsStyles.axaml` 的分组和紧凑字段行；组内输入使用 `settings-inline-input` 透明表面，命令使用 `settings-command-editor` 等宽多行编辑，分隔线使用 `settings-divider-row`。标题栏透明并与窗口表面一体，说明放入提示，不重复显示标题与完整路径。
- `ComboBox.icon-picker` 宽 64px、内边距 `8,0`，为 Fluent 模板的 32px 箭头列与 16px 图标保留完整空间。
- 首页脚本命令的右键菜单图标在名称左侧通过全局 `ComboBox.icon-picker` 选择，收起时仅显示图标，下拉项显示图标与文字，支持不配置／启动／中断，启动与中断复用 Fluent Play／Stop 图标及 `SuccessBrush`／`DangerBrush` 主题色，配置选项与菜单共用图标渲染；不配置在选择器中显示灰色横线，菜单不显示图标。多行编辑复用 `TextBox.multiline`，文字左上对齐并自动换行。
- 文件列表的文件名使用 `TextPrimaryBrush`；日期、大小、类型、数量及普通列标题／分组标题使用 `TextMutedBrush`，统一由 FastFileList 绘制，排序／筛选状态保留强调色。
- 文件列表「种类」与信息面板共用 `FileSystemEntry.KindText`，通过 `FileKindNames` 和四语资源显示格式全名，未知后缀显示「后缀 文件」，无后缀显示「文件」；普通目录、应用和已知项目包分别标识。种类列沿用全局列宽布局与单行省略，默认 230px，悬停显示完整名称。
- 自定义的组件全局通用，新增或者编辑代码都统一使用全局统一UI库组件，扫描到全局自定义组件样式以简洁语言自动归纳到本文件。
- 通用按钮使用 `primary`、`secondary`、`ghost`，紧凑操作追加 `compact`；对话框复用 `DialogWindow` / `AppWindow`。
- 有窗口关闭入口的预览、说明及只读弹窗不再单独添加底部“关闭”按钮或占位，正文铺满可用区域，保留标题栏关闭与 Escape；底部仅保留保存、取消、重试等实际操作，无操作时整组收起并移除分隔线与间距。
- 关于页产品信息使用透明的 `settings-about-card`，更新、隐私与反馈入口复用 `settings-group` / `settings-row` 和 `secondary compact`；反馈下方“给个好评”按渠道显示“跳转商店”或“github star”，分别打开 App Store 评分页或项目 GitHub 首页。反馈群文字与二维码并排，二维码通过 `CroppedBitmap` 展示原图码面与白色边距，不缩放整张海报。
- 隐私与主机信任确认复用 `ConfirmDialog` / `DialogWindow`、紧凑全局按钮与设置文字样式；标题栏透明，正文只显示数据接收方、用途或需要核对的指纹，默认聚焦取消。
- Copilot 对话与输入框复用 `ComponentStyles.axaml` 的 `copilot-*` 样式：助手气泡靠左、用户气泡靠右，过程用左侧细线，工具与确认用轻边框卡片；输入框和卡片使用全局 `RadiusLg`，发送按钮使用全局 `RadiusFull`。
- Copilot 同轮工具调用收在可展开的 `copilot-tool-card`，思考过程使用 `copilot-thinking-card` 和 Fluent Brain 图标；两者标题复用内容宽度的 `ghost compact copilot-tool-toggle`，展开明细沿左侧细线紧凑缩进，执行时展开，完成后自动收起。用户消息只显示右侧气泡，不显示“你”。
- Copilot 历史列表复用全局 `copilot-history-row` / `copilot-history-title` / `copilot-history-more` 样式；悬停、聚焦和当前会话统一使用主题选中色。默认收起，展开按钮使用 `copilot-history-toggle`，关闭按钮使用居中的 `copilot-close`。
- Copilot 附件区复用全局 `copilot-attachment-drop-zone` 和 `copilot-attachment-chip`；输入区与对话列表中的短附件标签统一使用 `RadiusSm` 和四边等距内边距，附件行与输入框留 4px 间距。右上角的红色圆形移除按钮仅在悬停或键盘聚焦时显示，悬停不变色。拖入文件或文件夹只记录路径，发送后清空暂存区，历史消息只显示只读附件标签。
- 紧凑设置中的文字选项复用全局 `segmented-control` 容器和 `segment-btn` 按钮，等宽居中排布，选中项追加 `active`，使用主题选中色而非主要操作按钮色。
- 设置页通过全局 `LazyTabItem.PageTemplate` 在首次进入时创建页面并缓存，首个页面在窗口首次布局前完成；插件、打开方式和快捷键按页面加载，语言切换仅刷新已创建页并保留草稿。设置页复用 `SettingsStyles.axaml` 的紧凑导航、分组和字段行，页面背景与工具栏统一使用 `TitleBarBackgroundBrush`；正文与说明分别使用主文字和次级文字资源。紧凑开关统一使用全局 `ToggleSwitch.compact`，轨道 36×20、滑块 16px，保留原生交互与焦点。
- 主窗口顶部标题／标签栏统一复用 `TitleBarBackgroundBrush`，浅色主题使用浅雾灰 `#F7F7F8`，与白色内容区柔和衔接；采用上述紧凑设置风格的对话框使用透明标题栏，与窗口表面一体。
- 文件工作区的首页、网络、AI 页面及右侧信息面板首次使用时创建并在当前页签内复用，隐藏时同步停止页面可见性相关工作；新窗口目标为目录时先准备目录视图，不创建随后会被替换的默认首页。
- 首页收藏夹与图标格样式集中在 `Assets/HomeStyles.axaml`，颜色复用全局动态主题资源；文件展示复用 `HomeFileImage` 与文件列表缩略图服务，异步按可见范围加载，无法预览时回退 `FileEntryToIconConverter`。
- 收藏夹最右下角 Fluent Resize 图标在悬停或聚焦时显示，可拖拽缩放，行列数显示在提示中，也可聚焦后用方向键调整；收藏关系统一经 `IFileTagService` 操作，不复制或移动原文件。

- 首页使用原有白色主题表面，侧栏保留自己的背景；卡片与启动台式居中展开页复用 `home-glass` 液态玻璃样式，内容层排除背景采样。收藏夹通过 `HomeFolderPanel` 自动填补缩放后的可用空位，并保留阴影外边距。
- 首页与展开页通过 `HomeThumbnailCache` 复用已解码缩略图；栅格未满不显示展开入口，溢出时才在末格显示文件类型组合图标。

- 信息面板使用单页分区；标题、字段行统一复用全局 `info-section-title` / `info-field-row` / `info-field-label` / `info-field-value`，字段值右对齐，行底部使用主题细分割线。

- 滚动条统一复用 `Assets/ScrollBarStyles.axaml` 与 `AppScrollBarTheme`，沿用 Fluent 原生交互、8px 尺寸、透明无描边轨道和圆角滑块；Markdown 等第三方预览在局部主题之后引用同一全局样式，避免替换模板。PDF／Office 的原生滚动条由 `DocumentPreviewHost` 传入同一组全局尺寸与浅深色资源；Office 网页预览铺满宿主，滚动条位于预览最右侧，不保留居中的纸张宽度滚动视口，也不增加右侧命中面板。

- 同名冲突、编辑同步和正文命中弹窗直接继承 `DialogWindow`，复用 `WindowTitleBar`、全局 `AppButton` / `AppSplitButton` 与 `SettingsStyles.axaml`，不增加中间弹窗基类。冲突和编辑同步保留紧凑分组、滚动正文与固定底部操作；正文命中由预览填满正文区域，不限制预览的固定高度，滚动由预览控件处理。正文命中使用 `ContentMatchDialog` / `DocumentPreviewHost` 的整页预览，沿用原搜索入口，不追加右侧命中片段面板，也不自动展开信息面板；信息面板保持原有单选展示，多选时回到选择提示。
- 显示独立标题的弹窗由 `WindowTitleBar` 统一在底部绘制 1px `DialogDividerBrush` 细线，标题和内容更新时同步显隐；主窗口页签通过 `ShowTitleBarDivider=False` 保留原衔接。编辑同步使用居中标题、全局 `dialog-status-*` 状态图标与单层文件信息分组；本地副本通过 `Button.dialog-location` 定位，长路径按段省略，技术错误可选择复制；底部仅保留处理操作，窄窗口按操作实际宽度自动纵排，多文件通过全局 `AppComboBox` 切换。重试仅禁用当前文件的重复请求，停止、定位及其他会话继续可用。
- 目录偏好只保存排序、筛选、列宽和面板状态，默认视图与分组沿用原逻辑；工作组和会话通过可选 `Preferences` 快照恢复，各窗格独立，不反写全局偏好。

- 全局 CheckBox 使用 16px 无边框圆角色块；文本／搜索／数字输入复用 `ComponentStyles.axaml`，无边框和聚焦描边，蓝色 2px 输入光标由 `InputCaret` 统一绘制。
- 输入框内部不单独绘制悬停或点击背景；搜索／地址输入保持透明，首页与文件列表搜索框的外层背景在悬停、点击和聚焦时均保持不变。
- 面包屑与右侧搜索共用 `breadcrumb-bar`：透明背景、1px `ColorBorderSubtle` 浅色主题边框；单击面包屑空白处进入路径编辑，文字导航与箭头下拉保持独立。
- ComboBox 与输入框使用相同的无边框、无悬停／按下／聚焦变色样式；下拉面板与 `context-submenu-surface` 共用背景、圆角、内边距和阴影，保留阴影外边距。
- 右键菜单、子菜单、ComboBox 下拉与工具栏弹出面板统一通过 `PopupGlass` / `popup-glass` 使用低折射、轻透明的液态玻璃，复用 `RadiusLg`；菜单 hover 与侧栏共用 `SidebarHoverBrush` / `SidebarHoverOutline`，内容层排除背景采样，原生菜单保留跨窗口边缘弹出。二级及更深菜单通过 `IsSubMenuOpen=True` 状态样式覆盖 Fluent 模板自身的实色背景、边框、圆角与内边距。菜单独立采样可见页面内容，不复用侧栏排除文件内容的背景快照；浅／深色表面底色不透明度统一约 95%，仅保留微弱透底效果，模糊半径为 8。阴影保留 12px 外边距，模糊半径为 3、偏移为 `0,2`；调整时须保证三倍模糊半径、偏移和抗锯齿余量均落在外边距内，阴影层不得被内容裁剪。

- 批量重命名工作台复用 `Assets/BatchRenameStyles.axaml` 的 `rename-list` / `rename-rules` / `rename-step`，参数使用紧凑设置字段行；目标名称变化通过通用 `RenameDiffText` 高亮，预览列表保持虚拟化。
- 紧凑步进输入统一使用全局 `NumericUpDown.compact`，整块圆角表面与居中文本，默认始终显示主题边框，聚焦时使用焦点色，原生增减和键盘行为保留；无表面图标操作复用 `Button.icon-action` / `ToggleButton.icon-action`，详情展开复用 `Expander.compact-details`，箭头随展开状态切换。

- Cmd+K 搜索在面板内固定右侧预览，默认尺寸，不显示尺寸选择、标题和路径，复用 `SuperPreviewView` 的纯内容预览及全局主题表面、`RadiusLg`，随结果选中项更新且不抢焦点；每项定位按钮复用 `Button.icon-action` 与 Fluent Location 图标，回车保持原打开操作。

- 商店版订阅页通过 `AppWindow.SubscriptionHost` 覆盖标题栏与正文，复用 `WindowTitleBar`、全局按钮和主题资源；功能介绍在窄窗口内滚动，价格、试用／订阅及恢复购买入口固定可见。

- 设置入口固定在侧栏底部、位于目录滚动区域之外，复用 Fluent Settings 图标与 `sidebar-item` 的 Border／StackPanel 结构，不嵌套按钮悬停表面；Copilot 通过设置中的启用开关控制，默认关闭，所有窗口同步按钮可见性。

- macOS 所有应用窗口统一继承 `AppWindow`，外沿由 `AppWindow.WindowOutline` 绘制 0.5px 细线，复用 `WindowBorderBrush`（浅色 `#E8E8E8`、深色 `#3A3D42`）；`MacWindowChrome` 统一关闭带深色外圈的原生阴影并保留窗口交互能力，通过 `WindowShadow.mm` 的不接收输入的原生附属层绘制轻柔外阴影（18px 模糊、向下 4px，激活 16%／未激活 10% 黑色），排除窗口内部并随移动、缩放、最小化及关闭同步；主窗口、对话框、任务窗口与文件速递共用该实现，隐藏后重显及浮动层级变化同步更新；阴影附属层允许越过菜单栏及屏幕边缘，不受可见区域约束，保持与主体外框对齐；最大化和全屏隐藏描边与轻阴影，勿通过内部 `ColorBorder*` 调整窗口外框。首次显示前通过基类 `WindowOpenedEvent` 初始化外观，关闭原生开窗动画和内容渲染层的隐式几何／纹理动画，首帧按最终尺寸铺满，不从中心扩散。显示前仅做轻量动画准备，显示后的外观更新合并执行；渲染层按实例初始化一次，并在 Avalonia 替换实际渲染层时同步准备；旧帧按内容坐标方向固定在左上角，以窗口 backing scale 保持原像素比例，跨屏缩放时同步更新，拖拽调整大小不拉伸旧纹理，也不在外观层修改 drawable 尺寸或呈现方式。原生观察器随视图释放，阴影仅在尺寸、圆角或激活强度变化时重绘。

- GitHub Pages 官网正文使用本地托管的 `Montserrat, system-ui, sans-serif`，中文回退系统字体；软件界面展示保留系统默认字体，复用 `docs/landing.css` 的按钮、分区与亮色网格主题，真实界面展示统一经 `replica-stage` 按当前截图的 1280×800 等比缩放，首屏默认文件列表与右键菜单并随视口剩余高度完整显示，正文仅介绍产品功能；底部开源与插件入口复用 `community-section` 的居中介绍和并排胶囊按钮；文案在 `docs/locales` 四语文件维护，由 `Tools/Website/build.py` 生成独立静态语言页面。
- 官网首屏堆叠复用 `showcase-stack` / `showcase-window`，前景保留完整文件列表与两级菜单；装饰窗口从无障碍树排除。主题 SVG 经 `Tools/Website/build.py` 从应用预置同步，保持原比例和 35% 图案强度；组件透明表面、菜单 65% 白色与 8px 模糊，不降低文字或图标透明度。 首页图标在构建时从本地 Fluent sprite 内嵌路径并保留 viewBox，避免外部 SVG use 引用在预览浏览器中丢失。 展示中的工具栏标签复用 `browser-toolbar-tags`，右侧面板使用 `browser-info-*` 的单页分区、透明预览、右对齐字段和主题细分隔线，新增文案由四语 `browserDemo` 提供。 官网展示的标题栏保留无纹理渐变，通过单层裁切为激活页签留出开口；页签直接透出窗口背景，不复制背景图，避免缩放拼接细线。 页签下缘不绘制分隔线或阴影。

- 官网指南复用 `landing.css` 的 `guide-index` / `guide-page` 与现有语言选择器；四语正文由 `docs/locales/guides` 生成静态页面，使用真实界面截图，规范网址和语言互链随页面生成并加入站点地图。

- 文件名显示统一复用 `FileNameText` 的实际宽度排版；空间不足时仅从中间省略，保留完整扩展名（含点号），不再按固定字符数预先截断。文件列表与图标视图复用同一排版规则，首页和画廊使用该共享文字控件。

# 多语言
- 新增或修改菜单、按钮、标题、提示和错误信息时，统一复用现有本地化机制（`LocalizationText.Get` / `ILocalizationService`），并同步补齐 `Languages` 目录下简体中文、繁体中文、英文和法语的已发布语言文件，不直接显示硬编码文案。语言列表从 `Languages/*.json` 构建生成，新增语言不修改设置列表；中文原文查询需将 key 加入中文文件的 `literalKeys`。运行时只查内存缓存，不扫描目录或读取 JSON，规则见 `Languages/README.md`。
- 动态右键菜单在构建时按当前语言取文案；检查语言切换后重新打开菜单的显示，确保中英文均正确，较长英文不裁切，参数占位符保持一致。
- 代码生成的设置列表订阅语言切换并仅刷新显示文案，不重载配置或覆盖草稿；内置打开方式使用 `OpenWithApp.DisplayLabel`，快捷键使用 `ShortcutDefinition.DisplayName` / `DisplayGroup` / `DisplayContext`，保存的名称及用户自定义名称保持原样。

# 自动测试
- 本地运行 `bash Tools/Testing/run-tests.sh fast`，按需追加 `ui` / `integration --area <功能>`；全量 `full` 由 GitHub Actions 执行。详见 `Tools/Testing/README.md`。
- `MacExplorer.UnitTests` 仅收纳明确无全局应用状态的可并行测试；其余测试保持串行与 Avalonia PerTest 隔离。普通 `dotnet test` 默认不运行显式 UI／集成／压力用例，发布 CI 必须通过统一入口运行 `full`，同时覆盖两个程序集。
- 需要启动真实应用或使用默认服务路径的自动测试、原生 UI 验证，通过 `bash Tools/Testing/run-isolated.sh <可执行文件> [参数...]` 启动，每次创建独立临时目录并设置 `MACEXPLORER_TEST_ROOT`；已有显式临时目录与测试替身的单元测试保持原运行方式。构建使用 `-p:SkipMacOSReleaseDMG=true`。
- 测试模式的数据库、配置、缓存、插件和默认用户目录均位于测试目录；启动索引及“这台 Mac”搜索仅覆盖测试目录。测试开关优先于 `MACEXPLORER_DB_PATH` / `MACEXPLORER_PLUGIN_PATH`，不会复用真实用户的历史与收藏。
- 原生 UI 测试直接启动新构建 `.app/Contents/MacOS/MacExplorer`，确保继承环境变量；不要复用已运行的普通实例。测试数据放在本次测试目录，避免真实桌面、文稿、下载目录及指向它们的符号链接。
- 此模式用于隔离默认路径，并非文件访问沙箱；需要验证真实受保护目录、系统废纸篓或权限流程时单独执行，不把系统授权窗口计为普通测试失败。

- 首页文件与展开入口共用固定 48px 图标区和文件名行，菜单按钮使用居中的 Fluent 图标；展开卡片保持 3:2 比例、透明玻璃表面，不显示标题，不透明淡色渐变遮罩覆盖窗口及顶部工具栏（深色主题使用深色渐变），右下角仅图标翻页。
- 首页文件项与展开入口的悬停卡片保持正方形，缩略图保留原始比例；最近使用混排文件与文件夹，使用文件名和首次加入记录时间的双行布局。
- 首页顶部不显示页面标题，搜索框与主目录／桌面／磁盘三个快捷按钮分别使用 `home-glass` 液态玻璃表面。
- 窗口级展开层使用 `AppWindow.WindowOverlayHost`，动画准备完成前保持隐藏；隔离输入不切换底层禁用样式，展开全程保持同一实时玻璃层与材质参数，仅内容交接，不切换玻璃截图或淡入玻璃透明度。
- 深色首页玻璃与左侧列表保持相同材质参数：关闭折射、高光和阴影，使用低背景采样透明度及深灰表面，不绘制亮边框。
- 首页玻璃卡片、展开页和顶部玻璃控件统一复用侧栏的 `RadiusLg` 圆角。

- 侧栏实际目录项拖放高亮复用 `SidebarHoverBrush` / `SidebarHoverOutline`；悬停 600ms 进入目录，松手按最终位置移动本地文件，固定区空白处保留添加快捷入口。

- 文件速递面板样式集中在 `Assets/FileDeliveryStyles.axaml`，复用 `AppWindow`、`DialogContentBrush`、`RadiusLg` 和全局按钮，通过 `ShowTitleBar=False` 隐藏红黄绿按钮与标题栏占位；面板宽高比为 √2:1，外框圆角、主题描边与轻阴影统一继承 `AppWindow`，不叠加独立描边或系统阴影；导航按钮复用全局 `workspace-nav-button`，路径复用 `BreadcrumbBar`，页签统一高度、行高及透明边框占位，选中不改变文字位置；右侧加号通过两级菜单添加目录／收藏夹，下拉可见表面距按钮 2px；右键页签删除入口，不删除原目录或收藏。顶部入口使用紧凑标签，共享文件列表以 `IsBrowseOnly` 模式承载，仅浏览、预览和复制拖出，偏好独立保存；原生拖拽激活保护在首次显示前注册，收到原生 `mouseDragged:` 时先阻止窗口前置，再进入 Avalonia／预览生成，首次及后续拖出均不激活主窗口，普通点击行为保留。

- 快捷键设置复用紧凑设置字段行，Cmd 使用 `shortcut-fixed-key`，附加修饰键使用可切换的 `shortcut-modifier`，Control 直接显示文字，修饰键宽度随内容自适应；主键通过 `shortcut-binding shortcut-main-key` 点击录制，错误使用 `shortcut-error`；提示浮层复用窗口 `WindowOverlayHost`、`PopupGlass` 和全局 `shortcut-hint` / `shortcut-group-title` / `shortcut-name` / `shortcut-keys` 样式，不抢键盘焦点；命令按钮通过全局 `ShortcutTip.Command` 自动同步快捷键提示。

- 排序菜单选中标记复用全局 `PathIcon.toolbar-popup-check` 与 `toolbar-popup-item.selected`，使用 Fluent Checkmark；未选中保留图标列占位，文字对齐。排序优先支持文件（默认）、文件夹和不区分，并保存偏好。

- 通用按钮使用 `FontFamilyButton` 的西文字体优先、中文苹方回退顺序，内容默认水平与垂直居中；菜单及行操作仍由对应样式显式指定对齐，不按语言添加固定像素偏移。
- 界面常规文字统一复用 `FontWeightRegular`，侧栏、文件／网络列表及行内名称编辑器不使用细体；标题和分组保留原有中等／半粗字重。字号使用 `TypographyTokens.axaml` 的语义资源，XAML 使用动态资源，代码创建控件通过 `AppTypography.BindFontSize` 绑定；命令编辑统一使用 `FontFamilyMono`，固定图标字形和拖拽位图徽标保留几何字号。

- 快捷键文字统一复用 `ShortcutText`、`FontFamilyShortcut` 与 `FontSizeBody`，覆盖菜单、设置主键／修饰键及 Cmd 提示；按字形实际边界将修饰键符号缩放到统一可见高度并居中，不仅统一字号。固定 Cmd 符号复用 `shortcut-symbol`。

- 主窗口页签由 `ChromiumTabStripSurface` 统一绘制交界分割线、悬停背景和激活背景。普通相邻页签无横向间距，悬停或激活时隐藏两侧分割线；两种背景共用底部衔接曲线，激活背景绘制在悬停背景上方。激活轮廓使用 `ActiveTabShadowColor` 绘制零偏移轻阴影。

- 文档预览共用 `DocumentPreviewHost`：PDF 使用按宽度适配的纵向连续页面，Office 使用内嵌系统 Quick Look；单文件预览收起内容列表，不显示底部元信息与操作栏，应用弹窗出现时隐藏原生内容。

- `.app` 列表大小在后台枚举时通过原生 `MDItem` 的 `kMDItemFSSize` 读取，不递归扫描应用包；系统大小尚未就绪时保持未知。普通文件夹列表不显示总大小，信息面板选中本地目录时通过 `DirectorySizeCalculator` 在后台统计，最多两个并发，不跟随符号链接；切换选择、关闭面板或释放页面时取消并拒绝过期结果。空目录显示 `0 B`，读取不完整时显示下限及说明；已有系统大小的 `.app` 直接复用，未知时在信息面板补算。

- 文件缩略图统一复用 `ThumbnailShadow` 主题资源与 `ThumbnailAppearance`，沿实际图片边缘绘制零偏移、轻模糊的均匀阴影；文件列表与首页共用，类型图标和文件夹图标不加矩形阴影。
- SVG／SVGZ 共用有界 SVG 渲染器；Krita 与 OpenRaster 只读取包内约定的预览图，经 `BoundedImageDecoder` 缩小。常见音频通过原生助手读取内嵌封面，其他系统支持的格式按需调用 Quick Look；缺少预览、解码失败或超过限制时显示类型图标。

- 搜索清除按钮复用全局 `Button.search-clear-btn`，尺寸 15×15px（30px 输入框高度的一半），10px Fluent 关闭图标居中。

- 信息面板的标签搜索复用 `context-submenu-surface` / `PopupGlass` 与 `omnibox-suggestions tag-suggestions`，使用 30px 紧凑候选行；仅匹配尚未添加的自定义标签，支持点击、方向键及回车添加，Escape 关闭，切换文件时清空搜索。

- 应用内弹窗统一使用 `DialogContentBrush` / `DialogGroupBrush` / `DialogDividerBrush`：浅色正文白色、分组 #F8F8F8、分割线 #EEEEEE，深色使用对应主题表面。`DialogWindow` 默认 `ExtendContentToTop`，取消独立标题栏，红黄绿按钮覆盖顶部，普通内容预留 44px；两栏窗口启用 `FullHeightContent`，侧栏灰色与分界线贯穿顶部，并自行预留按钮空间。主窗口保持原标题栏模式。
- 网络页面统一管理 SFTP、SMB、WebDAV 和系统网络卷；侧栏“位置”保留本机磁盘与原“网络”入口；“挂载”分组统一显示已连接的网络位置、网络卷及外置磁盘，为空时隐藏，复用挂载卷的单行图标、名称与弹出按钮。同一配置与对应卷合并展示，状态由 `NetworkLocationService` 同步。连接弹窗只编辑单个配置，类型参数放入 `ServiceParametersHost`，切换协议保留草稿；SMB／WebDAV 使用 macOS 系统挂载，WebDAV 输入完整 HTTP／HTTPS 地址，仅保存或连接成功后持久化，连接期间禁用编辑。

- 嵌套标签统一复用 `TagTreePanel`、`TagToolbar` 与 `TagMenuHeader`，样式集中在 `ComponentStyles.axaml` 的 `tag-tree-*` / `tag-toolbar-*`；工具栏标签复用 `toolbar-btn`，文字使用 `FontSizeLabel`、常规字重，图标与相邻工具栏入口统一为 13px，使用左图标右名称及 8px 水平内边距，悬停使用普通递归级联菜单、点击打开文件列表，菜单展开后由原生菜单交互管理关闭，不在移出按钮时关闭，树面板的名称与展开箭头独立操作；管理及文件标签菜单显示全部标签，工具栏仅显示有文件的分支并将溢出标签收进三点菜单。标签菜单复用连续整行悬停表面，行内按钮不单独绘制背景；展开区与文件列表共用 16px 点击宽度、12px Fluent 箭头及 30px 行高；二级菜单顶部左侧显示标题、右侧仅保留 Fluent Star Add 新建标签图标按钮。

- 网络页面不重复显示正文标题或刷新按钮，刷新复用顶部导航操作，连接服务器使用面包屑右侧的 Fluent 图标按钮并保留本地化提示与无障碍名称。网络页面直接复用 `FastFileList` 的 30px 行、斑马纹、悬停、选中、字体、图标槽位与滚动，表头复用 `FileListHeaderStyles.axaml`；非文件列通过 `DetailTextProvider` 提供，不触发文件缩略图读取或文件操作。状态使用全局 `Border.status-tag` 胶囊标签，操作使用 `secondary compact` 按钮，统一 24px 高度并在行内居中对齐；窄窗口允许横向滚动，错误统一通过 `AppToast` 显示窗口级 toast；Bonjour／WS-Discovery 仅在页面打开时共享运行，系统网络卷与保存的 SMB 连接统一状态，弹出前提示其他应用也会断开。

- 全局 Toast 复用 `AppToast`、`WindowNotificationManager` 与 `Assets/ToastStyles.axaml`，使用轻透毛玻璃表面、`RadiusXl`、圆形 Fluent 状态图标和主次两行文字；状态色仅用于图标区，长文案换行，窄窗口自适应，保留点击与自动关闭行为。

- 文件任务面板与独立任务窗口共用 `FileTaskPanelPresenter`；列表复用全局 `file-task-row` / `task-title` / `task-meta` / `task-percentage` / `task-progress`，任务面板按设计稿使用 `FileTaskIcons` 的统一描边图标（文件夹复制、文档下载／移动、拉链文档、云上传与空心播放／暂停），其余入口仍用 Fluent；中间为名称、传输量和细进度条，右侧固定百分比与 `Button.icon-action`；面板默认宽 560px、任务行最小高 54px；无进度条时名称、状态与图标在单行内居中对齐，窄窗口仅将带进度条的传输量换行，暂停及失败进度复用 `muted` 状态，批量清理与取消放入面板右键菜单，不显示历史入口、统计卡片或详情展开。队列、检查点和撤销记录仅保留在当前进程，清除任务不清除历史，执行中任务仅可取消，不直接移除。 单文件解压缺少原始大小时使用不确定进度条与状态文字，不显示百分比；暂停时停止不确定动画。
- 任务面板标题统一为“任务”；开合通过合成线程的 `CompositionVisual.Translation` 动画执行，不在 UI 线程逐帧更新坐标。进度通知按 50ms 合并，隐藏时不刷新列表，开合期间保留列表并在结束后同步最新状态；面板排除侧栏玻璃背景采样。

- 内存生命周期：永久移除工作区及关闭窗口时通过 `MacAutomationLifetime` 解除 Avalonia macOS 原生无障碍节点循环，包含页签标题、装饰层光标及焦点框；关闭窗口后解绑原生输入根与回调，隐藏或短暂切换不释放；`AutomationLifetime.mm` 为固定版本补齐无障碍子节点、父节点、根节点及原生窗口查询的 COM 引用释放；仅托管弱引用归零不足以证明窗口已释放，实机回归还需验证关闭窗口的原生引用归零，升级 Avalonia 后核对接口 ABI 并运行 `Tools/Performance/MemoryBench` 验证关闭对象归零及重复操作后的内存增长。成功／取消任务释放执行上下文，失败重试上下文最多保留最近 8 个、估算断点数据总量 32 MiB、15 分钟，超限释放执行闭包与断点并保留错误摘要，释放后需重新创建任务；完成摘要和撤销分组各保留最近 100 条，进行中的撤销分组不淘汰。`MacFontLifetime` 针对 Avalonia 12.0.4 为系统字体合成结果补齐请求家族名与样式的原缓存键，避免 Helvetica 等缺少备用家族名时反复从字体流重建并滞留 CoreText 注册表；不改字体选择和字重，升级时核对内部缓存接口并验证相同字体查询复用与原生字体数量稳定。macOS 在加载界面前通过 `MacFontTableLifetime` 安装固定 Avalonia 12.0.4 的 `ThirdParty/Avalonia.TextShaping` 回补，将 HarfBuzz 长期字体表复制到 POH，Blob 释放时解除句柄，不固定普通 SOH 数组；仅适配项目引用对应运行时后端接口，升级时核对源文件／ABI 并验证字体排版一致性、Blob 回收及真实目录下的堆碎片。共享位图以弱引用缓存，不释放仍被绑定的图片；`MemoryCacheMaintenance` 每 30 秒清理闲置超过 60 秒的缓存，首页已租用预览及当前可见缩略图继续保留。

- 图片预览通过 `BoundedImageDecoder` 在像素分配前读取尺寸，按最长边缩小且不放大，输出最长边最多 2048px；共享两个解码槽，每槽估算工作集上限 128 MiB，不支持安全缩小的大图返回类型图标。RAW 从原生 RGB 缓冲区直接采样到目标尺寸，不再建立全尺寸 RGBA 副本。搜索导航最多 100 条，最多缓存 8 份／4 MiB 结果快照，清理后返回原搜索时按原范围重新查询。
- 内存诊断由 `MemoryDiagnostics` 写入 `RuntimePaths.LogDirectory` 的 `memory-*.jsonl`，每 30 秒记录 physical footprint、RSS、托管堆、GC、窗口／页签与弱注册的缓存统计，并记录图片拒绝／大解码、失败重试状态清理及原生节点释放；不记录文件路径、搜索内容或图片内容。日志队列最多 128 条，其中操作最多占 96 槽且每秒最多 128 条，超限在序列化前丢弃；趋势保留 2 个文件／4 MiB、事件保留 6 个文件／12 MiB，每文件最多 2 MiB，最后写入超过 7 天清理、单文件满一天轮转，清理只在后台执行并保护活动日志，无法腾出空间或写盘失败时停用诊断；不得为采样强制 GC 或建立强引用注册表。操作时间线只使用固定操作类型、进程内匿名 ID、数量与耗时，生命周期最多跟踪 2048 个弱引用，已结束元数据最多观察 24 小时；统计源最多弱登记 512 个，锁忙跳过并标记缺失；每个新文件保留会话／运行时／GC 配置，日志包含采样自身耗时，并汇总关闭／释放后经历 Gen2 仍存活的候选；GC 详情按周期分别采集各类最近结果及分代／LOH／POH 前后数据，编号不变不重写，不订阅分配事件。具体字段与限制见 `Tools/Performance/MemoryBench/Diagnostics.md`。
- 窗口缩放诊断通过 `WindowRenderDiagnostics` 复用上述日志，记录最大化／还原前后及稳定后的布局、原生视图、Retina 缩放和 Metal 画布尺寸，持续不一致标记为 `window.render_mismatch`；只读采样，不触发布局、重绘、GPU drawable 获取或事务刷新，不记录窗口标题和画面内容。采样随窗口关闭解除，取证步骤见 `Tools/Diagnostics/window-rendering.md`。

- 主窗口页签通过 `AdaptiveTabPanel` 保留按内容 180–280px 的宽度，拥挤时按比例收窄至至少 112px，之后横向滚动；组名单独按内容计宽，最长 148px。新建按钮通过全局 `tab-add` 样式与可见页签区域保留 6px 左间距，与右侧预设及分屏入口统一使用全局 `title-nav-button`，采用与工具栏图标按钮一致的 32×32px 悬停尺寸、左右各 2px／上下各 6px 外边距并在 44px 标题栏内垂直居中；三项分别使用 Fluent Add／Square Multiple／Split Vertical 24 Regular，14px 图标居中并在同一水平线上，名称裁切并保留完整名称提示，不设置固定总宽度上限。

- 主窗口页签通过 `TabStripInteraction` 捕获指针并预览横向排序，松手后提交顺序；拖动保留当前激活页签与分栏，Escape 取消。页签轮廓使用 `TabSilhouetteCache` 按尺寸缓存，拖动内容与背景同步；右键菜单以目标页签操作，新建与关闭之间使用分割线，批量关闭复用工作区释放流程。
- 工具栏收藏夹、溢出及级联标签右键统一提供重命名、删除和添加子收藏夹，编辑复用 `TagTreePanel` 行内输入。系统颜色标签通过持久化 `DisplayNameOverride` 优先显示别名，底层名称与 Finder 关联保持不变；删除沿用现有层级删除规则。

- 普通文件夹与照片封面统一通过 `SvgIconCache.RenderFolderPng` 使用 macOS 系统蓝色文件夹底图；照片封面仅叠加预览，不另画文件夹或口袋遮挡。原生图标不可用时共用蓝色 Fluent 文件夹回退，不恢复旧黄色图标。

- 图标视图通过状态栏滑杆及 Ctrl＋滚轮切换 32／56／96／128pt 与画廊档，网格复用 `FastFileList` 几何、屏幕倍率分档和位图缓存；画廊底部复用 `HomeFileImage` 与横向虚拟化列表，上方复用 `SuperPreviewView` 单文件内容和原生文档预览，随选择取消过期任务，页签隐藏时释放预览资源。
- 缩略图缩放复用全局 `Slider.compact-slider`，蓝色进度、灰色底轨高 6pt，白色圆形滑块直径 21pt，带细描边和轻阴影；悬停与拖动显示蓝色光晕，深色数值气泡贴随滑块。滑杆和滑块不裁切，状态栏在分栏圆角裁切内给光晕保留至少 8pt 底部空间。

- 后台任务入口复用全局 `AnimatedProgressRing`（16px、2px 线宽，与相邻工具栏图标协调）：圆弧表示未结束任务的等权平均进度，蓝青渐变在渲染线程流动；包含不确定进度时改用短弧，暂停／隐藏／最小化／关闭时停止持续动画。状态颜色复用主题资源，中心留空，四语提示与无障碍名称同步进度及异常状态。任务开始、进度更新或完成不自动展开面板，点击入口才切换面板；手动展开后保持显示，直至用户关闭。

- 文件速递固定首个“暂存”页签，暂存路径仅保留在当前进程；拖拽投放窗口复用 `AppWindow` 与 `FileDeliveryStyles.axaml` 的 `delivery-drop-surface`，不激活主窗口，清空暂存仅移除路径引用。

- 背景主题仅作用于主窗口，通过 `WindowBackgroundTheme` 和 `BackgroundThemeSurface` 共享窗口坐标。`BackgroundThemePresets` 按科幻赛博、自然生境、动物三组提供各三款 SVG 预置，动物固定为龙、兔、熊猫；以 QQ 原始纹理改作，保留图案原比例、尺度与构图，同尺度单元连续排布为宽幅纹理，精品 PNG 转为透明矢量路径，精品与动物迷彩采用相邻镜像衔接，不嵌入位图或横向拉伸。使用普通混合，默认图案强度 35%；渐变覆盖整个窗口，不随纹理重复。参考与生成资产的 SHA-256 记录在 `Assets/BackgroundThemes/sources.json`，改作素材不按代码许可证重新授权。矢量缓存同时限制宽 4096px、高 1280px，用户导入位图仍限制最长边 2048px。旧开发预设自动映射并保留自定义图片。预设启用时增强主窗口次级文字对比度，切回照片或关闭主题恢复原色。`BackgroundThemeComposition` 将铺满窗口的渐变与独立强度的纹理合成；纹理或自定义图片按窗口高度等比横向平铺或居中铺满裁切，不随列表滚动。清晰与磨砂图从同一合成结果产生，每窗口缓存一对，合并尺寸／强度更新并在后台最多两个并发任务生成，源图片不随窗口缩放重新解码；取消过时任务、关闭窗口释放缓存，源素材由任务租用避免切换时提前释放。`Workspace*Brush` 提供透明表面入口，默认保持原主题；激活页签与底部状态栏显示原图，页签与下方连续。标题栏空白区从同一合成图的轻模糊缓存取宽区域均色，显示无纹理渐变；信息预览与标签复用全局 `workspace-preview-surface`／`info-tag-chip`，工具栏切换器及按钮交互态使用半透明表面。`WorkspaceControlHoverBrush`／`WorkspaceControlSelectedBrush` 默认跟随原交互配色，主题仅在主窗口覆盖，关闭后还原。分隔线统一通过 `WorkspaceDividerBrush` 适配，覆盖页签、工具栏、面包屑、列表表头／分组、信息字段及状态栏；主题下浅色使用约 9% 黑色、深色使用约 14% 白色，默认保留原线色，窗口外框与弹层分隔线仍用原资源。导航／面包屑与工具栏共用 `workspace-toolbar-material` 的 `RadiusLg` 圆角表面，和信息面板复用轻磨砂缓存；磨砂与原图共用相同坐标、比例及透明度，只改变模糊程度；信息面板磨砂从分隔线开始，不覆盖左侧拖拽区。侧栏与首页继续复用现有玻璃，主题启用时通过 `WorkspaceSidebarRefraction`／`WorkspaceSidebarRefractionHeight` 增强侧栏折射。设置入口使用 `BackgroundThemeSettingsView` 与紧凑设置分组，预设主题与自定义图片共用入口，图案强度不改变渐变底色，语言切换保留选择与强度；主题以紧凑网格展示每图案一张 SVG 预览卡，点击卡片打开取色卡；默认色及推荐色来自图案配置，不显示标题和名称，名称保留在提示和无障碍标签中。四语文案同步；图片经 `BoundedImageDecoder` 限制解码后导入应用 Themes 目录，清除仅删除托管副本。独立窗口和弹出菜单不增加主题配置，不对文字与图标整体调整透明度。

- 主题设置使用全局 `Button.theme-swatch` / `Border.theme-swatch-image`，每图案一张 SVG 卡片，点击打开取色卡；默认色与推荐色通过 `BackgroundThemePreset.DefaultColor`／`ColorOptions` 配置，不显示图案名称、标题或悬停文字，名称仅保留在四语无障碍标签中。`BackgroundThemeOptions.Colors` 按图案保存配色和自选色，调色复用源纹理并同步清晰／磨砂背景；小预览在后台按 384×128 上限生成，离开设置时释放。调色盘复用通用 `ColorPalette`，主题弹出层使用 `context-submenu-surface` / `PopupGlass`。

- 主题与交互颜色共用 `ColorPalette`，样式集中在 `ComponentStyles.axaml` 的 `color-palette-*`；色块固定 18px，避免无内容 Border 被居中布局压成零尺寸。十六进制输入保留草稿，回车／失焦后校验应用，Esc 撤销，不在输入过程中回写文本或保存短格式；交互颜色允许透明度，背景主题仅允许 RGB。推荐色来自各自配置，恢复默认由所属服务处理。

- 背景主题交互表面由 `WindowBackgroundTheme` 在主窗口内统一覆盖语义色与 Fluent 状态资源；菜单、子菜单和下拉列表保留 `PopupGlass` 材质并透明透底，独立设置／对话框保留默认背景。主题仅按高度等比横向平铺；设置“透明度”以 100 减去已保存强度显示，保留旧配置效果。

- 主题取色卡复用 `ColorPalette.popover` 与 `PopupGlass` 的单层表面，通过左对齐 Popup 紧贴所选卡片，不叠加默认 Flyout 卡片；恢复默认使用带提示的 Fluent 图标，内联交互调色盘保留文字按钮。主题网格直接读取随包 PNG 预览纹理并实时合成配色，不在打开设置时解析 SVG；更新 SVG 后运行 `Tools/Design/ThemePreviews` 重新生成预览。

- 商店版通过 `FeatureAccessService` 的内存快照按功能授权，基础操作不查高级权限；`AppWindow.SubscriptionHost` 仅按需展示可关闭的升级页。侧栏底部显示体验状态，背景预览免费、应用及修改为高级功能，到期仅停止显示并保留配置；文件速递暂存与 WebDAV 分别复用文件速递及网络权限。

- 启动页签恢复默认关闭，按窗口分别保存文件夹顺序、重复页签与活动页签；逐个关闭窗口时保留最后关闭窗口，退出应用时一次保存全部窗口及活动窗口。恢复先建立页签并选中已保存位置，其余目录最多两个并发静默加载，完成不切换焦点；关闭恢复中的页签只取消并释放该页签，不影响其他页签。显式路径与快速访问优先，旧单窗口快照自动迁移。

- 工作组复用全局 `Border.tab-group-header` / `TabGroupLine`，自动分配并保存颜色，不提供颜色编辑；组名使用单层 20px 实色标签，不加外框或底色托盘；展开时在页签高度内显示 2px 彩线，距页签顶部 2px。组名和普通成员的色线延伸至各自右边界。激活或拖动中的页签在顶部独立显示彩线，两端各收进 6px，沿圆角留出断口。所有页签内容统一使用 `Grid.tab-title-content` 的 `10,0,4,2` 边距，位置图标靠左、关闭按钮靠右，计入关闭按钮内部留白后两侧图形距边缘约 10px，不为组内首项缩小边距；组名距离仅调整标题本身。相邻彩色工作组之间通过全局 `ListBoxItem.group-separated` 保留 8px 窄竖缝，不额外绘制弧形分界、描边或整组底色，其他页签背景保持原样；色线保留原有贯穿效果与顶部位置。点击组名只切换展开或收起，保留当前活动页签和分屏。工作组成员横向滑出或缩回，后续页签同步移动，彩线和激活轮廓随组范围裁切。开合只在切换时布局，合成线程复用起止画面，连续点击从当前进度反向。启用系统减少动态效果时直接切换。收起时仅显示组名标签，隐藏成员页签和顶部色线；整组拖动仅排序，不改变成员、组内顺序或分屏状态。普通新建页签默认在组外，仅“工作组内新建页签”和分屏补位显式加入组，插入位置不得拆开已有组；单页签拖动时预览目标颜色与组尾边界，拖出隐藏色线，松手提交，Esc 恢复。组内成员不限，各组独立保存布局、可见页签和活动栏；未分组开启分屏自动建组，补位只使用同组成员。组名编辑复用 `DialogWindow` 与紧凑设置字段行，会话恢复沿用本地文件夹范围和原开关。

- 工作组预设入口位于标题栏分屏按钮左侧，复用全局 `Button.title-nav-button`，菜单打开时追加 `active` 主题选中态；下拉右对齐，依次显示预设名称与目录数、保存、管理，以细分隔线分区。预设单击在当前窗口打开工作组，已打开时展开并激活原组，不重复创建；预设关联随会话保存，改名或导航后仍可复用；管理使用 Fluent Settings 图标及递归子菜单，无预设时禁用。保存和重命名复用 `TabGroupNameDialog`，透明标题栏标题使用全局 `TextBlock.dialog-window-title`，过长省略并提示完整标题；同名替换与删除复用确认对话框，手动预设与自动会话恢复独立。

- 空格视频预览复用 `VideoPreviewHost` 与 macOS `AVPlayerView`，等比显示并点击后播放，空格／Escape 关闭；系统控件提供进度和音量，隐藏、最小化及应用弹窗覆盖时暂停，恢复后不自动继续。切换和关闭时取消异步加载、移除观察者并释放播放器后再清理临时文件；Cmd+K 和画廊继续显示静态缩略图。

- Diff／Patch 预览复用 `DiffPreviewView` 的双栏只读 AvaloniaEdit、`diff-preview` 样式与全局等宽字体／字号；按区块配对增删行并补齐空位，页边显示原文件行号，双向同步纵向滚动，各栏保留横向滚动。增删行使用 `SuccessBrush`／`DangerBrush` 文字与淡色背景，文件头与区块使用 `AccentBrush`；复制跳过对齐空位并保留原换行，无有效区块、格式不支持或超过十万行时显示补丁原文，读取上限和编码共用 `TextPreviewReader`。

- 同名文件弹窗以全局默认按钮的实际尺寸为比例基准，按钮固定 32px 高、默认面板约 616×380px，不叠加 compact 字号或局部按钮高度；使用共享文件类型图标与左右元数据对照，窄窗口改为上下排列；标题居中复用 `dialog-window-title centered`；底部取消任务与处理操作分开，保留两者为默认操作，替换的附加选项复用全局 `SplitButton.secondary` 与标准菜单；其外观直接由 `Button.secondary` 和 `Button.icon-action` 组合，不复制颜色、圆角或边框样式，弹窗按钮不局部覆盖颜色和内边距。

- 应用按钮统一使用 `Controls/AppButton.cs` 的 `AppButton` / `AppToggleButton` / `AppSplitButton`，所有按钮样式、主题和专用变体集中在 `Assets/ButtonStyles.axaml`，经 `ComponentStyles.axaml` 全局加载；页面只指定 Classes、内容、行为和布局，不设置颜色、字体、圆角、边框、内边距或固定高度。普通操作使用 primary／secondary／ghost／danger，按需追加 compact／small／dialog-action，图标操作用 icon-action；透明表面共用 SurfaceButtonTheme，组合下拉复用现有按钮。`Tools/Testing/check-buttons.py` 在统一测试入口自动检查，详见 `docs/development/buttons.md`。

- 独立插件 UI 宿主同样加载全局字体、主题及 `ButtonStyles.axaml`；SDK 示例保持已发布的 Avalonia Button API，通过统一 Classes 接入，不复制样式、不依赖主程序控件类型。

- 普通按钮尺寸固定为默认 32px、compact 28px、small 24px、large 36px，同档 primary／secondary／ghost／danger 高度一致；上下不叠加内容内边距，由全局模板居中文字和图标，水平内边距分别保留 12／9／8／12px；字体仍跟随全局字号资源，验证四语、三档字号与图标组合不裁切。

- 下拉选择统一使用 `Controls/AppButton.cs` 的 `AppComboBox`，字段、选项及四档高度均在 `Assets/ButtonStyles.axaml` 定义，设置行显式使用 compact，页面不再覆盖样式；ComboBox 下拉与 MenuFlyout（含组合按钮菜单）共用标准 `context-submenu-surface` / `PopupGlass`，保留统一圆角、内边距、悬停与阴影空间。
