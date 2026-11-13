# 全局按钮组件

应用界面统一使用 `Controls/AppButton.cs` 的 `AppButton`、`AppToggleButton`、`AppSplitButton`、`AppComboBox`。它们保留 Avalonia 原生控件的命令、点击、选择、键盘和无障碍行为，按钮保留默认／取消操作，通过原生 StyleKey 接入统一主题。

按钮与下拉选择的外观只在 `Assets/ButtonStyles.axaml` 定义，由 `ComponentStyles.axaml` 全局加载。不要在页面添加按钮主题、模板、颜色、边框、圆角、字体、内边距或固定高度，也不要在代码创建按钮后再次设置这些属性。页面可以设置文本、图标、命令、事件、可用／显示状态及位置、外边距、最小宽度等布局。

| 用途 | Classes |
| --- | --- |
| 主要操作 | `primary` |
| 次要操作 | `secondary` |
| 透明操作 | `ghost` |
| 危险操作 | `danger` |
| 紧凑文字操作 | 上述类型加 `compact` |
| 无表面图标操作 | `icon-action` |
| 小型行内操作 | 上述类型加 `small` |
| 标准对话框操作 | 上述类型加 `dialog-action` |
| 分段文字选项 | `segment-btn`，选中加 `active` |
| 带下拉的次要操作 | `AppSplitButton` + `secondary` |

工具栏、面包屑、文件列表表头、页签关闭、窗口红黄绿按钮、设置行、首页文件项、标签和 Copilot 等专用角色也集中在同一文件。专用角色用于特定交互，不复制另一套基础按钮外观；下拉组合按钮直接组合 `AppButton.secondary` 和 `AppButton.icon-action`。普通透明表面按钮共享 `SurfaceButtonTheme`，滑块／数字输入内部的 `RepeatButton` 保留框架原语。

新增按钮优先选择现有角色。确需新增角色时，在 `ButtonStyles.axaml` 定义，并补充本页；不要在业务页面覆盖已有角色。

`python3 Tools/Testing/check-buttons.py` 扫描应用的 XAML 和 C#，检查组件入口、页面局部样式以及实例外观覆盖。统一测试入口的所有配置均执行该检查。框架和第三方组件内部自行生成的按钮不属于应用声明的按钮，应用的 Markdown 复制入口通过现有 `icon-action` 接入主题。

验证：`bash Tools/Testing/run-tests.sh fast`、`bash Tools/Testing/run-tests.sh ui`。原生窗口覆盖使用 `MACEXPLORER_WINDOW_OPEN_SMOKE=1` 隔离启动，冲突弹窗使用 `MACEXPLORER_FILE_CONFLICT_SMOKE=1`。

独立插件由主程序的 `PluginWorker.WorkerApplication` 创建 Avalonia 宿主，同样加载字体、主题色及 `ButtonStyles.axaml`。SDK 示例保持兼容已发布的 Avalonia `Button` API，通过 primary／secondary 等同一组全局变体接入，不引用主程序控件类型，也不定义插件页面按钮样式；扫描器同时检查这些声明。

## 固定高度

普通按钮与 ComboBox 共用 `InteractionCornerRadius`，默认圆角为 8px；胶囊、圆形和其他专用角色保留各自的语义圆角。

| 尺寸 | 高度 | 水平内边距 | 标准字号 |
| --- | --- | --- | --- |
| 默认 | 32 | 12 | 13 |
| compact | 28 | 9 | 12 |
| small | 24 | 8 | 12 |
| large | 36 | 12 | 13 |

四种外观 primary／secondary／ghost／danger 使用相同的尺寸定义；AppToggleButton 和次要操作 AppSplitButton 同步使用。普通操作按钮不叠加上下内容内边距，内容由模板在扣除对称边框后的空间中居中，避免字体行高和边框撑高或裁切。字体随全局字号预设变化，宽度继续按内容及水平内边距计算。

## 下拉选择与菜单

应用声明的 ComboBox 统一使用同一组件文件中的 `AppComboBox`，保留原生选择、绑定、键盘、图标与数据模板行为。字段、四档高度、图标选择器及下拉选项样式集中在 `ButtonStyles.axaml`，配置页面使用 `compact`，透明分组字段追加 `settings-inline-input`，不在 `SettingsStyles.axaml` 或实例中覆盖外观。

ComboBox 下拉、组合按钮的 MenuFlyout 和右键菜单共用 `context-submenu-surface` / `PopupGlass`：相同背景、圆角、6px 内容内边距和 12px 阴影外边距。MenuFlyout 通过全局 `StandardMenuFlyoutPresenterTheme` 使用标准菜单表面，业务页面只提供菜单项和事件。源码扫描同时禁止直接声明原生 ComboBox 以及页面局部样式。
