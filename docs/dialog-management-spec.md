# FKFinder 弹窗管理统一方案

## 1. 现状分析

### 1.1 现有弹窗类型

| 弹窗名称 | 类型 | 打开方式 | 位置 | 问题 |
|---------|------|---------|------|------|
| ProgressDialog (任务进度) | 信息展示 | ViewModel状态驱动 | Home.razor | 与ViewModel耦合 |
| CompressDialog (压缩文件) | 表单操作 | ViewModel状态驱动 | Home.razor | 与ViewModel耦合 |
| SettingsDialog (设置) | 表单操作 | 本地状态驱动 | Home.razor | 独立管理 |
| DeleteConfirmDialog (删除确认) | 确认操作 | ViewModel状态驱动 | Home.razor | 与ViewModel耦合 |
| CollectionDeleteConfirmDialog (收藏夹删除确认) | 确认操作 | ViewModel状态驱动 | Home.razor | 与ViewModel耦合 |

### 1.2 现有问题

1. **状态管理混乱**：部分弹窗通过 ViewModel 状态驱动，部分通过本地状态驱动
2. **代码重复**：每个确认弹窗都需要在 Home.razor 中定义 MDialog 容器和回调方法
3. **调用方式不统一**：有的通过 Command，有的通过直接方法调用
4. **扩展困难**：新增弹窗需要修改多个文件
5. **双重状态同步风险**：ViewModel 的弹窗状态需要通过 SyncDialogState() 同步到 Home.razor 本地字段，任一侧遗漏同步都会导致弹窗状态不一致（例如取消弹窗后自动重弹）

## 2. 统一方案设计

### 2.1 弹窗分类

```
弹窗类型
├── 确认类弹窗 (ConfirmDialog)
│   ├── 删除确认
│   ├── 收藏夹删除确认
│   └── 清空废纸篓确认
├── 表单类弹窗 (FormDialog)
│   ├── 压缩文件
│   ├── 重命名
│   └── 新建文件夹
├── 信息类弹窗 (InfoDialog)
│   ├── 任务进度
│   ├── 文件信息
│   └── 错误提示
└── 设置类弹窗 (SettingsDialog)
    └── 应用设置
```

### 2.2 核心设计原则

1. **函数式调用**：确认类弹窗采用 `await ShowConfirmAsync()` 方式
2. **状态集中管理**：所有弹窗状态由专门的 `DialogService` 管理
3. **声明式渲染**：Home.razor 中只保留一个统一的弹窗容器
4. **类型安全**：使用泛型和强类型参数

## 3. 具体实现方案

### 3.1 新增文件结构

```
src/FKFinder/
├── Services/
│   ├── IDialogService.cs         # 弹窗服务接口
│   └── DialogService.cs          # 弹窗服务实现
├── Models/
│   └── DialogModels.cs           # 弹窗数据模型
└── Components/
    └── Dialogs/
        ├── DialogHost.razor      # 统一弹窗容器（替换现有多个MDialog）
        ├── ConfirmDialog.razor   # 通用确认弹窗组件
        └── [保留现有组件]       # CompressDialog, SettingsDialog 等
```

### 3.2 DialogService 接口设计

```csharp
// 确认弹窗选项
public class ConfirmOptions
{
    public string Title { get; set; } = "确认";
    public string Message { get; set; } = "";
    public string ConfirmText { get; set; } = "确定";
    public string CancelText { get; set; } = "取消";
    public ConfirmType Type { get; set; } = ConfirmType.Info;
}

public enum ConfirmType { Info, Warning, Danger }

// 弹窗服务接口
public interface IDialogService
{
    // ── 状态通知 ──
    // DialogHost 订阅此事件以触发 StateHasChanged()
    event Action? OnStateChanged;

    // ── 当前弹窗状态（供 DialogHost 绑定） ──
    ConfirmOptions? CurrentConfirm { get; }
    bool IsCompressDialogVisible { get; }
    CompressOptions? CompressOptions { get; }
    BackgroundTaskInfo? ActiveTask { get; }
    bool IsSettingsDialogVisible { get; }

    // ── 确认类弹窗 - 函数式调用 ──
    Task<bool> ShowConfirmAsync(ConfirmOptions options);
    Task<bool> ShowDeleteConfirmAsync(string itemName);
    Task<bool> ShowDeleteConfirmAsync(int itemCount, string firstItemName);
    Task<bool> ShowCollectionDeleteConfirmAsync(string collectionName);
    
    // ── 表单类弹窗 ──
    Task<CompressOptions?> ShowCompressDialogAsync(CompressOptions initialOptions);
    Task<string?> ShowRenameDialogAsync(string currentName);
    
    // ── 信息类弹窗 ──
    void ShowProgressDialog(BackgroundTaskInfo task);
    void CloseProgressDialog();
    
    // ── 设置类弹窗 ──
    void ShowSettingsDialog();
    void CloseSettingsDialog();

    // ── 内部回调（供 DialogHost 调用） ──
    void ConfirmCurrent(bool result);
}
```

### 3.3 DialogService 实现关键设计

#### 3.3.1 渲染通知机制

Blazor 中服务层状态变化不会自动触发组件重新渲染。`DialogService` 通过 `OnStateChanged` 事件通知 `DialogHost` 调用 `StateHasChanged()`：

```csharp
public class DialogService : IDialogService
{
    public event Action? OnStateChanged;

    public ConfirmOptions? CurrentConfirm { get; private set; }
    private TaskCompletionSource<bool>? _confirmTcs;

    public Task<bool> ShowConfirmAsync(ConfirmOptions options)
    {
        // 如果已有弹窗在显示，先取消
        _confirmTcs?.TrySetResult(false);

        CurrentConfirm = options;
        _confirmTcs = new TaskCompletionSource<bool>();
        OnStateChanged?.Invoke(); // 通知 UI 重新渲染
        return _confirmTcs.Task;
    }

    public void ConfirmCurrent(bool result)
    {
        _confirmTcs?.TrySetResult(result);
        _confirmTcs = null;
        CurrentConfirm = null;
        OnStateChanged?.Invoke(); // 通知 UI 关闭弹窗
    }

    // 便捷方法
    public Task<bool> ShowDeleteConfirmAsync(string itemName)
    {
        return ShowConfirmAsync(new ConfirmOptions
        {
            Title = "确认删除",
            Message = $"确定要删除 \"{itemName}\" 吗？",
            ConfirmText = "删除",
            Type = ConfirmType.Danger
        });
    }

    public Task<bool> ShowDeleteConfirmAsync(int itemCount, string firstItemName)
    {
        var message = itemCount == 1
            ? $"确定要删除 \"{firstItemName}\" 吗？"
            : $"确定要删除选中的 {itemCount} 个项目吗？";
        return ShowConfirmAsync(new ConfirmOptions
        {
            Title = "确认删除",
            Message = message,
            ConfirmText = "删除",
            Type = ConfirmType.Danger
        });
    }

    public Task<bool> ShowCollectionDeleteConfirmAsync(string collectionName)
    {
        return ShowConfirmAsync(new ConfirmOptions
        {
            Title = "确认删除收藏夹",
            Message = $"确定要删除收藏夹 \"{collectionName}\" 吗？收藏夹中的文件不会被删除。",
            ConfirmText = "删除",
            Type = ConfirmType.Danger
        });
    }
}
```

#### 3.3.2 并发弹窗控制

同一时刻只允许一个确认弹窗。如果新弹窗请求到达时旧弹窗尚未关闭，旧弹窗的 `TaskCompletionSource` 会被自动取消（返回 `false`）：

```csharp
public Task<bool> ShowConfirmAsync(ConfirmOptions options)
{
    // 取消之前未完成的确认弹窗
    _confirmTcs?.TrySetResult(false);

    CurrentConfirm = options;
    _confirmTcs = new TaskCompletionSource<bool>();
    OnStateChanged?.Invoke();
    return _confirmTcs.Task;
}
```

#### 3.3.3 多窗口生命周期

MAUI 多窗口场景下，每个窗口拥有独立的 Blazor WebView 和独立的 DI Scope。`DialogService` 应注册为 **Scoped**，确保每个窗口有自己的弹窗状态：

```csharp
// MauiProgram.cs
builder.Services.AddScoped<IDialogService, DialogService>();
```

> 注意：不能使用 Singleton，否则多窗口会共享弹窗状态。

### 3.4 DialogHost.razor 设计

```razor
@inject IDialogService DialogService
@implements IDisposable

@* 确认类弹窗 *@
@if (DialogService.CurrentConfirm != null)
{
    <MDialog Value="true" 
             MaxWidth="380"
             ContentClass="fk-dialog-glass"
             Persistent="true">
        <ConfirmDialog Options="DialogService.CurrentConfirm"
                       OnConfirm="() => DialogService.ConfirmCurrent(true)"
                       OnCancel="() => DialogService.ConfirmCurrent(false)" />
    </MDialog>
}

@* 表单类弹窗 - 压缩 *@
@if (DialogService.IsCompressDialogVisible)
{
    <MDialog Value="true"
             MaxWidth="380"
             ContentClass="fk-dialog-glass"
             Persistent="true">
        <CompressDialog Options="DialogService.CompressOptions"
                       OnConfirm="DialogService.OnCompressConfirm"
                       OnCancel="DialogService.OnCompressCancel" />
    </MDialog>
}

@* 信息类弹窗 - 进度 *@
@if (DialogService.ActiveTask != null)
{
    <MDialog Value="true"
             ContentClass="fk-dialog-glass-dense"
             Persistent="true">
        <CenterProgressCard Task="DialogService.ActiveTask"
                           OnMinimize="DialogService.MinimizeProgress" />
    </MDialog>
}

@* 设置类弹窗 *@
@if (DialogService.IsSettingsDialogVisible)
{
    <MDialog Value="true"
             MaxWidth="420"
             ContentClass="fk-dialog-glass">
        <SettingsDialog OnClose="DialogService.CloseSettings" />
    </MDialog>
}

@code {
    protected override void OnInitialized()
    {
        DialogService.OnStateChanged += OnDialogStateChanged;
    }

    private async void OnDialogStateChanged()
    {
        await InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        DialogService.OnStateChanged -= OnDialogStateChanged;
    }
}
```

### 3.5 使用示例

#### 删除文件（函数式调用）

```csharp
// 修改前（ViewModel中）
[RelayCommand]
public void ShowDeleteConfirmDialog()
{
    if (SelectedEntries.Count == 0) return;
    IsContextMenuVisible = false;
    IsDeleteConfirmDialogVisible = true;  // 状态驱动
}

[RelayCommand]
public async Task ConfirmDeleteSelectedAsync()
{
    IsDeleteConfirmDialogVisible = false;
    await ExecuteDeleteSelectedAsync();
}

// 修改后（函数式调用）
[RelayCommand]
public async Task DeleteSelectedAsync()
{
    if (SelectedEntries.Count == 0) return;
    IsContextMenuVisible = false;
    
    var confirmed = await _dialogService.ShowDeleteConfirmAsync(
        SelectedEntries.Count, 
        SelectedEntries.First().Name);
    
    if (confirmed)
    {
        await ExecuteDeleteSelectedAsync();
    }
}
```

#### 收藏夹删除（函数式调用）

```csharp
// 修改前
public void ShowCollectionDeleteConfirmDialog(int collectionId, string collectionName)
{
    IsContextMenuVisible = false;
    PendingDeleteCollectionId = collectionId;
    PendingDeleteCollectionName = collectionName;
    IsCollectionDeleteConfirmDialogVisible = true;
}

// 修改后
public async Task DeleteCollectionAsync(int collectionId, string collectionName)
{
    IsContextMenuVisible = false;
    var confirmed = await _dialogService.ShowCollectionDeleteConfirmAsync(collectionName);
    if (confirmed)
    {
        await ExecuteDeleteCollectionAsync(collectionId);
    }
}
```

### 3.6 需要修改的文件清单

| 文件 | 修改类型 | 修改内容 |
|------|---------|---------|
| `Models/DialogModels.cs` | 新增 | ConfirmOptions, ConfirmType 数据模型 |
| `Services/IDialogService.cs` | 新增 | 弹窗服务接口（含 OnStateChanged 事件） |
| `Services/DialogService.cs` | 新增 | 弹窗服务实现（含 TaskCompletionSource 机制） |
| `Components/Dialogs/DialogHost.razor` | 新增 | 统一弹窗容器（订阅 OnStateChanged） |
| `Components/Dialogs/ConfirmDialog.razor` | 修改 | 改为通用确认组件，接收 ConfirmOptions |
| `Components/Pages/Home.razor` | 修改 | 移除所有 MDialog 块，替换为 `<DialogHost />` |
| `ViewModels/FileListViewModel.cs` | 修改 | 注入 IDialogService，删除弹窗状态属性 |
| `MauiProgram.cs` | 修改 | 注册 DialogService（Scoped） |

## 4. 迁移步骤

### 步骤1：创建基础服务
1. 创建 `DialogModels.cs`（ConfirmOptions, ConfirmType）
2. 创建 `IDialogService` 接口（含 `OnStateChanged` 事件）
3. 创建 `DialogService` 实现（含 `TaskCompletionSource` 和并发控制）
4. 在 `MauiProgram.cs` 中注册为 **Scoped** 服务

### 步骤2：创建通用组件
1. 创建 `ConfirmDialog.razor` 通用确认组件（接收 ConfirmOptions）
2. 创建 `DialogHost.razor` 统一容器（订阅 `OnStateChanged` 驱动渲染）
3. 在 `Home.razor` 中添加 `<DialogHost />`

### 步骤3：迁移确认类弹窗
1. ViewModel 注入 `IDialogService`，用 `await ShowDeleteConfirmAsync()` 替代状态驱动
2. 删除 ViewModel 中的 `IsDeleteConfirmDialogVisible`、`IsCollectionDeleteConfirmDialogVisible` 等状态属性
3. 删除 Home.razor 中对应的 MDialog 块、本地字段、回调方法
4. 删除 `DeleteConfirmDialog.razor` 和 `CollectionDeleteConfirmDialog.razor`（被通用 ConfirmDialog 取代）

### 步骤4：迁移其他弹窗
1. 迁移压缩对话框到 DialogService
2. 迁移设置对话框到 DialogService
3. 迁移进度对话框到 DialogService

### 步骤5：清理
1. 删除 Home.razor 中所有旧的 MDialog 声明
2. 删除 Home.razor 中的 `SyncDialogState()` 方法和相关本地字段
3. 删除 ViewModel 中所有弹窗状态属性（`_compressDialogVisible` 等）
4. 测试验证所有弹窗场景

## 5. 预期收益

1. **代码简化**：Home.razor 中弹窗相关代码减少 80%+（所有 MDialog 块 → 一个 `<DialogHost />`）
2. **消除双重状态**：不再需要 SyncDialogState() 在 ViewModel 和 Home.razor 之间同步
3. **调用统一**：所有确认弹窗使用相同的 `await ShowConfirmAsync()` 模式
4. **易于扩展**：新增弹窗只需在 DialogService 中添加方法，无需修改 Home.razor
5. **类型安全**：编译时检查弹窗参数
6. **测试友好**：DialogService 易于 Mock，方便单元测试
7. **多窗口安全**：Scoped 注册确保每个窗口独立

## 6. 兼容性考虑

- 保持现有弹窗组件内部 UI 不变（CompressDialog, SettingsDialog 等）
- 仅修改弹窗的打开/关闭机制
- 不修改弹窗内部 UI 和交互逻辑

## 7. 技术要点

### 7.1 渲染通知

Blazor 服务不参与组件生命周期。`DialogService` 的状态变化必须通过 `OnStateChanged` 事件显式通知 `DialogHost` 调用 `InvokeAsync(StateHasChanged)`。`DialogHost` 在 `OnInitialized` 中订阅、在 `Dispose` 中取消订阅。

### 7.2 线程安全

`ShowConfirmAsync` 返回的 `Task<bool>` 由 `TaskCompletionSource<bool>` 支撑。`ConfirmCurrent(bool)` 在 UI 线程（Blazor dispatcher）上调用 `TrySetResult`，与调用方在同一线程，因此不需要额外的锁。

### 7.3 弹窗队列策略

当前设计采用"后来者取代"策略：如果新弹窗到达时旧弹窗未关闭，旧弹窗自动返回 `false`。如果后续需要队列化（多弹窗排队显示），可以在 `DialogService` 中引入 `Channel<ConfirmOptions>` 队列，但当前场景下不需要。
