# 界面语言文件

本目录是应用本地化文案的唯一来源。`*.json` 在每次构建时自动发现，设置中的语言列表按 `order`、`id` 排序；“跟随系统”始终在第一项。新增语言无需修改枚举、设置 XAML 或本地化服务。

语言文件示例（`fr.json`）：

```json
{
  "id": "fr",
  "nativeName": "Français",
  "culture": "fr-FR",
  "order": 3,
  "fallback": "en",
  "strings": {
    "settings.language": "Langue d’affichage",
    "settings.version": "Version {0}"
  }
}
```

- 文件名必须与 `id` 一致，`culture` 使用 .NET 支持的文化名称；`nativeName` 用该语言自身的名称显示。
- 新语言允许部分翻译，未翻译词条沿 `fallback` 回退。默认回退英文，再回退简体中文；未知 key 原样返回。删除已选语言后会恢复跟随系统。
- 已发布的简体中文、繁体中文、英文和法语保持完整、key 一致；新增或修改文案同时更新 `zh-CN.json`、`zh-Hant.json`、`en.json` 和 `fr.json`。参数占位符（例如 `{0}`、`{1}`）必须一致。
- `zh-CN.json` 的 `literalKeys` 保留现有 `LocalizationText.Get("中文")` 调用的映射；新增以中文原文查询的词条时，将其 key 加入该列表。XAML 的 `loc:Loc` 和直接按 key 查询无需此步骤。
- 可选 `aliases` 列表声明地区语言别名；例如 `zh-Hant.json` 将 `zh-TW`、`zh-HK`、`zh-MO` 映射到繁体中文。别名冲突会使构建失败。
- 系统语言优先精确匹配 id／culture／aliases，再查找父语言；不支持时沿用原有简体中文默认值。旧设置 `system`、`zh`、`zh-CN`、`en`、`en-US` 继续兼容。

`Languages/Markdown` 存放 Markdown 控件的补充翻译，与主界面语言目录分开，随现有 I18n 资源打包。内置文件转换的跨进程文案由插件自身按请求语言加载。

## 构建与性能

`Tools/Localization/generate_catalog.py` 使用 Python 3（无第三方依赖）将语言文件编译到 `obj/.../LocalizationCatalog.g.cs`，只在内容变化时改写生成文件。重复 key、损坏 JSON、缺失基础文案、无效占位符和循环回退会使构建失败。

本目录的语言加载在应用启动、打开设置和创建界面时均不扫描目录、不读取语言文件、不解析 JSON；设置列表只使用轻量语言元数据。各语言字典及文化信息独立延迟初始化并在进程内复用，当前语言由服务缓存，界面查询直接访问内存字典。切换仍通过原有通知更新各窗口、菜单、日期和数字格式。

**新增或修改文件后需要重新构建应用**；这不是在已安装应用中投放文件的热加载目录，运行时不启动文件监听器。

验证：

```bash
python3 -m unittest discover -s Tools/Localization -p 'test_*.py'
bash Tools/Testing/run-tests.sh fast
bash Tools/Testing/run-tests.sh ui --area Localization
```
