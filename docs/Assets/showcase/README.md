# 官网界面素材

当前官网以 **Mac Explorer 1.0.60** 为参考，2026-10-21 从当前源码重新构建，并通过 `Tools/Testing/run-isolated.sh` 使用独立测试目录采集。原始参考图：

`outputs/pages-redesign/latest-list-1.0.60.png`（1280 × 800）。

`latest-document.webp`、`latest-folder.webp`、`latest-lake.webp` 和 `latest-archive.webp` 为该次截图中文档、文件夹、图片与压缩包图标的裁切。HTML 中的用户名、测试目录路径替换为通用展示名称。

文件列表按当前截图的布局、30px 行高及主题色重现。右键菜单内容依据当前 `ViewModels/FileListViewModel.cs`、`ViewModels/FileListViewModel.Conversion.cs`、`Views/FileListView.axaml.cs` 和 `Assets/ComponentStyles.axaml` 的菜单结构与样式制作，展示 Markdown 编辑、文件操作与格式转换。窗口与菜单由 `Tools/Website/build.py` 生成，图标 UI 使用 `Assets/icons/fluent.svg`。

本目录不包含访问者文件或真实用户目录内容。

## 首页主题堆叠展示

首页保留上述文件列表与两级菜单结构，以五层等比例窗口向左上方退叠。前景使用“灵动玉兔”，后方依次为“龙行绚烂”“妙手生春”“幻彩机核”“夏绿成荫”。装饰窗口不进入无障碍树，前景说明在四语 `browserAlt` 中维护。

`themes/*.svg` 由 `Tools/Website/build.py` 从 `Assets/BackgroundThemes` 同步，不单独编辑；保持源图比例，按窗口高度等比铺排，图案强度 35%。配色取自 `Models/BackgroundThemePreset.cs`；组件使用透明表面，右键菜单与子菜单复用应用主题模式 65% 白色表面和 8px 模糊，文字与图标不整体降低透明度。

背景素材为用户提供的 QQ 参考改作，不按代码许可证重新授权；来源、改作说明和 SHA-256 记录见仓库 `Assets/BackgroundThemes/README.md` 与 `sources.json`。

前景窗口加入工具栏收藏夹／颜色标签及右侧信息面板，面板展示选中文档的类型预览、属性、标签和快捷操作；列表为面板预留独立列，两级菜单保持在列表区域。面板、预览和标签输入使用主题透明表面与 8px 轻磨砂，字段分隔线为约 9% 黑色；新增说明通过四语 `browserDemo` 维护。

标题栏保留无纹理渐变，单层裁切为 124px 激活页签留出圆角开口；页签直接透出同一个窗口背景层，不复制渐变或纹理，避免缩放拼接细线。
