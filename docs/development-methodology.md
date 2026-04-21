# 一个人、七天、一个 macOS Finder：AI 辅助原生应用开发方法论

> 基于 Mac Explorer (FKFinder) 项目的开发实践总结。一个用 .NET MAUI Blazor + Mac Catalyst 构建的 macOS 文件管理器，从零到功能完整，历时 7 天，78 次提交。

---

## 一、项目概览

Mac Explorer 是一个 macOS 原生文件管理器，技术栈为 .NET 10 MAUI Blazor + Mac Catalyst。它不是一个 Web 应用套壳，而是一个需要深度对接 macOS 原生能力的桌面应用——毛玻璃窗口、原生拖拽、FSEvents 文件监听、NSMenu 右键菜单、系统剪贴板、Dock 菜单，全部通过 P/Invoke 调用 AppKit/CoreServices 实现。

核心数据：
- **开发周期**：7 天（2026-04-10 至 2026-04-17）
- **提交次数**：78 次，日均 11 次
- **代码规模**：30+ 服务接口、8 个 ViewModel、15+ Blazor 组件、6 个原生 Helper
- **技术文档**：14 篇，覆盖每一个核心模块的架构方案和疑难问题

---

## 二、核心方法论：「文档驱动 + AI 协作」的开发范式

### 2.1 先写文档，再写代码

这个项目最显著的特征不是代码量，而是文档量。每一个核心功能模块都有一篇结构化的技术文档，且文档不是事后补写的——它是开发过程的一部分。

以拖拽功能为例，[drag-drop-implementation.md](docs/drag-drop-implementation.md) 完整记录了：

1. 需求定义（四种拖拽场景）
2. Mac Catalyst 拖拽管线的系统分析（通过 `log stream` 抓取内核日志）
3. **6 个失败方案**及其失败原因
4. 最终方案的详细实现
5. 跨窗口拖拽的后续 Bug 修复

这种文档不是给别人看的 README，而是给 AI 和未来的自己看的**上下文文档**。它的价值在于：

- **为 AI 提供精确上下文**：当你告诉 AI "实现拖拽功能"，它不知道 Mac Catalyst 的桥接层会拦截事件。但当你把前 5 个失败方案喂给它，它就能理解约束条件，在正确的方向上思考。
- **避免重复踩坑**：文档里记录的不仅是"怎么做"，更重要的是"什么不能做、为什么不能做"。
- **方案选择有据可查**：每个架构决策都有推导过程，不是凭感觉选的。

**方法论总结**：文档是你和 AI 之间的共享记忆。人脑记不住六个失败方案的细节，但文档可以。当你需要修改或扩展功能时，AI 读完文档就能站在正确的起点上。

### 2.2 「规范文档」先行于实现

设置系统、弹窗管理、统一刷新机制——这些跨组件的架构功能，都先产出了规范文档（spec），再进入编码。

[settings-system-spec.md](docs/settings-system-spec.md) 就是一个典型案例。它在一行代码都没写之前就定义了：
- 四个 Tab 页的设置项清单和默认值
- 数据流向图（SettingsDialog → ISettingsService → ViewModel → Blazor 组件）
- 属性转发和事件通知的具体机制
- **新增设置项的操作指南**（一步步告诉你改哪几个文件）

这种规范文档本质上是一份**面向 AI 的 API 合约**。当你需要新增一个设置项时，你不需要重新解释整个架构，只需要说"按照 settings-system-spec.md 的指南，新增一个 XXX 设置项"，AI 就能精确执行。

**方法论总结**：对于跨组件的架构功能，先花 30 分钟写规范文档，再让 AI 按规范实现。规范文档的 ROI 远高于代码注释。

### 2.3 Spec → 实现 → 文档的闭环

项目中有一个 `.qoder/specs/` 目录，存放实现前的设计 spec。以统一刷新机制为例，完整的开发流程是：

```
1. 识别问题：拖拽后不刷新、跨窗口不同步、外部变更无感知
2. 写 Spec：四层刷新管线架构、各场景信号流向、边界情况分析
3. AI 实现：按 Spec 生成代码（4 个新文件 + 8 个修改文件）
4. 验证：按 Spec 中的验证步骤逐项测试
5. 归档文档：将 Spec 精简为架构文档，补充实际踩坑记录
```

这个闭环的关键在于——**Spec 里包含验证步骤**。AI 生成的代码不一定对，但如果 Spec 里写了"Terminal 执行 `touch /viewed/dir/new.txt` → MacExplorer 1s 内显示"，你就有了一个明确的验收标准。

---

## 三、AI 协作的具体策略

### 3.1 让 AI 理解你的架构分层

Mac Explorer 有一个清晰的三层架构：

| 层级 | 技术 | 控制者 |
|------|------|--------|
| AppKit 层 | NSWindow / NSView / NSVisualEffectView | macOS 系统 |
| UIKit 层 | UIWindow / UIView | MAUI 框架 |
| Web 层 | Blazor / HTML / CSS / JS | 开发者 |

这三层之间通过 Mac Catalyst 桥接层连接。理解这个分层是整个项目的关键——毛玻璃效果要在 AppKit 层做（因为 MAUI 管不到），拖拽要在 AppKit 层拦截（因为桥接层会抢先处理），而 UI 渲染在 Web 层做。

透明窗口的实现过程就是这个分层思维的体现。[mac-catalyst-transparent-window.md](docs/mac-catalyst-transparent-window.md) 记录了 8 种尝试：

1. UIKit 层设置背景 → 失败（NSWindow 底层不透明）
2. ObjC 运行时设置 NSWindow → 不稳定（MAUI 会重置）
3. MAUI Mapper 拦截 → 不稳定（管不到 Catalyst 桥接层）
4. KVO 观察属性变化 → 失败（竞态条件 + 闪烁）
5. NSTimer 定时强制 → 失败（性能差 + 仍闪烁）
6. Method Swizzling → 失败（桥接层不走标准 setter）
7. 第三方库 → 不支持
8. **AppKit 层插入 NSVisualEffectView → 成功**

最终方案之所以稳定，核心原理只有一句话：**MAUI 只能操作 UIKit 层，对 AppKit 层的 NSView 完全无感知，所以不会重置。**

**给 AI 的启示**：当你把分层架构和之前的失败经验告诉 AI，它就能理解"为什么要在 AppKit 层做"。否则它会本能地建议你用 MAUI 的标准 API，然后陷入同样的坑。

### 3.2 穷举式问题求解

拖拽功能的开发过程是 AI 辅助开发中「穷举式求解」的典型案例。问题的本质是：Mac Catalyst 的桥接层在 AppKit 层面拦截了拖拽事件，WebKit 声称已处理但实际上因为 sandbox extension 失败导致 HTML5 drop 事件不触发。

面对这种底层平台限制，解决方式不是"想到一个方案就去实现"，而是：

1. **先用系统日志理解内部管线**（`log stream --process MacExplorer --level debug`）
2. **系统化列举所有可能的拦截点**（UIDropInteraction、NSDraggingDestination、Method Swizzling、NSView 覆盖层...）
3. **逐个尝试，记录失败原因**
4. **从失败模式中推导出正确方向**

方案 4（在 contentView 注入方法）和方案 6（新增顶层子视图）的区别非常微妙——前者是在现有类上注入方法，桥接层的 UINSView 作为更深层子视图先处理了事件；后者是新增一个顶层子视图，NSDragging 命中测试从最顶层开始，所以覆盖层先于 UINSView 接收事件。

这种区别不是 AI 能凭空推理出来的。但当你把方案 4 的失败日志和 NSView 命中测试机制告诉 AI，它就能设计出方案 6。

**方法论总结**：对于底层平台问题，人负责探索和记录，AI 负责在约束条件下设计方案。人的优势是能运行程序、看日志、理解系统行为；AI 的优势是在已知约束下快速生成实现代码。

### 3.3 P/Invoke 模式的复用

项目中有大量通过 P/Invoke 调用 Objective-C 运行时的代码，形成了一套可复用的模式：

- `DockMenuHelper.cs`：`class_replaceMethod` 注入 ObjC 方法
- `ContextMenuHelper.cs`：`objc_allocateClassPair` 创建自定义 ObjC 类
- `VibrancyHelper.cs`：`UISBHSDidCreateWindowForSceneNotification` 监听窗口创建
- `DropOverlayHelper.cs`：综合使用以上所有模式

当需要实现新的原生功能时，不是从零开始，而是告诉 AI "参考 VibrancyHelper 的窗口创建监听模式"。AI 就能套用已有的 P/Invoke 模板，大幅减少试错。

**方法论总结**：将底层交互模式抽象为可复用的"配方"，在文档中记录。AI 擅长模式复用，不擅长从零发明底层交互方式。

---

## 四、架构策略：面向 AI 可维护性的设计

### 4.1 接口隔离 + 依赖注入

项目有 30+ 个服务接口（`IFileService`、`IClipboardService`、`IDragDropBridge`、`IThemeService`...），每个接口职责单一。这不仅是常规的好设计，在 AI 辅助开发中还有额外价值：

- **降低 AI 的认知负担**：让 AI 实现 `IThemeService` 比让它修改一个 5000 行的 God Class 容易得多。
- **安全的并行开发**：你可以同时让 AI 实现多个不相关的服务，接口保证它们不会互相冲突。
- **Mock 友好**：测试和验证时可以独立替换任何一个服务。

### 4.2 ViewModel 分层协调

`FileListViewModel`（70KB）是项目中最复杂的文件，但它不是一个巨型类——它是一个协调器，将职责委托给子 ViewModel：

- `SortFilterViewModel`：排序和过滤逻辑
- `NavigationViewModel`：导航和面包屑
- `ArchiveViewModel`：压缩文件浏览
- `AiViewModel`：AI 分析视图
- `CollectionViewModel`：收藏夹视图
- `FileOpsViewModel`：文件操作（复制/粘贴/删除）
- `SearchViewModel`：搜索

通过属性转发和事件转发机制（`OnSortFilterPropertyChanged`），子 ViewModel 的变更可以冒泡到 Blazor 组件层。

这种分层的 AI 协作优势：当你需要修改排序逻辑时，只需让 AI 关注 `SortFilterViewModel` 和相关文档，不需要它理解整个 `FileListViewModel`。

### 4.3 桥接模式处理跨层通信

Blazor（Web 层）和原生功能（AppKit/UIKit 层）之间的通信，统一通过桥接接口：

- `IDragDropBridge`：拖拽操作的跨层桥接
- `NavigationBridge`：JS → C# 的导航操作
- `WKScriptMessageHandler`：JS → 原生的消息通道

每个桥接点都有明确的数据合约。这让 AI 可以独立处理 Web 端或原生端的修改，只要桥接接口不变。

---

## 五、开发节奏：高频提交、功能切片

### 5.1 日均 11 次提交的节奏

从 Git 历史来看：

| 日期 | 提交数 | 主题 |
|------|--------|------|
| 04-10 | 1 | 项目初始化 |
| 04-11 | 15 | 核心框架（文件列表/侧边栏/预览/工具栏/图标系统） |
| 04-12 | 17 | 交互增强（拖拽/剪贴板/右键菜单/压缩/多窗口） |
| 04-13 | 12 | AI 功能（图像分析/标签/模板/设置） |
| 04-14 | 7 | 架构重构（拖拽重构/统一刷新/ViewModel 拆分） |
| 04-15 | 10 | UI 打磨（动画/弹窗/导航修复） |
| 04-16 | 16 | 体验完善（设置系统/深色模式/毛玻璃/国际化） |

规律是明确的：

- **前两天疯狂搭建**：把所有核心功能的骨架立起来
- **第三天加入差异化功能**：AI 图像分析是区别于原生 Finder 的核心功能
- **第四天还技术债**：重构拖拽架构、统一刷新管线、拆分 ViewModel
- **最后两天打磨体验**：深色模式、毛玻璃透明度配置、路径本地化

### 5.2 每个提交是一个完整的功能切片

提交消息采用 Conventional Commits 格式（`feat/fix/refactor/docs`），每个提交对应一个完整的功能切片。这种粒度非常适合 AI 协作：

- 一次对话完成一个 feature
- 出问题时 `git revert` 可以精确回滚
- 提交消息本身构成了开发日志

### 5.3 Commit Message 即文档

项目的提交消息全部使用中文，且带有明确的 scope：

```
feat(theme): 集成深色模式 UI（Blazor 组件 + 设置对话框）
fix(build): 构建后注入 CFBundleLocalizations 使 NSFileManager 返回本地化名称
refactor(archive): 将归档视图状态管理移至 NavigationViewModel
```

这些消息不仅描述了"改了什么"，还描述了"为什么改"和"改了哪里"。当未来需要回溯某个功能的演变过程时，Git log 本身就是一份时间线文档。

---

## 六、疑难问题的攻关模式

### 6.1 系统日志分析法

面对 Mac Catalyst 这种文档稀缺的平台，最有效的调试方式是直接分析系统日志：

```bash
log stream --process MacExplorer --level debug
```

通过日志，项目发现了：
- Mac Catalyst 桥接层的完整拖拽管线
- WebKit sandbox extension 失败是 HTML5 drop 事件不触发的根因
- `UIKitMacHelper DruidConnection` 不使用标准 UIDropInteraction 对象

**方法论总结**：当官方文档不够用时，系统日志是唯一的真相来源。先理解系统行为，再设计方案。

### 6.2 文件日志调试法

当 `Console.WriteLine` 和 NSLog 在 Mac Catalyst 上都不可靠时（`open` 启动的应用在 `log stream` 中不显示），使用文件日志：

```csharp
File.AppendAllText("/tmp/fkfinder-drag.log", $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
```

虽然原始，但有效。在调试 P/Invoke 和 ObjC 运行时交互时，这可能是唯一能用的调试手段。

### 6.3 构建管线的深层问题

路径本地化功能遇到了一个典型的"构建管线 vs 运行时"问题：

1. `NSFileManager.DisplayName()` 返回英文而非中文 → 因为 `Info.plist` 没有 `CFBundleLocalizations`
2. 在源文件中添加了该键 → 构建后键不存在 → MAUI 构建管线会剥离自定义键
3. 使用 PlistBuddy 在构建后注入 → 单条命令 27+ 参数崩溃 → 拆分为三条命令
4. 重复构建导致数组条目叠加 → 注入前先 Delete

每一层问题都是上一层解决方案引入的。这种"递归踩坑"在跨平台开发中很常见，文档化的价值也最大。

---

## 七、总结

这个项目的开发方式可以概括为三个核心原则：

### 原则一：文档是第一等公民

不是先写代码后补文档，而是文档驱动开发。Spec 定义架构，文档记录踩坑，提交消息串联时间线。文档的首要读者不是人，而是 AI。

### 原则二：人做探索，AI 做实现

底层平台的行为只能通过运行、观察、调试来理解。但一旦约束条件明确了，实现工作可以交给 AI。人负责把 6 个失败方案的经验转化为精确的约束描述，AI 负责在约束内生成正确的实现。

### 原则三：架构服务于 AI 可维护性

接口隔离、ViewModel 分层、桥接模式——这些设计不仅是好的工程实践，更是降低 AI 协作时上下文复杂度的手段。每次对话只需要让 AI 理解一个接口、一个子 ViewModel、一篇文档，而不是整个项目。

这三个原则的底层逻辑是一致的：**把隐式知识显式化，把全局复杂度分解为局部问题。** 这恰好是人类和 AI 协作的最佳模式——人擅长处理模糊的、需要实际验证的探索性问题，AI 擅长处理明确的、有足够上下文的实现性问题。让各自做各自擅长的事。
