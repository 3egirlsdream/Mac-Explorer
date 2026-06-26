# 压缩/解压密码支持设计

日期: 2026-06-26

## 目标

为 FKFinder 的压缩和解压功能添加密码支持：
- 浏览加密压缩包时自动检测并弹出密码输入框
- 解压加密压缩包时自动使用缓存的密码
- 压缩时在面板中可选输入密码
- 预览加密压缩包内的文件时支持密码

## 影响范围

| 文件 | 改动 |
|---|---|
| `Models/CompressOptions.cs` | 添加 `Password` 属性 |
| `Services/IArchiveService.cs` | 3 个读取方法添加可选 `password` 参数 |
| `Services/Impl/ArchiveService.cs` | 所有读/写操作传递密码；加密检测逻辑；密码缓存 |
| `Views/Dialogs/CompressDialog.axaml` | 添加密码输入框（带显示/隐藏） |
| `Views/Dialogs/CompressDialog.axaml.cs` | 绑定密码字段 |
| `Views/Dialogs/PasswordDialog.axaml` | **新建** — 密码输入对话框 |
| `Views/Dialogs/PasswordDialog.axaml.cs` | **新建** — 对话框逻辑 |
| `ViewModels/ArchiveViewModel.cs` | 加密检测 + 密码重试流程 + 密码缓存状态 |
| `ViewModels/FileListViewModel.cs` | 传递密码参数 |

## 技术背景

SharpCompress 0.49.1 的 API 现状：
- `ReaderOptions.Password` 可用于读取加密压缩包
- `WriterOptions` 的构造函数接受密码参数用于写入加密 ZIP
- `IArchive.IsEncrypted` **不可靠**（对所有格式返回 false），不能用于检测加密
- 检测加密的正确方法：尝试打开第一个文件条目的流，捕获 `CryptographicException` 或 `InvalidFormatException`
- 密码仅 ZIP 格式有效，tar.gz / tar.bz2 不支持密码

## 设计

### 1. 数据模型变更

`CompressOptions` 添加：
```csharp
public string? Password { get; set; }
```

### 2. 接口变更

`IArchiveService` 的三个读取方法添加可选密码参数：
```csharp
Task<IReadOnlyList<FileSystemEntry>> GetArchiveContentsAsync(
    string archivePath, string internalPath = "", string? password = null);

Task ExtractAsync(
    string archivePath, string destinationPath,
    IProgress<ArchiveProgress>? progress = null,
    CancellationToken ct = default,
    string? password = null);

Task<string> ExtractEntryToTempAsync(
    string archivePath, string entryKey, string? password = null);
```

`CompressAsync` 通过 `CompressOptions.Password` 获取密码，无需改接口签名。

### 3. 加密检测方法

`ArchiveService` 新增 `IsEncrypted(string archivePath)` 方法：
1. 以无密码方式打开压缩包
2. 找到第一个非目录条目
3. 尝试 `entry.OpenEntryStream()` 
4. 成功 → 立即 Dispose 流，返回 false
5. 捕获 `CryptographicException` / `InvalidFormatException` → 返回 true

### 4. 密码缓存

`ArchiveService` 内维护一个 `ConcurrentDictionary<string, string>`，以压缩包绝对路径为 key 缓存密码。仅在内存中，进程重启后清空。

```csharp
private readonly ConcurrentDictionary<string, string> _passwordCache = 
    new(StringComparer.OrdinalIgnoreCase);
```

三个读取方法在调用时先查缓存，缓存未命中且加密检测为 true 时返回特殊异常（如 `PasswordRequiredException`），由 ViewModel 层触发 UI。

### 5. 交互流程

#### 浏览加密压缩包

```
用户点击加密 ZIP → FileListViewModel 调用 GetArchiveContentsAsync
    → ArchiveService 无密码打开失败（加密检测触发）
    → 抛出 PasswordRequiredException
    → FileListViewModel 捕获，弹出 PasswordDialog
    → 用户输入密码
    → 缓存密码，用密码重试 → 成功，显示内容
    → 密码错误 → 提示重新输入
```

#### 解压加密压缩包

```
用户点击"解压到此处" → 从缓存获取密码，传入 ExtractAsync
    → 若缓存未命中 → 弹出 PasswordDialog → 缓存 → 解压
```

#### 预览加密压缩包内文件

```
用户选中文件 → ExtractEntryToTempAsync 使用缓存密码
    → 缓存未命中 → 弹出 PasswordDialog → 缓存 → 重试
```

#### 创建加密压缩包

```
压缩对话框 → 用户输入密码（可选）
    → ConfirmClick 将密码写入 CompressOptions.Password
    → CompressAsync 中仅当 Format == Zip 且 Password 非空时设置 WriterOptions 密码
```

### 6. UI 变更

#### PasswordDialog（新建）

- 继承 `DialogWindow`，尺寸 360×180
- 标题："此压缩包已加密，请输入密码"
- 密码输入框 + 显示/隐藏切换按钮（眼睛图标）
- 取消 / 确认 按钮
- 返回 `string?`（null = 取消）

#### CompressDialog 修改

- 在压缩级别下方添加一行：
  - 标签："密码（可选，仅 ZIP 格式）"
  - 密码输入框 + 显示/隐藏切换
- 选择 tar.gz / tar.bz2 格式时自动禁用密码输入
- `ConfirmClick` 中将密码传入 CompressOptions

### 7. 错误处理

| 场景 | 处理 |
|---|---|
| 无密码打开加密压缩包 | 弹出 PasswordDialog |
| 密码错误 | 提示"密码错误，请重试"，重新弹出对话框 |
| 用户取消密码输入 | 回退到上一级目录 / 取消操作 |
| 对 tar.gz 设置密码 | 忽略（不弹错误，压缩正常进行） |

### 8. 测试要点

- ZIP 无密码：浏览/解压/预览正常
- ZIP AES-256 加密：浏览/解压/预览弹出密码框
- 密码错误重试
- 密码缓存：同一压缩包不同操作复用密码
- 压缩对话框：输入密码创建加密 ZIP
- tar.gz 格式：密码输入框灰显
