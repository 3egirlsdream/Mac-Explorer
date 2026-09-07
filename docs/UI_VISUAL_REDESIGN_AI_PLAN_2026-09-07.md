# FKFinder / Mac Explorer UI 视觉评审与 AI 执行方案

日期：2026-09-07
项目根目录：`/Users/jiangxinji/Documents/FKFinder`
评审基线：`main`，HEAD `f6bdecf`，版本 `1.0.34`，Avalonia `12.0.4`，.NET `10`。
交付状态：**本地实现已完成，构建及 251 项自动回归通过；部分实机综合验收待完成。**
实施结果、前后截图与未验证范围：[实施与验收记录](/Users/jiangxinji/Documents/FKFinder/docs/UI_VISUAL_REDESIGN_IMPLEMENTATION_2026-09-07.md)。

## 1. 结论与设计方向

值得修改。当前程序已经有完整的桌面文件管理器结构，也建立了颜色、字体与交互资源；最有价值的提升是让视觉层级更清晰、同类控件更一致，并改善小文字、焦点提示和复杂状态的可读性。

推荐方向：**安静、清晰、紧凑的 macOS 文件工作台**。

- 文件内容是主角，侧栏、工具栏和边框退到辅助层级。
- 底色统一为中性灰白 / 中性深灰；蓝色用于选中、焦点和主要动作。
- 操作图标使用现有 Fluent Regular 体系，文件夹、文件、应用图标保留原生或现有类型识别能力。
- 连续工作区使用平整表面；菜单、弹窗才使用明显浮层阴影。
- 维持桌面操作密度、键盘效率、多窗格与预览能力，不用网页展示页的大字号、大卡片和大面积装饰留白。

本方案的颜色与尺寸是针对本项目作出的设计选择，不是 Apple 官方规定值。设计依据是内容优先、导航与内容有明确层次、控制导航区域的用色、保证文字和控件辨识度。Apple 的 Liquid Glass 指引强调控制导航区域用色；本项目据此采用克制材质，不把整个文件区改成透明玻璃。[Apple Liquid Glass](https://developer.apple.com/documentation/technologyoverviews/liquid-glass)

## 2. 证据范围与执行前提

### 2.1 本次实际查看了什么

| 类型 | 覆盖内容 | 证据范围 |
|---|---|---|
| 实际界面 | 首页、项目目录列表、单文件选中、信息面板、图标视图 | 通过 macOS 应用界面观察，浅色外观 |
| 代码 | 全局颜色与字体、SurfaceCard、标题栏与页签、工作区、侧栏、工具栏、文件列表、首页、AI 文字搜索、信息面板、部分设置和远程连接弹窗 | 对当前工作区进行只读检查 |
| 数值核查 | 当前弱文字颜色、主色，以及建议颜色的若干对比度 | 依据 sRGB 相对亮度计算，见第 5 节 |
| 未实测 | 深色外观、所有弹窗、分组列表、分屏全部排列、动态字号、VoiceOver、各种原生预览类型 | 属于实施后的验收项，不声称已通过 |

运行时观察涉及 `bin/TabSurfaceQA/Mac Explorer.app`。机器上还有其他调试 / QA 实例；观察过程中该应用包也出现了变化。因此：**截图观察说明当时界面的表现，不证明运行程序集与当前源码完全一致。** 未重建、终止或覆盖这些正在运行的实例。

实际信息面板在一次 Markdown 选择中显示了“预览生成失败”。这里只把它作为错误态的视觉样本，不据此断言当前主分支的预览功能存在确定缺陷，也不推断失败原因。

### 2.2 当前工作区在持续变化

已有大量未提交改动，涉及标题栏、页签、文件图标、文件列表加载与快照、缩略图、预览及相关测试。执行 AI 必须先重新读取相关文件和差异。

特别注意：旧的 `ChromiumTabShape.cs` 已在当前工作区删除，实际页签表面入口是 `ChromiumTabStripSurface.cs`；不能依据旧资料恢复旧类。工作区还已有紧凑布局与搜索展开相关实现，不能重复添加一套。

本文件采用“文件 + 控件 / 类名”定位，避免依赖容易失效的行号。若源码已完成某个条目，验证后跳过，不重复改造。

## 3. 当前评价与修改优先级

优先级含义：P0 为可读性和交互辨识；P1 为主体视觉整理；P2 为外围界面一致性。它们表示本次设计执行顺序，不表示事故等级。

| 编号 | 优先级 | 观察 / 实现依据 | 修改意见 | 预期收益 |
|---|---|---|---|---|
| V01 | P0 | `TextMutedBrush=#989A9E`、`TextSubtleBrush=#AEB0B4`；状态与面板日期使用弱文字色 | 加深承载信息的弱文字；将装饰色与可读文字色区分 | 日期、说明、占位提示更易读 |
| V02 | P0 | 全局 Button 和工作区导航焦点使用 `InteractionSelectedBrush`；服务还把 Selected 映射到 FocusRing | 独立焦点颜色及样式，阻断选中颜色覆盖焦点的路径 | 键盘焦点不再依赖浅选中底色 |
| V03 | P1 | 标题栏偏暖，主体偏冷白；侧栏、工具栏和内容分别包裹圆角 SurfaceCard | 统一中性底色，去掉连续工作区内部的多重卡片阴影 | 空间结构更完整，减少拼接感 |
| V04 | P1 | 首页同时出现顶部路径输入、右上搜索和中央搜索 | 首页只保留中央主输入；目录页保留路径与搜索各自职责 | 入口更明确 |
| V05 | P1 | 侧栏大量独立硬编码彩色图标；与彩色文件图标同时争夺注意力 | 侧栏操作导航统一中性色，选中用蓝色；标签和真实状态保留颜色 | 文件与选中位置更突出 |
| V06 | P1 | 导航和工具图标多为 14 DIP，信息面板关闭图标 10 DIP / 按钮 24 DIP；部分图标按钮 AX 名称为类型名 | 统一图标尺寸与光学重量，扩大少数小按钮命中区，补齐语义名称 | 操作更好认、更好点 |
| V07 | P1 | 图标模式文件名使用 11 DIP；存在固定 13 字符中间省略策略 | 文件名提升到 12 DIP，保留现有省略规则、完整提示和布局尺寸 | 保持密度并提升读名能力 |
| V08 | P1 | 信息面板日期较淡、七个彩色标签按钮醒目，预览失败仅有一句弱提示 | 调整信息排序和标签视觉重量，明确空 / 失败 / 不支持状态 | 预览与文件信息更突出 |
| V09 | P2 | 远程连接弹窗按钮圆角 6，设置导航圆角 7，全局交互圆角 10；存在局部原始 Path 与字符关闭按钮 | 收敛同类控件的几何和图标规则 | 页面之间更像同一个程序 |
| V10 | P2 | 文件列表空态仅区分搜索与普通空目录；AI 空态有提示但层次偏弱 | 提供准确的状态文案和已有动作入口 | 用户知道发生了什么、下一步做什么 |

### 3.1 应当保留的基础

- 保留当前页签与下方内容连续衔接的造型，以及单独的导航操作区域。
- 保留原生 macOS 文件夹 / 应用图标、已有文件类型图标和缩略图解析流程。
- 保留当前轻字重侧栏偏好，先解决颜色与信息层级；不把全局文字一律加粗。
- 保留普通按钮默认透明、悬停时才出现底色的交互习惯。
- 保留面包屑无悬停背景这个明确例外。
- 保留系统主题、字体预设、交互自定义、列宽和面板宽度的持久化。
- 保留列表虚拟化、快照更新、滚动锚点、多选、框选、拖拽、重命名和原生预览。

这些保留项是实施约束，不是重新开发的任务。

## 4. 布局规范

### 4.1 目录页结构

```text
┌──────────────────────────────────────────────────────────────────────┐
│ 红黄绿    当前工作区页签 / 新建页签             窗格布局 / 窗口操作    │ 44
├────────────────┬─────────────────────────────────────────────────────┤
│                │ 后退 前进 上级 刷新   路径面包屑       搜索          │ 40
│ 侧栏           │ 新建  剪切 复制 粘贴 删除 首页   视图 排序 信息 更多 │ 40
│ 默认宽 240     ├────────────────────────────────┬────────────────────┤
│                │ 名称 / 日期 / 大小 / 类型      │ 信息面板           │
│ 常用位置       │                                │ 默认沿用当前宽度   │
│ 收藏夹         │ 文件列表或图标网格             │ 预览               │
│ 位置           │                                │ 基本信息           │
│ 远程服务器     │                                │ 标签 / 快速操作    │
│ AI 智能        ├────────────────────────────────┴────────────────────┤
│ 标签           │ 数量 / 选中统计                         可用容量     │ 28
└────────────────┴─────────────────────────────────────────────────────┘
```

上图是目标结构示意，不是效果图。为保持现有导航效率，本轮保留目录页两行工具区域，主要通过去除浮卡、统一对齐和明确分组减少视觉层数，不强行把所有按钮挤进一行。

**布局数值，均为 DIP：**

| 项目 | 目标 | 实施约束 |
|---|---|---|
| 窗口最小尺寸 | 维持 `1000 × 680` | 不新增移动端布局 |
| 标题 / 页签行 | 44 | 不改页签曲线、拖拽区和红黄绿位置 |
| 路径导航行 | 40 | 导航按钮命中区统一 32 × 32；图标 16 |
| 文件操作行 | 40 | 操作按钮高 32；分组之间 12，组内 4 |
| 侧栏普通宽度 | 默认 240，当前为 260 | 只调整默认常量，不清空已有用户宽度配置（如有） |
| 侧栏紧凑展开 / 图标轨道 | 保留 220 / 48 | 保留已有 1180 工作区宽度断点与 `ForceCompact` |
| 信息面板宽度 | 沿用当前保存值和 380 初始值 | 不覆盖用户拖拽宽度和展开预览逻辑 |
| 状态栏高度 | 默认 28，大字体时可增长 | 状态文字使用 11–12 DIP，不能裁切 |
| 表头 / 普通列表行高 | 保留 30 / 30 | 本轮不引入列表密度设置 |
| 工作区内边距 | 横向 12；分组间 12 或 16 | 文件名列、表头、工具分组视觉对齐 |
| 弹窗内容边距 | 20；大设置页 24 | 不将表单各行独立做成浮卡 |

### 4.2 消除不必要的浮卡

修改 [ExplorerWorkspaceView.axaml](/Users/jiangxinji/Documents/FKFinder/Views/ExplorerWorkspaceView.axaml) 内的 `SidebarSurface`、工具栏 SurfaceCard、内容 SurfaceCard：

1. 侧栏使用平整 `ColorBgSidebar` 表面，与文件区之间保留 1 DIP 分隔线。
2. 工具栏和文件区共用连续内容底色；去掉两者之间的 4 DIP 卡片缝隙、单独圆角和阴影。
3. 单工作区内部不再出现“侧栏一张卡、工具栏一张卡、列表一张卡”的三组外轮廓。
4. 优先将这几处容器替换为 `Border` / `Grid`，保留名称与内容绑定；同步更新依赖旧容器类型的代码和测试。
5. 不直接把全局 `SurfaceCardShadow` 清零，因为其他真正的浮层可能仍然需要它。
6. 如果保留 SurfaceCard，必须让模板真正消费所需属性。当前模板背景直接读 `SurfaceBrush`，仅给外层设置 `Background` 不足以保证效果。

### 4.3 工具栏分组

- 左侧文件动作保持当前顺序：新建、剪切、复制、粘贴、删除、首页。
- 右侧查看动作集中：图标 / 列表切换、排序、信息面板、更多。利用中间弹性空白区分“修改文件”和“查看文件”。
- 新建、排序保留文字，不新增一排重复标签；图标按钮都具有可读名称和快捷键提示。
- 未选择文件时，依赖选中的动作禁用；有无可用剪贴板内容决定粘贴状态。复用现有权限 / 状态计算，不能只把按钮画成灰色。
- 删除在普通工具栏中保持中性色，悬停与确认界面使用危险色；不常驻大块红色。
- 菜单打开时，触发按钮保持可见的打开状态，但不能伪装成文件选中。
- 紧凑模式复用 `FinderToolbar:compact` 与现有溢出菜单，保证隐藏的动作仍可从更多菜单或原快捷键访问。

### 4.4 首页收敛为一个主输入

**首页最终状态：**

- 保留标题栏和侧栏。按 2026-09-07 后续反馈，首页隐藏顶部整行导航；目录页仍保留导航返回能力。
- 首页不显示顶部的后退、前进、刷新、首页定位文字及页面内搜索入口，导航行不保留空白高度；中央输入继续接收 ⌘L 与页面搜索快捷键。
- 隐藏不适用于首页的文件操作行，以及“0 项”状态栏。
- 中央只保留一个 Omnibox：宽度 `min(560, 可用内容宽度 - 48)`，高 44，圆角 10，文字 14。
- 输入框上方显示“打开位置或搜索文件”，18 DIP、Medium；下方说明用 12 DIP、Secondary。
- 提示文字使用“输入路径、网址或文件名”；AI 查询能力继续保留在已有建议分类或准确说明中，不新增服务、不暗示不存在的全能助手。
- 三个快捷入口改为一行紧凑入口：图标 24、文字 13、单项约 136 × 44，间距 12；空间不足时允许换行。
- 不增加欢迎插画、营销标语、假统计或没有真实数据来源的最近文件卡片。

入口减少后，必须同时调整焦点路由：首页 `⌘L` / 原页内查找入口应聚焦中央输入框；目录页维持原行为；`⌘K` 全局搜索保持现有独立能力。复用 `HomeView` 的 Omnibox 事件与服务，不复制搜索逻辑。新增的聚焦方法仅负责焦点，不承担查询职责。

### 4.5 侧栏与多窗格

侧栏目标顺序为：常用位置、收藏夹、位置、远程服务器、AI 智能、标签。只移动现有分组容器，保留收藏排序、拖放目标、折叠状态与原事件。空收藏夹保留可发现的添加入口，不铺一大片空白。

普通侧栏行高保持现有字体布局规则，默认约 30–32；图标 16，图文间距 8，横向内边距 12。分组标题 11、Medium、Secondary，组间距 16。侧栏仍允许滚动，不能为了“所有入口都露出”把字号压到 10。

多窗格以每个 `ExplorerWorkspaceView` 的实际可用宽度决定紧凑状态，不能用整个窗口宽度替代。活动窗格增加清晰的细边界，非活动窗格不对全文降透明度；键盘焦点必须能说明下一次命令作用于哪个窗格。

## 5. 色彩与材质规范

### 5.1 建议颜色表

直接维护 [ThemeTokens.axaml](/Users/jiangxinji/Documents/FKFinder/Assets/ThemeTokens.axaml)，继续使用现有语义资源，避免新增另一套平行主题目录。

以下均为不透明 `#RRGGBB`；透明颜色如确有需要使用 Avalonia 的 `#AARRGGBB`，不能混用 CSS 的尾部 alpha 写法。

| 语义资源 / 用途 | Light | Dark | 规则 |
|---|---|---|---|
| `WindowBackgroundBrush` | `#F3F4F6` | `#17191D` | 应用外壳 |
| `TitleBarBackgroundBrush` | `#ECEEF1` | `#202329` | 中性背景；去掉当前暖米色 / 暖棕色偏向 |
| `SurfaceBackgroundBrush` | `#F3F4F6` | `#17191D` | 非内容底层 |
| `SurfaceBrush` / 内容 | `#FFFFFF` | `#1B1D21` | 文件阅读表面 |
| `SurfaceElevatedBrush` | `#FFFFFF` | `#292D34` | 菜单与弹层 |
| `SurfaceMutedBrush` | `#F3F4F6` | `#24272D` | 次级分组表面 |
| `ColorBgSidebar` | `#F3F4F6` | `#202329` | 侧栏 |
| `TextPrimaryBrush` | `#252830` | `#ECEEF2` | 文件名、标题、字段值 |
| `TextSecondaryBrush` | `#565D67` | `#B8BEC8` | 日期、类别、说明 |
| `TextMutedBrush` | `#646B76` | `#A4AAB5` | 仍必须读清的提示和次要文字 |
| `TextSubtleBrush` | `#737A86` | `#8E98A8` | 仅装饰或更弱信息；关键文字不得使用 |
| `TextDisabledBrush` | `#A0A5AF` | `#686F7C` | 真正禁用的内容 |
| `BorderBrush` / 分隔 | `#E1E4E9` | `#343942` | 装饰性结构分隔；不作为唯一状态线索 |
| `BorderStrongBrush` | `#7C8491` | `#A4AAB5` | 需要明确辨识的边界 |
| `AccentBrush` / `ColorAccent` | `#2463D4` | `#8AB4FF` | 主要动作与状态 |
| `AccentHoverBrush` | `#1F56BA` | `#A3C4FF` | 主要按钮悬停 |
| `TextOnAccentBrush` | `#FFFFFF` | `#FFFFFF` | 保留当前固定深底预览徽章等消费者，不能全局改成深字 |
| 新增 `ButtonPrimaryForegroundBrush` | `#FFFFFF` | `#17253D` | 仅供主要按钮；深色主题的浅蓝按钮使用深字 |
| `InteractionHoverBrush` | `#ECEEF2` | `#2B3038` | 普通交互悬停 |
| `InteractionPressedBrush` | `#E0E4EA` | `#363D48` | 普通交互按下 |
| `InteractionSelectedBrush` | `#E5EEFC` | `#283E61` | 普通选中背景 |
| `InteractionSelectedHoverBrush` | `#D8E6FA` | `#324C74` | 已选中再悬停 |
| `FocusRingBrush` | `#2463D4` | `#8AB4FF` | 独立、不透明的键盘焦点框 |
| `DangerBrush` / `ColorDanger` | `#BF3B43` | `#FF8D94` | 错误文字、危险动作 |
| `SuccessBrush` / `ColorSuccess` | `#23784D` | `#7AC99A` | 成功 / 已连接；始终配语义文字或符号 |
| `WarningBrush` / `ColorWarning` | `#946200` | `#EBC16A` | 警告；始终配语义文字或符号 |

兼容资源必须与对应主资源同步，包括 `ColorBgContent`、`ColorBgToolbar`、`ColorBgPrimary`、`ColorAccentHover`、`ColorBgSelected` 等。先检查资源的实际类型和运行时覆盖，不能把 Brush 作为 Color 填进 `SolidColorBrush.Color`。不为了去重引入新的主题框架。

**前景资源不能机械复用：** `InfoPanelView` 的固定深色预览徽章和 Git 徽章也使用 `TextOnAccentBrush`。本方案保留它的白色默认，并用一个小范围的 `ButtonPrimaryForegroundBrush` 解决浅蓝主按钮文字；危险实心按钮若采用浅红背景，单独使用深色前景（建议 Dark `#371518`），危险文字按钮仍使用 Danger。逐个检查标签、徽章和预览叠层的真实前景 / 背景配对，不能因为主按钮达标而让深底徽章失去文字。

`Glass*`、ComboBox 弹层、地址栏、视图分段切换等相关资源也需按上表表面角色收敛。保留用户开启的材质能力和现有不透明默认；不能批量清空旧设置来强制显示新配色。

### 5.2 数值核查

以下计算使用源码默认颜色和指定背景，不是对截图全部像素的测量，也不能代表用户自定义主题。值保留两位小数用于阅读，实际判断用未舍入数值。

| 颜色组合 | 对比度约值 | 判断 |
|---|---|---|
| 当前 `#444648` / 白底 | 9.48:1 | 当前主文字本身已有足够对比，不是所有文字都需要“救黑” |
| 当前 `#626468` / 白底 | 5.93:1 | Secondary 默认值可读 |
| 当前 `#989A9E` / 白底 | 2.82:1 | 不适合 11–13 DIP 的信息文字 |
| 当前 `#AEB0B4` / 白底 | 2.17:1 | 仅适合不承载必要信息的弱装饰 |
| 当前白字 / `#3B82F6` | 3.68:1 | 小字号主要按钮文字需要调整配色 |
| 建议 `#646B76` / `#F3F4F6` | 4.88:1 | 适合浅灰侧栏的次要信息 |
| 建议 `#646B76` / `#E5EEFC` | 4.60:1 | 在浅色选中背景上仍有可读性 |
| 建议白字 / `#2463D4` | 5.51:1 | 满足普通小文字目标 |
| 建议 `#17253D` / `#8AB4FF` | 7.35:1 | 深色主题浅蓝按钮的文字组合 |
| 建议 `#8AB4FF` / `#1B1D21` | 8.08:1 | 深色内容背景上的焦点提示 |

普通信息文字以至少 4.5:1 为目标；必要的图标、焦点和状态指示以相邻背景至少 3:1 为目标。轻微分隔线和辅助 hover 底色不需要全部加深到 3:1。Apple 给出的 macOS 自定义字体默认参考为 13 pt；本项目正文维持 13 DIP，实际还要验证渲染缩放。[Apple Accessibility](https://developer.apple.com/design/human-interface-guidelines/accessibility/) [W3C Non-text Contrast](https://www.w3.org/WAI/WCAG22/Understanding/non-text-contrast.html)

### 5.3 选中、焦点、主要按钮必须分工明确

- 选中：淡蓝底 + 深色正文；用 1 DIP Accent 内边界补充辨识，布局尺寸不变化。
- 焦点：在对应控件外缘或内缘绘制 2 DIP `FocusRingBrush`，不依赖选中背景；选中且聚焦时二者同时成立。
- 非活动窗格中的选中：使用中性弱底色与可辨识边界，保留选中内容，不隐藏状态。
- 主要按钮：常态 Accent + ButtonPrimaryForegroundBrush；hover 使用 AccentHover；按下使用明确的主色深浅变化，不能悬停后退化成普通灰色按钮。
- 危险按钮：危险色体系覆盖自己的状态；不能套普通 hover 后失去危险语义。

实施时必须检查 [InteractionStyleService.cs](/Users/jiangxinji/Documents/FKFinder/Services/Impl/InteractionStyleService.cs)：当前 Selected 的桥接列表含 `FocusRingBrush`、`TextControlBorderBrushFocused`、`ComboBoxBackgroundBorderBrushFocused`。将这三者从 Selected 覆盖中解除，焦点资源独立维护。继续保留旧设置向 Selected 的迁移，避免丢失历史选中颜色；本轮不新增新的焦点设置页面或迁移体系。

同时修改实际用到 `InteractionSelectedBrush` 画焦点的选择器。只改资源文件不能修复这些直接引用。为主要按钮、图标按钮、工作区导航、输入框、分段控件验证最终模板。

## 6. 字体、圆角与图标

### 6.1 字体

沿用 [TypographyTokens.axaml](/Users/jiangxinji/Documents/FKFinder/Assets/TypographyTokens.axaml)、[TypographyService.cs](/Users/jiangxinji/Documents/FKFinder/Services/Impl/TypographyService.cs) 与已有字体预设。

| 用途 | 目标字号 / 字重 | 说明 |
|---|---|---|
| 列表文件名、普通按钮 | 13 / Regular | 不全局放大成网页正文 |
| 图标模式文件名 | 12 / Regular | 从 Caption 改为 Label，最多两行 |
| 列表日期、大小、类型 | 12 / Regular | 同类元数据统一，大小数值右对齐 |
| 侧栏条目 | 14 / 现有 Light | 保留 PingFang SC；选中可用 Medium，但不因加粗改变行宽 |
| 分组标题 | 11 / Medium | 使用 Secondary，避免极浅灰 |
| 面板标题 | 14 / Medium | 与普通字段形成一层差异即可 |
| 首页标题 | 18 / Medium | 只用于首页引导 |
| 设置页标题 | 20–24 / Semibold | 使用现有语义字号，不额外造字体系统 |
| 技术路径 / 预览代码 | 沿用 Mono | 不把全部文件名换成等宽字体 |

字号放大时保证按钮与输入框不裁字。当前普通文件行固定为 30 DIP，`FileListScrollAnchor.DetailsRowHeight` 也使用 30。本轮保持这个约束；若大字号实测确实裁切，必须作为同一个小任务同时修改实际行高、滚动定位公式和相关测试，不能只删除 XAML 的 `MaxHeight`。

### 6.2 几何与材质

- 普通交互圆角维持现有 `InteractionCornerRadius=10`；用户自定义继续有效。
- 小标签圆角 4–6；弹出菜单 10；对话框内容分组 10–12。
- 连续文件区域和侧栏的内部边界使用 0 圆角；窗口外轮廓沿用现有实现。
- 通常不为小按钮添加默认投影。菜单使用一层柔和阴影，建议浅色 `0 8 24 -8 #26000000`；深色配细边界，避免多层重阴影。
- 不把同一个控件的外框、内框、模板内容三层都各画一次阴影或边线。

### 6.3 图标分两类管理

**操作 / 导航图标：**

现有 [Icons.cs](/Users/jiangxinji/Documents/FKFinder/Assets/Icons.cs) 声明使用 24 × 24、fill path 的 Fluent 风格图标。继续复用这套资源，缺失图标再从同一体系补充；不新增 Lucide / Material 等第二套运行时依赖。可从官方资源核查同类符号，注意保留许可与来源信息。[Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons)

| 位置 | 画面尺寸 | 命中尺寸 | 色彩 |
|---|---|---|---|
| 导航、工具栏 | 16 × 16 | 32 × 32 起 | Secondary；激活 Accent |
| 侧栏 | 16 × 16 | 整行 | 普通 Secondary；当前位置 Accent |
| 菜单 | 16 × 16 | 行高至少 30 | 与菜单文字协调 |
| 信息面板关闭 / 展开 | 14–16 | 28 × 28 起 | Secondary；提供名称和提示 |
| 页签关闭 | 保持现有几何 | 至少 24 × 24；验证与拖拽不冲突 | 依照页签状态 |
| 首页快捷入口 | 24 × 24 | 约 136 × 44 | 单一中性色 / 主色 |

不要给 fill path 强行增加 `StrokeThickness` 来“统一线宽”；需要改善光学重量时，选择合适的同系列图形或调整显示尺寸。搜索、排序、布局、信息、设置、更多必须逐个在实际尺寸看清。

把 `AiView` 的字符 `✕` 等普通操作符号换成已有 `Icons.Close`。红黄绿系统按钮的专用符号和文件状态文字不纳入机械替换。信息面板 `ExpandPreviewIcon` 可迁移为 Icons 中具名资源，先检索是否已存在，避免重复。

**文件 / 应用 / 内容图标：**

- 保留 `NSWorkspace` / 当前原生解析、macOS 文件夹图、文件类型识别和缩略图。
- 不把它们统一染灰，也不覆盖应用品牌图标。
- 本轮保持列表文件图标约 18 DIP、网格主图约 56 DIP 和当前图标容器，避免干扰正在进行的缩略图与网格性能改动。
- 单次加载时近乎空白的缩略图只能作为待复现样本；不得据此重写图标解析或异步流水线。
- Git、远程状态和标签颜色属于业务语义，保留；补充 M / ? / 文本提示，不能只靠红绿区分。
- 网格文件名仍使用当前首尾 / 扩展名省略策略。完整名称通过 Tooltip 和辅助功能名称提供。不要本轮引入逐字符测量器、修改固定 13 字符规则或改变 120 DIP 网格列宽。

## 7. 控件状态规范

| 控件 | 默认 | Hover | Pressed / Open | Selected / Focus | Disabled |
|---|---|---|---|---|---|
| 普通图标按钮 | 透明、Secondary | Hover + 圆角 10 | Pressed；菜单开时保持提示 | 独立 2 DIP 焦点框 | 背景透明，沿用禁用透明度，不能触发 |
| 主要按钮 | Accent + ButtonPrimaryForegroundBrush | AccentHover | 主色按下状态 | 独立焦点框 | 与普通禁用规则一致 |
| 侧栏行 | 透明 | Hover | Pressed | 选中浅蓝 + 明确边界，聚焦另加焦点 | 仅真正不可用才禁用 |
| 列表 / 网格项 | 内容底色 | Hover | 不缩放整个文件卡 | Selected 与 Focus 可叠加 | 剪切淡化沿用现有独立语义 |
| 面包屑 | 透明 | 不加背景 | 沿用点击导航 | 编辑输入态有清晰焦点 | 无效段按既有导航逻辑 |
| TextBox | 稳定浅底 + 必要边界 | 不改变控件尺寸 | 编辑态清晰 | 独立焦点颜色 | 不用占位符冒充字段标签 |
| ComboBox | 与输入框同系列 | Hover | 弹层触发态 | 键盘定位清晰 | 不可操作 |
| 视图切换分段 | 一层弱容器 | 单项 Hover | 点击切换 | 当前项用选中底 + 图标状态，具有键盘焦点 | 不允许同时两项选中 |
| 颜色标签 | 小色点 + 提示 | 环形强调 | 不放大整行 | 勾选标记 + 轮廓 | 与真实编辑权限一致 |

Avalonia 落地注意事项：

1. Fluent 按钮的可见背景可能由 `/template/ ContentPresenter#PART_ContentPresenter` 绘制，必须检查模板最终效果。
2. ComboBox 弹层检查 `Border#PopupBorder`；保留当前选中项不在候选列表重复出现的既有行为，不借视觉修改改变它。
3. 滚动条绘制目标是 `Thumb`。轨道透明，视觉宽度沿用约 6–8；命中区不跟着无限缩小，两个方向均可操作。
4. 键盘焦点不能通过添加 BorderThickness 造成内容抖动；用预留边界或独立焦点装饰层。
5. 所有关闭、展开、清除、推出、布局、更多等图标按钮都应具有 `AutomationProperties.Name`。辅助名称描述动作，不使用“PathIcon”或“按钮”。
6. 具有动作的 Border 必须有等价键盘入口和语义。优先复用按钮 / 命令；若改成 Button，需要保留原有点击、拖拽和上下文菜单路由，避免双执行。

## 8. 信息面板、搜索、弹窗与反馈

### 8.1 信息面板

修改 [InfoPanelView.axaml](/Users/jiangxinji/Documents/FKFinder/Views/InfoPanelView.axaml) 及必要的展示代码：

- 标题区高至少 40，统一 16 横向留白；关闭和展开按钮对齐。
- 预览表面使用中性弱底色，正常图像完整显示；保持现有原生承载、展开预览、分页与滚动策略。
- 预览下方优先显示文件名（必要时两行）以及类型 / 大小摘要；随后才是详细字段。
- 字段统一为标签 12 / Secondary，值 12–13 / Primary 或 Secondary；日期不再使用低对比 Muted。
- 位置使用中间截断或既有截断 + 完整 Tooltip，保持可复制动作。
- “基本信息 / EXIF 信息”统一为 32 高的轻量分段控件；无 EXIF 的类型显示清晰的“无 EXIF 信息”，不出现含混空白。
- 七个标签的可见色点缩小到 12–14，外层命中区保持 24–28，间距 6；选中有勾，不把整个彩色圆盘做成面板最醒目的内容。
- 标签输入去掉多层卡片包裹。快速操作采用统一 32 高动作行和 16 图标，保留可发现性。
- 不为了预览框圆角修改 Quick Look / WebKit / AVPlayer 的渲染结构。

### 8.2 搜索职责和结果

目录页的面包屑负责位置与路径编辑；页内搜索和全局 `⌘K` 搜索保留已有服务职责。占位文字应在核查实际搜索范围后写成准确名称，不能把全局查询随意标成“当前文件夹”。

搜索结果显示查询词、实际范围、结果数量；“清除 / 返回原位置”调用当前 `ExitSearchAsync` / 恢复搜索来源逻辑。清除输入框必须恢复原位置和既有选择策略，不能偷偷导航到默认目录。

紧凑搜索复用已有展开按钮和 `_isPageSearchExpanded` 行为。新样式必须覆盖折叠、展开、Esc、跨窗格焦点和已有查询重新打开。

### 8.3 空、加载、失败、不可用

| 状态 | 主文案 | 次说明 / 动作 | 数据约束 |
|---|---|---|---|
| 空目录 | 此文件夹为空 | 可用时显示“新建文件夹” | 禁写位置不能提供假可用动作 |
| 无搜索结果 | 未找到匹配的文件 | 显示查询词和真实范围；清除搜索 | 不暗示未查询的位置也没有文件 |
| 初次加载 | 正在读取文件… | 沿用真实加载状态；轻量进度 | 无精确进度时不用假百分比 |
| 后台刷新 | 保持已有内容 | 只在局部显示刷新提示 | 不清空当前列表和选中态 |
| 未选择文件 | 选择文件以预览 | 支持时说明空格键预览 | 隐藏无意义的“—”字段堆叠 |
| 预览失败 | 暂时无法预览此文件 | “重试”与“使用默认应用打开” | 绑定真实能力；重试要使旧缓存 / 请求状态正确失效 |
| 类型不支持 | 暂不支持此格式的预览 | 显示文件图标、类型；可用时打开 | 不与生成失败共用错误原因 |
| 远程未连接 | 未连接 | 使用已有连接入口 | 不自动发起连接 |
| 读取失败 | 无法读取此位置 | 展示现有可理解错误摘要、重试 / 返回 | 不把权限或连接错误画成空目录 |
| AI 索引为空 | 还没有可搜索的识别内容 | 准确说明已有索引建立条件 | 不生成假识别结果，不默认开启 AI |

只基于已有状态显示文案。若某个分支尚无可区分状态，新增最小的展示状态映射或记录为待补充，不能虚构失败原因；不建设新的后台任务 / 事件框架。

### 8.4 设置与对话框

- [SettingsDialog.axaml](/Users/jiangxinji/Documents/FKFinder/Views/Dialogs/SettingsDialog.axaml)：保留现有设置导航；统一标题、分组、标签字号，去掉静态分组阴影，减少每一行都高亮强调。
- [RemoteConnectionDialog.axaml](/Users/jiangxinji/Documents/FKFinder/Views/Dialogs/RemoteConnectionDialog.axaml)：统一按钮圆角与字段间距；字段标签常驻；底部以“连接”为主要动作，“保存”为次要动作，“删除”与二者分开。
- [DeleteConfirmDialog.axaml](/Users/jiangxinji/Documents/FKFinder/Views/Dialogs/DeleteConfirmDialog.axaml)：明确文件名 / 数量和真实删除后果；主危险动作与取消对齐，不改删除语义。
- 压缩、批量重命名、密码弹窗与 TaskPanel 复用同类规范；任务列表保留真实进度和已有取消能力。
- 提示与错误放在相关字段附近，不能全部依靠消失很快的浮动通知。
- 不修改服务器、密码、密钥、删除方式、AI 开关、默认应用等设置值。

### 8.5 动效

保持已有短反馈：颜色 100–140 ms、按下 80 ms、轻量弹层 120–160 ms；不叠加大幅缩放、弹簧跳动或文件列表逐项入场。

文件列表、拖拽、滚动与原生预览不增加动画负担。已有 Reduce Motion 支持则遵循；若没有，先保持静态可用，不能声称已自动适配系统减少动态效果设置。

## 9. 实施入口与保护边界

| 模块 | 主要入口 | 允许修改 | 必须保护 |
|---|---|---|---|
| 颜色 | [ThemeTokens.axaml](/Users/jiangxinji/Documents/FKFinder/Assets/ThemeTokens.axaml) | 明暗默认值、语义与兼容资源 | 用户配置覆盖、运行时主题切换 |
| 交互 | [ComponentStyles.axaml](/Users/jiangxinji/Documents/FKFinder/Assets/ComponentStyles.axaml)、[Styles.axaml](/Users/jiangxinji/Documents/FKFinder/Assets/Styles.axaml)、[InteractionStyleService.cs](/Users/jiangxinji/Documents/FKFinder/Services/Impl/InteractionStyleService.cs) | 状态、模板、焦点桥接 | 既有配置键和普通 Hover 例外 |
| 字体 | [TypographyTokens.axaml](/Users/jiangxinji/Documents/FKFinder/Assets/TypographyTokens.axaml)、[TypographyService.cs](/Users/jiangxinji/Documents/FKFinder/Services/Impl/TypographyService.cs) | 控件消费的语义字号 | Small / Standard / Large 与 CJK 实际字形 |
| 外壳 / 页签 | [MainWindow.axaml](/Users/jiangxinji/Documents/FKFinder/Views/MainWindow.axaml)、[WindowTitleBar.axaml](/Users/jiangxinji/Documents/FKFinder/Controls/WindowTitleBar.axaml)、[ChromiumTabStripSurface.cs](/Users/jiangxinji/Documents/FKFinder/Controls/ChromiumTabStripSurface.cs) | 配色、按钮状态和可访问名称 | 曲线缓存、连续衔接、命中测试、活动页签 |
| 工作区 | [ExplorerWorkspaceView.axaml](/Users/jiangxinji/Documents/FKFinder/Views/ExplorerWorkspaceView.axaml)、[ExplorerWorkspaceView.axaml.cs](/Users/jiangxinji/Documents/FKFinder/Views/ExplorerWorkspaceView.axaml.cs)、[ResponsiveWorkspaceLayout.cs](/Users/jiangxinji/Documents/FKFinder/Controls/ResponsiveWorkspaceLayout.cs) | 表面、间距、默认侧栏宽、首页显隐 | 多窗格、活动窗格、紧凑搜索、预览宽度 |
| 侧栏 / 工具栏 | [FinderSidebarView.axaml](/Users/jiangxinji/Documents/FKFinder/Views/FinderSidebarView.axaml)、[FinderToolbar.axaml](/Users/jiangxinji/Documents/FKFinder/Views/FinderToolbar.axaml) | 分组、图标色、几何、状态 | 拖放、收藏编辑、折叠、禁用状态、原事件 |
| 列表 / 网格 | [FileListView.axaml](/Users/jiangxinji/Documents/FKFinder/Views/FileListView.axaml) 及同名 code-behind | 字号、前景、选中 / 焦点、对齐 | 30 行高、120 网格列宽、虚拟化与选择 |
| 首页 / AI | [HomeView.axaml](/Users/jiangxinji/Documents/FKFinder/Views/HomeView.axaml)、[AiView.axaml](/Users/jiangxinji/Documents/FKFinder/Views/AiView.axaml) | 首页入口、空态、图标、一致性 | Omnibox 和 AI 数据行为 |
| 预览 / 信息 | [InfoPanelView.axaml](/Users/jiangxinji/Documents/FKFinder/Views/InfoPanelView.axaml)、[SuperPreviewView.axaml](/Users/jiangxinji/Documents/FKFinder/Views/SuperPreviewView.axaml) | 标题、字段、标签、按钮和状态呈现 | Quick Look、WebKit、AVPlayer、远程本地化 |
| 菜单 | [ContextMenuView.axaml](/Users/jiangxinji/Documents/FKFinder/Views/ContextMenuView.axaml)、[ContextMenuPopupStyler.cs](/Users/jiangxinji/Documents/FKFinder/Views/ContextMenuPopupStyler.cs) | 菜单表面与状态 | 先确认实际可见菜单由原生还是 Avalonia 分支绘制 |

下列文件是依赖核查点，不是默认修改目标：

- [FileListScrollAnchor.cs](/Users/jiangxinji/Documents/FKFinder/Services/FileListScrollAnchor.cs) 与 [FileListView.Snapshots.cs](/Users/jiangxinji/Documents/FKFinder/Views/FileListView.Snapshots.cs)：固定行高与滚动位置。
- [FileListColumnLayoutService.cs](/Users/jiangxinji/Documents/FKFinder/Services/Impl/FileListColumnLayoutService.cs)：列宽缩减、最小宽和持久化。
- [FileIconConverter.cs](/Users/jiangxinji/Documents/FKFinder/Converters/FileIconConverter.cs)、[FileIconResolver.cs](/Users/jiangxinji/Documents/FKFinder/Services/Impl/FileIconResolver.cs)：文件图标与内容识别。
- [FileThumbnailSizing.cs](/Users/jiangxinji/Documents/FKFinder/Services/FileThumbnailSizing.cs)、[LivePreviewCoordinator.cs](/Users/jiangxinji/Documents/FKFinder/Views/LivePreviewCoordinator.cs)：缩略图尺寸和活动预览生命周期。

## 10. AI 可直接执行的任务清单

按顺序完成；每阶段结束留下具体变更与验证结果。默认只做到本地实现和验证，不自动升级版本、提交、推送或发布。

### 阶段 0：确认基线

- [x] 读取项目约束、当前 Git 状态和上述相关文件；记录开始时已存在的改动。
- [x] 检查本方案中的问题是否已经被其他改动处理，确认每项实际渲染入口。
- [x] 选择与当前源码对应的隔离 QA 输出目录和独立 `MACEXPLORER_DB_PATH`；不覆盖正在运行的应用包。
- [x] 使用脱敏样例目录准备中文、英文、长文件名、图片、普通文档、代码和空文件夹；不把真实远程地址写入公开截图。
- [x] 保存一套当前实现的浅色 / 深色基线图；本任务不需要连接真实服务器。

### 阶段 1：P0 可读性与焦点

- [x] 按第 5 节改主题颜色及必要的兼容资源，保留自定义覆盖。
- [x] 解除 Selected 与 Focus 的资源桥接，调整直接用选中底色画焦点的样式。
- [x] 修复主要按钮 Hover 与 Pressed 的颜色系列；保持普通按钮的透明默认状态。
- [x] 统一信息文字角色；列表正文保持 13，图标名使用 12。
- [x] 补齐关键图标按钮的名称和键盘可达性。
- [x] 验证明暗主题、保存过的选中颜色、Tab 焦点和主要按钮四种状态。

### 阶段 2：P1 主工作区布局

- [x] 去掉工作区内部三块浮卡的重复阴影、圆角与缝隙，建立连续阅读表面。
- [x] 标题栏改中性底色，保持 Chromium 连续曲线和标题栏命中行为。
- [x] 普通侧栏默认宽调整为 240，紧凑宽与断点沿用已有机制。
- [x] 工具栏动作重新分组对齐，查看动作右对齐，紧凑溢出功能完整。
- [x] 首页减少重复输入，隐藏无意义文件操作与“0 项”；补齐首页焦点路由。
- [x] 验证 1000、1280、1600 宽度和现有多窗格布局，不出现按钮重叠或不可达操作。

### 阶段 3：P1 图标与文件呈现

- [x] 侧栏操作图标使用主题中性色，选中 Accent；保留标签与真实状态色。
- [x] 统一导航和工具图标至 16，修复过小的关闭 / 展开命中区。
- [x] 迁移重复原始路径或字符操作图标，复用已有 Icons。
- [x] 文件名、元数据、表头对齐；大小右对齐时同步表头和列内边距。
- [x] 列表、分组、网格的 hover / selected / focus 一致；不改变列表和网格几何常量。
- [ ] 验证多选、框选、重命名、列拖拽、滚动后刷新、缩略图异步更新。

### 阶段 4：P1 / P2 面板与外围控件

- [x] 整理信息面板标题、字段、标签和快速操作；保持原生预览承载。
- [x] 将空、加载、失败、未连接等状态映射为准确呈现；重试只调用现有能力。
- [ ] 统一设置、远程连接、删除、压缩、重命名等弹窗的字段、按钮和层次。
- [ ] 检查 ContextMenu / Popup 的真实渲染分支、ComboBox 和双向 ScrollBar。
- [ ] 检查原生预览里的浮动按钮与滚动区域，没有遮挡和命中回归。

### 阶段 5：验收与交付

- [ ] 完成第 11 节视觉矩阵与必要回归。
- [x] 输出真实前后对比图、完成条目、未验证条目和剩余问题。
- [x] 说明哪些是方案新增改动，哪些是原有工作区改动；不把他人改动归入本次成果。
- [x] 未经另行要求不改版本、不提交、不推送、不打发布包。

## 11. 验收标准

### 11.1 必须提供的视觉证据

实施后图像保存至独立的 `artifacts/ui-visual-review-2026-09-07/` 或等价 QA 目录，不纳入生产资源。每张图注明窗口 DIP、渲染缩放、主题、字体预设、是否为默认配置。

| 场景 | 最少覆盖 | 通过标准 |
|---|---|---|
| 首页 | 浅 / 深，1280 × 800 | 一个主输入、无多余文件操作、快捷入口和说明清晰 |
| 普通列表 | 浅 / 深，1280 × 800 | 表头与列内容对齐；日期可读；选中 / hover 不混淆 |
| 图标网格 | 浅 / 深，中文与长名 | 主体图标清晰、文件名 12 DIP 不挤压、扩展名与完整提示可访问 |
| 信息面板 | 文件选中、无选择、预览失败 | 预览为主，按钮可识别，错误有准确含义 |
| 紧凑窗口 | 1000 × 680 | 导航、搜索与溢出入口不重叠、不丢失 |
| 多窗格 | 当前支持的两窗格及最密布局 | 活动窗格清楚，搜索 / 侧栏 / 预览仅作用于对应窗格 |
| 页签 | 单页签、多个页签、滚动到末尾 | 连续曲线无白缝，关闭按钮可用，拖拽不误触导航 |
| 输入 / 菜单 | 1 个 TextBox、ComboBox、上下文菜单 | hover / focus / selected 状态均清楚，弹层边界一致 |
| 字体预设 | Small / Standard / Large | 无裁切、重叠；CJK 实际字重符合预期 |
| 键盘与焦点 | Tab、Shift+Tab、Enter、Esc | 焦点可见，进出菜单和预览后能回到合理位置 |

原生预览另做代表性人工或实机操作验证：PDF、Office、多页内容、Markdown / HTML、视频；远程场景使用可控样例或测试替身验证本地化路由，不能在没有证据时声称真实远程预览已通过。

### 11.2 必要回归检查

在当前实际输出路径安全的前提下，参考命令为：

```bash
dotnet build MacExplorer.csproj --no-restore
dotnet test Tests/MacExplorer.Tests/MacExplorer.Tests.csproj --no-restore
git diff --check
```

**构建前先确认输出位置**：本项目 Build 会创建 / 替换 `.app`。若默认输出已有运行中的实例，改用独立输出目录并验证独立 QA 包，不能直接照抄上面的构建命令覆盖它。

重点复用以下现有测试，具体类名和覆盖范围以执行时文件为准：

- `InteractionStyleServiceTests`：更新“Selected 同时改变 Focus”的旧断言；验证自定义选择色不会覆盖独立焦点色，旧配置迁移仍可用。
- `TypographyTests`：资源与字号预设；视觉字重仍需 macOS 截图。
- `AppWindowTests`、`ChromiumTabStripSurfaceTests`、`TabStartupTests`：页签与窗口行为。
- `ExplorerWorkspaceViewTests`、`ResponsiveWindowLayoutTests`、`PaneLayoutIconTests`：工作区布局与紧凑行为。
- `FileListViewLayoutTests`、`FileListColumnLayoutServiceTests`、`FileListScrollAnchorAndSizingTests`：几何和滚动约束。
- `ComboBoxStyleTests`、`ScrollBarStyleTests`：真实模板关键目标。
- `LivePreviewCoordinatorTests`、`FilePreviewRegressionTests`：预览生命周期与回归。

只为新增行为写必要测试，例如首页快捷键焦点路由、Focus 与 Selected 解耦、控件显隐和响应式条件；不为每个颜色值写一份镜像式测试。

构建通过、单元测试通过与真实 UI 通过是不同结论。不得用前两者替代后者，也不得把旧实例截图当作修改后的效果。

### 11.3 完成定义

- 主体视觉与第 4–8 节一致，没有同类控件各自发挥的配色 / 圆角。
- 没有丢失原导航、选择、搜索、快捷键、拖拽、编辑和预览行为。
- 默认明暗配色满足文字与必要状态辨识要求；自定义外观继续生效。
- 文件列表虚拟化、滚动锚点与缩略图加载仍然正常；不凭视觉优化声称性能提升。
- 文档任务表真实标记：完成、未完成、未验证分开记录。
- 不将未经实测的设置页、深色外观或原生预览标成已通过。

## 12. 可直接交给执行 AI 的提示词

将下面内容与本文件一起交给负责实施的 AI：

```text
请在 /Users/jiangxinji/Documents/FKFinder 中实施
docs/UI_VISUAL_REDESIGN_AI_PLAN_2026-09-07.md 的视觉修改方案。

先完整读取该方案、项目约束、当前工作区差异及实际渲染入口。
本项目使用 Avalonia 12.0.4 和 .NET 10。当前工作区有未提交的页签、
文件列表、缩略图与预览改动。保留已有工作，不能 reset、回滚、覆盖
或恢复已经被替换的旧实现。以当前代码为准，已完成的方案条目验证后跳过。

目标是中性、清晰、紧凑的 macOS 文件工作台：
1. 按方案调整明暗主题与文字可读性，将键盘焦点和选中背景解耦。
2. 整理工作区表面、侧栏、工具栏和首页输入层级。
3. 统一现有 Fluent 操作图标和控件状态，保留原生文件 / 应用图标。
4. 整理信息面板、表单、菜单、空态与失败态。
5. 按方案完成代表性实机视觉验证和必要回归测试。

按阶段 0 → 1 → 2 → 3 → 4 → 5 执行，优先作最小、可验证的修改。
不要更换主题库，不要新建一套设计系统，不要复制搜索服务，
不要新增无数据来源的卡片、装饰动画或状态。
保留页签连续曲线、标题栏拖拽、紧凑布局、搜索焦点、用户自定义设置。
普通列表行高 30 与网格列宽 120 本轮不变；若发现必须改变，
同步核查滚动 / 选择 / 框选几何，完成相关行为验证，不只改 XAML。
不要改文件加载快照、远程服务、Quick Look / WebKit / AVPlayer 的架构。

截图和测试使用明确对应本次源码的独立 QA 包与测试数据。
不要覆盖正在运行的 .app，不连接真实服务器，不操作用户文件作为测试数据。
不能只凭 build 或 headless 测试声称视觉完成。

完成后提供：修改摘要、方案条目状态、实际前后对比截图、测试结果、
未验证场景和剩余问题。发现本方案与最新源码不一致时，先定位等价入口，
在保持方案目标和原功能的前提下修正实施细节，并说明调整理由。
本次授权为本地 UI 实施与验证；除非用户另外要求，不升级版本、提交、
推送、合并或发布。若实际环境阻止某项验收，准确标为未验证，不能伪造完成。
```

## 13. 参考来源与取舍

来源在 2026-09-07 查询。以下用于设计原则和可读性判断，具体配色与实现范围由本文件定义。

- [Apple Liquid Glass](https://developer.apple.com/documentation/technologyoverviews/liquid-glass)：导航与控件的材质和克制用色；本方案仅提取层级原则，不要求 Avalonia 模拟完整系统玻璃效果。
- [Apple Accessibility](https://developer.apple.com/design/human-interface-guidelines/accessibility/)：macOS 字体尺寸参考与明暗外观的辨识要求。
- [Apple Typography](https://developer.apple.com/design/human-interface-guidelines/typography)：字号、字重和信息层级。考虑用户已选择侧栏轻字重，本轮保留该偏好，优先改善颜色和选中层级。
- [Apple Designing for macOS](https://developer.apple.com/design/human-interface-guidelines/designing-for-macos/)：桌面使用习惯和可定制能力。
- [W3C Non-text Contrast](https://www.w3.org/WAI/WCAG22/Understanding/non-text-contrast.html)：必要控件 / 状态的对比度，以及装饰性边界和辅助 hover 的区别。
- [Microsoft Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons)：现有操作图标体系的核查来源。

本次没有生成概念效果图。布局图是结构规格；真正的视觉完成以当前代码实施后的实机截图与代表性交互为准。
