# 固定按钮高度验证（2026-11-12）

全局 `Assets/ButtonStyles.axaml` 将普通操作按钮固定为默认 32、compact 28、small 24、large 36px。primary、secondary、ghost、danger 共用尺寸规则，AppToggleButton 与次要操作 AppSplitButton 同步适配。上下内边距为 0，内容水平、垂直居中；工具栏、窗口控制和文件卡片等专用角色继续由全局角色样式定义。

重复文件弹窗的默认尺寸同步调整为 616×380px，使用 32px 全局操作按钮，面板与按钮的比例通过设计图断言。

验证结果：

- `fast`：1240 通过、2 个渠道相关跳过、0 失败。
- 完整 `ui`：738 通过、0 失败；最终专用角色调整后追加 Buttons、ContextMenu、Menu、FileConversion、BackgroundTheme 回归，48 通过。
- 按钮源码扫描：235 个 XAML、66 个代码声明、3 个 SDK 示例，0 违规；扫描器自身 5 项测试通过。
- macOS 隔离实例：四种语言、浅深主题、三档字号，24 张按钮矩阵截图及 768 个按钮的实际几何断言全部通过，实测高度只有 24、28、32、36px，文字与图标居中且无裁切。
- 原生窗口覆盖：44 项窗口／设置页检查、68 张截图，报告无错误。
- 重复文件弹窗：四语、浅深主题、窄窗口、长文件名及操作结果共 13 项检查通过；中文默认面板 616×380px、主要按钮 92×32px。
- Debug 构建通过，`git diff --check` 通过。

证据位于 `outputs/fixed-button-heights-20261112/`，包含测试摘要、源码扫描结果、`button-components/button-sizes.json`、原生窗口报告及截图、`file-conflict/report.json`。
