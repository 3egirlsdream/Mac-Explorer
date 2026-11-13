# 公共 ComboBox 与标准下拉菜单验证

2026-11-12：新增 `Controls/AppButton.cs` 中的 `AppComboBox`，将应用内 10 处 XAML、8 处 C# 声明迁入公共入口。字段、图标选择器、选项、四档高度和弹出表面统一由 `Assets/ButtonStyles.axaml` 提供，移除 `SettingsStyles.axaml` 中的 ComboBox 局部覆盖。默认／compact／small／large 高度分别为 32／28／24／36px。

组合按钮的 MenuFlyout 使用全局 `StandardMenuFlyoutPresenterTheme`，与右键菜单、ComboBox 下拉复用 `context-submenu-surface` / `PopupGlass`，统一背景、圆角、内容内边距和阴影外边距。冲突弹窗不声明独立的下拉外观。

验证结果：

- 源码检查无违规；扫描器自身 6 项测试通过。
- `fast --no-build`：1241 通过，2 项渠道相关跳过，0 失败。
- `ui --area Buttons --area InputAppearance --area PopupGlass`：33 项通过。
- 完整 `ui --no-build`：740 项通过。
- Debug 应用构建通过，`git diff --check` 通过。
- 隔离 macOS 实例：44 项窗口及设置页检查通过，84 张组件／页面截图；新增四语、浅深主题、大字号下四档 ComboBox 及展开面板截图。
- 冲突弹窗：13 项既有比例及操作检查通过，额外保存四语浅深主题菜单展开截图，并断言每个菜单使用标准表面。

证据：`outputs/shared-combobox-20261112/`。按钮／ComboBox 图集位于 `components/`，冲突菜单截图位于 `file-conflict/`，其余包含源码扫描和自动测试摘要。
