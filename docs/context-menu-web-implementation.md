# 右键菜单 Web 实现方案

> 仅涵盖 Blazor 组件实现，不包含原生 NSMenu 版本。

## 整体架构

右键菜单采用 **数据驱动 + Blazor 组件渲染** 的方式，核心链路为：

```
用户右键 → oncontextmenu 事件 → ViewModel 生成菜单数据 → Blazor 组件渲染 → 用户点击 → 执行回调
```

---

## 一、核心文件

| 模块 | 文件 |
|------|------|
| 数据模型 | `src/MacExplorer/Models/ContextMenuAction.cs` |
| 菜单 UI 组件 | `src/MacExplorer/Components/ContextMenu/ContextMenu.razor` |
| 菜单服务接口 | `src/MacExplorer/Services/IContextMenuService.cs` |
| 菜单服务实现 | `src/MacExplorer/Platforms/MacCatalyst/Services/MacContextMenuService.cs` |
| 状态管理 & 触发 | `src/MacExplorer/ViewModels/FileListViewModel.cs` |
| 触发点 - 文件列表 | `src/MacExplorer/Components/FileList/FileGridView.razor` |
| 触发点 - 侧边栏 | `src/MacExplorer/Components/Sidebar/FinderSidebar.razor` |
| 菜单容器 | `src/MacExplorer/Components/Pages/Home.razor` |
| JS 辅助 | `src/MacExplorer/wwwroot/js/keyboard.js` |
| 样式 | `src/MacExplorer/wwwroot/css/app.css` + `src/MacExplorer/wwwroot/css/variables.css` |

---

## 二、数据结构 — `ContextMenuAction`

```csharp
public class ContextMenuAction
{
    public string Label { get; init; }                                    // 菜单项标签
    public string IconSvg { get; init; }                                 // SVG 图标
    public string ShortcutText { get; init; }                            // 快捷键文本 (⌘O)
    public bool IsEnabled { get; init; }                                 // 启用/禁用
    public bool IsSeparator { get; init; }                               // 分隔符
    public Func<Task>? Execute { get; init; }                            // 执行回调
    public IReadOnlyList<ContextMenuAction>? SubItems { get; init; }     // 子菜单
    public string? Tag { get; init; }                                     // 标签标识
    public bool IsQuickAction { get; init; }                             // 快速操作栏标志
}
```

统一的数据模型同时服务 Web 菜单和原生菜单，一次定义、多处消费。

---

## 三、触发方式

### 1. 文件项右键

```
@oncontextmenu → FileGridView.OnFileContextMenu(MouseEventArgs, entry)
  → ViewModel.ShowFileContextMenuAsync(entry, x, y)
```

### 2. 空白区域右键

```
@oncontextmenu → FileGridView.OnBackgroundContextMenu(MouseEventArgs)
  → ViewModel.ShowBackgroundContextMenuAsync(x, y)
```

### 3. 侧边栏集合项右键

```
@oncontextmenu → FinderSidebar.ShowCollectionContextMenu(e, collection)
```

使用 `_fileContextMenuActive` 标志防止文件项菜单事件冒泡到背景，避免同时弹出两个菜单。

---

## 四、菜单数据生成 — `IContextMenuService` + WireUp

**两阶段构造**：

1. **IContextMenuService** 生成基础菜单项（Label、Icon、快捷键等声明式数据）
2. **ViewModel.WireUpContextMenuActionsAsync** 为每个菜单项关联 `Execute` 委托

```csharp
// WireUp 示例
if (action.Label == "打开")
    result.Add(action with { Execute = () => OpenEntryCommand.ExecuteAsync(entry) });

if (action.Label == "拷贝")
    result.Add(action with { Execute = async () => _fileOps.CopySelectedCommand.Execute(...) });
```

这种分离使菜单内容定义（Service）和业务逻辑（ViewModel）解耦。

---

## 五、状态管理 — ViewModel 属性

```csharp
public bool IsContextMenuVisible { get; set; }                          // 可见性
public double ContextMenuX { get; set; }                                // X 坐标
public double ContextMenuY { get; set; }                                // Y 坐标
public ObservableCollection<ContextMenuAction> ContextMenuActions       // 菜单项集合
public FileSystemEntry? ContextMenuEntry                                // 当前右键的文件条目
```

- **显示**：设置坐标、填充 Actions、`IsContextMenuVisible = true`
- **关闭**：`CloseContextMenu()` → `IsContextMenuVisible = false` + `Actions.Clear()`

---

## 六、UI 组件 — `ContextMenu.razor`

### 渲染结构

```
.context-menu-overlay          ← 遮罩层，点击关闭
  └─ .context-menu             ← 主菜单容器
       ├─ .context-menu-quick-bar    ← 快速操作栏（剪切/拷贝/重命名/删除）
       ├─ .context-menu-item         ← 普通菜单项
       ├─ .context-menu-separator    ← 分隔符
       └─ .context-menu-item.has-submenu  ← 有子菜单的项
            └─ .context-submenu      ← 子菜单（悬停显示）
```

### 快速操作栏

菜单顶部的图标栏，包含高频操作（剪切、拷贝、重命名、删除、粘贴），通过 `IsQuickAction = true` 标记。

### 子菜单

- 悬停触发：`@onmouseenter` 显示 / `@onmouseleave` 隐藏
- 状态跟踪：`_hoveredSubMenu` 变量记录当前展开的子菜单
- 绝对定位在父项右侧
- CSS `::before` 伪元素在菜单间隙创建"桥梁"，防止鼠标移动时子菜单消失

### 菜单项点击

```csharp
ExecuteAction(action):
  → 检查 IsEnabled
  → 关闭菜单
  → 执行 action.Execute() 回调
```

---

## 七、JS 辅助 — `keyboard.js`

### 位置调整

```javascript
fkfinderContextMenu.adjustPosition(menuEl, x, y)
  // 防止菜单超出窗口边界
  if (x + width > viewportWidth)  → 左移
  if (y + height > viewportHeight) → 上移
```

### 自动关闭

```javascript
fkfinderContextMenu.registerAutoClose(dotNetRef)
  // 点击菜单外部区域时关闭
  document.addEventListener('mousedown', handler)
  if (!e.target.closest('.context-menu'))
    → dotNetRef.invokeMethodAsync('RequestClose')
```

---

## 八、CSS 样式层级

```css
.context-menu-overlay    { z-index: 500; }   /* 阻止点击穿透 */
.context-menu            { z-index: 501; }   /* 主菜单 */
.context-submenu         { z-index: 502; }   /* 子菜单 */
```

关键样式特征：

- 毛玻璃背景（`backdrop-filter: blur`）
- 0.5px 细分隔线
- 悬停高亮
- 禁用项半透明 + 不可点击
- 子菜单箭头 `›`
- 快捷键文本右对齐、浅色

---

## 九、菜单内容（按场景）

| 场景 | 快速操作 | 菜单项 |
|------|---------|--------|
| **文件/文件夹** | 剪切、拷贝、重命名、删除 | 打开、打开方式(子菜单)、压缩/解压、复制路径、在Finder中显示、在终端中打开、在VS Code中打开、Pin到收藏、查看文件信息、添加到收藏夹(子菜单) |
| **空白背景** | 粘贴 | 新建文件夹、新建文件、在终端中打开、在VS Code中打开、复制路径 |
| **废纸篓文件** | — | 永久删除 |
| **废纸篓背景** | — | 清倒废纸篓 |
| **虚拟条目(AI分类)** | — | 打开、重命名(仅人物分类) |
| **侧边栏集合** | — | 重命名、删除集合 |

---

## 十、完整生命周期

```
1. 用户右键点击
2. Blazor @oncontextmenu 捕获事件，阻止默认浏览器菜单
3. 根据点击位置判断场景（文件/背景/集合/废纸篓/虚拟条目）
4. IContextMenuService 生成基础菜单项列表
5. ViewModel.WireUp 关联执行委托
6. 设置坐标和可见性，触发 Blazor 重新渲染
7. JS adjustPosition 调整菜单位置（防溢出）
8. JS registerAutoClose 注册外部点击监听
9. 用户交互：
   ├─ 点击菜单项 → ExecuteAction → 关闭菜单 → 执行业务逻辑
   ├─ 悬停子菜单项 → 展开子菜单
   └─ 点击外部 → RequestClose → 关闭菜单
10. 清理状态
```
