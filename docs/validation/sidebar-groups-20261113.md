# 侧栏自定义分组验证（2026-11-13）

实现：侧边栏设置末尾增加自定义分组及添加图标；紧凑命名弹窗支持添加、重命名和原位错误提示，删除需确认。自定义分组按创建顺序显示在 AI 智能之后，空组保留拖入区域；标题、固定目录行和取消固定操作复用现有侧栏样式及共享模板。四语文案齐全，用户名称保持原样。

固定关系按“分组＋路径”唯一，旧记录迁入常用位置，保留 ID、名称、顺序和时间。组内重复固定不产生副本，多组互不影响；删除分组仅移除快捷入口。目录路径同步覆盖各组；搜索及移动到候选覆盖各组并按路径去重。服务变更同步各窗口和页签，过期读取不回写，释放页面时解除订阅。

## 自动检查

- `bash Tools/Testing/run-tests.sh fast`：1271 通过、2 个渠道 Skip、0 失败。
- `bash Tools/Testing/run-tests.sh ui --area Sidebar --area Localization --area Window`：76/76 通过。
- `python3 -m unittest discover -s Tools/Localization -p 'test_*.py'`：7/7 通过。
- 全局按钮检查：0 违规；`git diff --check` 通过。

新增服务测试覆盖真实旧结构迁移、重复初始化、重启保留、跨组独立固定／取消、删除后拒绝再固定、名称校验、路径同步和组内顺序隔离。

新增 UI 测试覆盖标题及空白处多文件夹拖入、重复拖入、混合文件拒绝、目录行移动、窄侧栏与图标栏切换、跨 ViewModel 同步、搜索／移动到去重、设置按需创建、重复名称原位报错、添加／改名／取消删除／确认删除，以及语言切换保留用户名称。现有 600ms 悬停与最终落点回归继续通过。

## 真实应用隔离验证

```bash
dotnet build MacExplorer.csproj -c Debug -p:SkipMacOSReleaseDMG=true
MACEXPLORER_SIDEBAR_GROUPS_SMOKE=1 bash Tools/Testing/run-isolated.sh \
  'bin/Debug/net10.0/osx-arm64/Mac Explorer.app/Contents/MacOS/MacExplorer'
```

新构建的 macOS 应用在独立测试目录中运行，13 项检查全部通过；检查设置按需加载、实际添加／重命名交互、四语操作名称、多页签刷新、多组独立固定、取消固定隔离及删除后原目录和内容保留。

截图已检查：浅色侧栏、深色 180px 窄侧栏、命名弹窗、四语浅／深色设置。截图与原生报告位于 `outputs/sidebar-groups-20261113/native/`；自动检查摘要及构建日志位于同级目录。所有测试文件及配置位于临时测试目录，未使用真实用户目录。原生检查通过按钮事件和服务操作驱动；拖放由 Avalonia UI 测试发送拖放事件覆盖，未进行人工鼠标拖拽验收。

## 间距修正

命名行改由 `Border.settings-compact-row` 承载，恢复全局 12px 行内边距；设置中添加按钮紧邻“自定义分组”名称，使用 24px `icon-action small` 和 12px Fluent 加号。复核命名行留白、标题与图标居中及四语浅／深色设置；新截图位于 `outputs/sidebar-groups-20261113/native-spacing/`。本次相关 UI 检查 44/44 通过，fast 1271 通过、2 Skip，原生隔离检查通过。

## 自定义分组收起／展开

自定义分组标题与箭头复用 AI 智能的 `sidebar-section-header` / `sidebar-collapse-icon`，标题点击或 Enter／Space 切换，箭头随状态旋转。各组独立记忆状态并跨窗口同步，默认展开；重命名、固定目录刷新及重启不重置状态。收起时只隐藏目录和空白区，标题仍可拖入固定文件夹，拖入后保持收起。

本轮 fast 1271 通过、2 个渠道 Skip；Sidebar／FileDelivery／Localization UI 检查 55/55 通过。新增 UI 检查覆盖浅深色、空组独立开合、键盘、状态恢复、多 ViewModel 同步和收起标题拖入。真实 macOS 原生鼠标检查 16 项通过：连续拖入期间保持收起，Escape 取消不添加入口，最后实际点击标题展开。两种状态截图、报告和测试摘要保存在 `outputs/sidebar-group-collapse-20261113/`。运行入口仍为 `MACEXPLORER_DELIVERY_SMOKE=native-drag` 加 `run-isolated.sh`。
