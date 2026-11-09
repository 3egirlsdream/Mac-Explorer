# Issue #19 核实与工作组预设方案

## 本次确认的产品方向

启动仍然恢复上次会话。工作组预设是独立的手动入口：保存一组常用目录与布局，之后点击预设，在当前窗口新增并展开一个工作组。

本修订替代此前“固定启动工作区”的草案，不添加启动来源选择，也不保存所有窗口为启动模板。设置中的恢复开关和自动退出保存规则保持原样。

## 2.0.1 核实结论

核对基线为 Mac Explorer 2.0.1（`13449f0851de9334b9a149400a4b87415a92c818`），开发基线为 main `d3806e623c66e6f85436a37f72d60dab6aef3377`。其间两次提交为官网改动，相关应用源码一致。核对日期：2026-11-09。

[Issue #19](https://github.com/3egirlsdream/Mac-Explorer/issues/19) 要求固定自订布局与常用位置，核实时仍为 Open。[PR #28](https://github.com/3egirlsdream/Mac-Explorer/pull/28) 已合并，关联解决 #27；[2.0.1](https://github.com/3egirlsdream/Mac-Explorer/commit/13449f0851de9334b9a149400a4b87415a92c818) 已包含工作组状态恢复。

| 行为 | 原版 2.0.1 | 本修订 |
| --- | --- | --- |
| 恢复上次会话 | 已实现，默认关闭；保留各窗口页签、工作组布局、可见页签和活动窗格 | 保持原规则 |
| 指定默认启动目录 | 没有独立配置；`navigation_last_directory` 自动记录最后目录 | 不新增此配置 |
| 每次启动自动使用固定预设 | 没有 | 按本次要求不采用 |
| 手动保存和打开工作组预设 | 没有保存及选择入口 | 新增多份预设，点击在当前窗口打开 |
| 窗口几何信息 | 会话不保存窗口坐标、尺寸 | 不扩展预设范围 |

`FolderWindowSession` 的 window boundaries 表示各窗口的页签归属，不是窗口坐标和宽高。分屏保存已有 `PaneLayout` 类型，不新增自由比例布局。

源码入口：[原启动设置](https://github.com/3egirlsdream/Mac-Explorer/blob/13449f0851de9334b9a149400a4b87415a92c818/Views/Dialogs/SettingsDialog.axaml#L129)、[会话结构与过滤](https://github.com/3egirlsdream/Mac-Explorer/blob/13449f0851de9334b9a149400a4b87415a92c818/Services/FolderTabSession.cs)、[最后访问目录](https://github.com/3egirlsdream/Mac-Explorer/blob/13449f0851de9334b9a149400a4b87415a92c818/ViewModels/FileListViewModel.cs#L264)、[启动恢复](https://github.com/3egirlsdream/Mac-Explorer/blob/13449f0851de9334b9a149400a4b87415a92c818/Views/MainWindow.Tabs.cs)、[工作组捕获与恢复](https://github.com/3egirlsdream/Mac-Explorer/blob/13449f0851de9334b9a149400a4b87415a92c818/ViewModels/MainWindowViewModel.Groups.cs)。

## 界面和操作

| 入口 | 操作 | 结果 |
| --- | --- | --- |
| 组名右键菜单 | 保存工作组为预设… | 紧凑名称对话框，默认组名；同名须确认替换 |
| 标题栏，分屏按钮左侧 | 工作组预设图标 | 下拉显示“预设名称 · 文件夹数量” |
| 下拉中的任一预设 | 单击 | 在当前窗口新增并展开该组，激活保存的窗格 |
| 下拉底部 | 保存工作组为预设… | 保存当前活动组；没有活动组时禁用 |
| 下拉 → 管理预设 → 预设名称 | 重命名 / 删除 | 修改预设本身；删除须确认，不影响已打开的组 |

复用 Fluent 工作组图标、原生 ContextMenu、现有 `TabGroupNameDialog`、全局紧凑按钮、主题资源和四语文案。主入口固定在标题栏右侧，不随页签横向滚动。首个版本不做预设管理大面板、拖拽排序或预设与打开实例之间的自动同步。

预设记录：名称、组颜色、文件夹页签顺序（包括重复路径）、分屏类型、可见页签、活动页签及窗格。打开时强制展开。搜索、压缩包、首页与其他虚拟页面继续遵循原文件夹会话过滤规则；只含这些页面的组不允许保存。

每个窗口最多打开一个对应预设的工作组。已有组时展开并激活该组，保留当前目录与分屏；组名或预设名称变化不影响关联，关联随自动会话恢复保留。关闭该组后可重新从预设打开。平时导航、切换组、退出应用都不会改写预设；修改预设需要再次保存，同名时确认替换。

## 最小实现和关键差异

继续使用 `ISettingsService`，仅新增 `tab_group_presets` 键，JSON 是 `TabGroupPreset[]`。每条记录有预设 ID、名称以及只含一个组的原 `FolderTabSession`。不新增数据库、存储文件、迁移系统或另一套恢复引擎。

原 `restore_folder_tabs` 和 `navigation_folder_tabs` 的读取、写入及启动优先级没有改变。工作组预设的保存和手动打开不受自动恢复开关控制。

```csharp
internal sealed record TabGroupPreset(Guid Id, string Name, FolderTabSession Session);

// 保存时仅选中这一组的成员，复用现有索引和布局重映射。
var members = group.TabIndices.ToHashSet();
var session = FolderTabSession.Capture(
    paths.Select((path, index) => members.Contains(index) ? path : null),
    group.SelectedIndex, [group]);

// 打开时重新检查目录；产生新组 ID，避免跨窗口或重复打开发生冲突。
var restored = FolderTabSession.Filter(Session.Paths, Session.SelectedIndex, canRestore, Session.Groups);
var newGroup = restored.Groups[0] with { Id = Guid.NewGuid(), Name = Name, IsCollapsed = false };
```

```diff
-PrepareRestoredFolderTabsAsync(FolderTabSession session)
+PrepareRestoredFolderTabsAsync(FolderTabSession session, bool append = false)

-var tab = i == 0 ? initialTab : await AddTabAsync(...);
+var tab = i == 0 && !append ? initialTab : await AddTabAsync(...);

+// 手动打开预设时追加页签，随后沿用 RestoreGroups、布局和目录加载。
+var ordered = await PrepareRestoredFolderTabsAsync(session, append: true);
+await LoadRestoredFolderTabsAsync(ordered);
```

启动恢复和手动预设可能同时加载，改为共享窗口内的两个目录加载槽。一个任务结束不再清空另一个任务的待恢复记录。取得加载槽后重新检查窗口关闭、页签关闭及用户已改变导航的状态；后台完成不会重新选择页签。

| 文件 | 改动 |
| --- | --- |
| `Services/TabGroupPreset.cs` | 单组捕获、目录过滤、独立实例，以及预设集合读写 |
| `Views/MainWindow.GroupPresets.cs` | 打开列表、保存、同名替换、重命名、删除、异常提示 |
| `Views/MainWindow.Tabs.cs` | 组右键入口；复用恢复流程追加页签，共享并发限制 |
| `Views/MainWindow.axaml` | 标题栏预设按钮，调整后续按钮列号 |
| `Views/Dialogs/TabGroupNameDialog.cs` | 允许复用名称对话框的标题和字段标签 |
| `Languages/{zh-CN,zh-Hant,en,fr}.json` | 对应菜单和提示文案 |
| `Tests/MacExplorer.UnitTests/TabGroupPresetTests.cs` | 持久化、过滤、快照独立性及开关兼容测试 |
| `Tests/MacExplorer.Tests/GroupPresetTests.cs` | 点击入口、追加组、多窗口、切换、关闭及并发测试 |
| `Tools/Testing/group-presets.md` | 本设计与验收记录 |

## 边界行为与回归验收

| 场景 | 预期 |
| --- | --- |
| 自动恢复关闭 | 不自动恢复会话；仍可保存和手动打开预设 |
| 自动恢复开启 | 仍恢复退出时状态；不会用预设替换上次会话 |
| 部分目录失效、无权限或磁盘脱机 | 跳过不可访问目录，重映射页签与布局，非阻塞提示；原预设不变 |
| 全部目录失效 | 显示提示，不新增空组，不替换当前页面 |
| 目录重新可用 | 关闭旧组后重新打开预设可以恢复这些目录 |
| 多窗口 | 预设集合共享，打开仅作用于发起窗口；组实例独立 |
| 工作组切换 | 各组保留自己的布局、可见页签及焦点 |
| 已打开组继续浏览 | 不改写预设；再次点击激活已有组并保留当前目录 |
| 重复点击 | 打开进行中阻止重复创建；完成后再次点击展开并激活已有组 |
| 加载中关闭页签或切换到其他组 | 不重建被关闭页签，不抢回当前焦点 |
| 启动恢复与预设同时加载 | 合计最多两个目录加载任务，不清除彼此的待处理记录 |
| 商店版权限和分屏权益 | 继续经过原目录访问检查和 `RefreshSplitAccess`，不绕过现有机制 |
| 删除预设 | 不关闭已打开组，不删除文件 |

原始 diff 在 Linux/.NET 10 环境编译应用及测试项目，并运行 Avalonia Headless 测试。macOS 专有构建步骤使用仓库外的测试适配跳过，补丁不修改产品构建配置。

验证结果：

- 应用、Avalonia 测试项目和单元测试项目构建通过（已有依赖/可空性/XAML 警告，无错误）。
- 39 个 `NavigationSettings` / `GroupPresets` 单元测试通过。
- 38 个 `GroupPresets` / `TabGroups` / `TabStartup` / `TabStripLayout` Avalonia 相关测试通过，包含点击实际菜单、并发加载、关闭队列中页签和两窗口独立性。
- 7 个本地化生成器测试通过；另外检查四种语言均包含 17 个预设文案且占位符可格式化。
- `git diff --check` 通过；最终补丁对 2.0.1 基线通过 `git apply --check`。

原始 diff 新增 14 个单元测试用例和 5 个界面集成用例。标准 `run-tests.sh fast` 当前固定查找 macOS 输出，因此 Linux 使用同一测试项目生成的 linux-x64 可执行文件和原 `run-isolated.sh` 执行；未宣称完整原生 fast 套件通过。测试命令的过滤器如下：

```sh
# MacExplorer.UnitTests
-filter '/[(Area=GroupPresets)|(Area=NavigationSettings)]' -parallel none
# MacExplorer.Tests
-filter '/[(Area=GroupPresets)|(Area=TabGroups)|(Area=TabStartup)|(Area=TabStripLayout)]' -explicit on -parallel none
```

macOS 开发环境可运行以下仓库标准入口：

```sh
bash Tools/Testing/run-tests.sh fast --area GroupPresets --area NavigationSettings --area TabGroups
bash Tools/Testing/run-tests.sh ui --area GroupPresets --area TabGroups --area TabStartup --area TabStripLayout
```

macOS 上还应手工检查浅色/深色、最窄窗口、长组名、键盘菜单操作、外接盘重新挂载和商店版授权提示。原始 diff 未进行原生 macOS 真机测试，不把 Linux Headless 结果等同于原生视觉与系统集成验收；后续本机验证见下节。

本补丁实现的是本次确认的“手动工作组预设”，不声称已实现原 Issue 的“每次自动固定启动布局”。未评论或关闭 Issue，未推送、合并或发布。

## macOS 参考图界面实现与验证（2026-11-09）

在当前 2.0.2 开发工作区按参考图完善保存、打开和管理入口。标题栏按钮复用全局 `title-nav-button`，菜单打开时使用 `active` 主题选中态；下拉贴按钮右对齐，以预设列表、保存、管理三个区域组织。列表单击直接打开独立工作组；管理使用 Fluent Settings 图标及递归子菜单，无预设时保留禁用入口。名称对话框在透明标题栏显示一次标题，空名称不能保存。

新增菜单完整操作回归，覆盖保存、取消同名替换、确认替换、重命名和删除；核对预设 ID、已打开工作组及文件夹保持正确状态。

原生隔离检查：

```sh
dotnet build MacExplorer.csproj -c Debug -p:SkipMacOSReleaseDMG=true
MACEXPLORER_GROUP_PRESETS_SMOKE=1 bash Tools/Testing/run-isolated.sh \
  'bin/Debug/net10.0/osx-arm64/Mac Explorer.app/Contents/MacOS/MacExplorer'
```

`group-presets/report.json` 记录保存、打开、替换、重命名和删除的结果。截图包括四语浅色宽窗口／深色窄窗口菜单、管理子菜单、保存／重命名及确认对话框、打开后的分屏与活动窗格。数据全部位于本次测试目录，检查结束后自动退出；不修改真实用户预设或目录。

本机原生检查 13 项通过；`ui --area GroupPresets --area Window --area Localization` 的 55 项测试通过。参考截图保存在本地 `outputs/group-presets-ui-20261109/`。扩大到页签组回归时，工作区并行新增的 `GroupAccentsStayContinuousAcrossSelectionAndSeparateAdjacentGroups` 浅／深色用例存在 1px 衔接断言失败；该项不计为通过，也未覆盖或回退并行的彩线改动。


## 按钮统一与重复打开修订（2026-11-09）

新建、工作组预设及分屏三个按钮统一使用全局 30×30px 尺寸、16px 图标和 `4,8,4,0` 边距，页签原有顶部位置保持不变。三项使用 Fluent Add／Square Multiple／Split Vertical 20 Regular，以相近的可见轮廓和相同线宽避免扁图标显小；按钮水平中心线与页签中心对齐。页签布局为新建按钮保留完整的 38px 宽度，包含左右留白。已保存或从预设打开的工作组通过 `PresetId` 关联预设；再次点击只展开并激活已有组，保留当前目录、布局和组名。关联随会话序列化，重启恢复后继续去重；关闭组后允许重新打开，不同窗口分别管理自己的组实例。

本轮 `fast` 1177 通过、2 跳过；预设、页签布局、启动恢复与工作组的定向 UI 回归 31 项全部通过；原生隔离检查 16 项通过，包括三个按钮的实际尺寸、边距、纵向位置，以及保存后立即打开和重复打开不增加页签。上节记录的并行彩线回归已由当前工作区的后续修改通过。
