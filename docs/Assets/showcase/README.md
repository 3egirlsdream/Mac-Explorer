# 官网界面素材

当前官网以 **Mac Explorer 1.0.60** 为参考，2026-10-21 从当前源码重新构建，并通过 `Tools/Testing/run-isolated.sh` 使用独立测试目录采集。原始参考图：

`outputs/pages-redesign/latest-list-1.0.60.png`（1280 × 800）。

`latest-document.webp`、`latest-folder.webp`、`latest-lake.webp` 和 `latest-archive.webp` 为该次截图中文档、文件夹、图片与压缩包图标的裁切。HTML 中的用户名、测试目录路径替换为通用展示名称。

文件列表按当前截图的布局、30px 行高及主题色重现。右键菜单内容依据当前 `ViewModels/FileListViewModel.cs`、`ViewModels/FileListViewModel.Conversion.cs`、`Views/FileListView.axaml.cs` 和 `Assets/ComponentStyles.axaml` 的菜单结构与样式制作，展示 Markdown 编辑、文件操作与格式转换。窗口与菜单由 `Tools/Website/build.py` 生成，图标 UI 使用 `Assets/icons/fluent.svg`。

本目录不包含访问者文件或真实用户目录内容。
