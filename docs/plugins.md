# 插件 SDK 开发指南

使用 SDK 开发文件处理插件，打包为 `.mexplug`，再通过开发者中心发布。

## 1. 准备环境

- [下载完整 SDK](developers/sdk.html)，解压后保留整个目录。
- 安装 .NET 10 SDK。首次还原依赖需要联网。
- 使用 Mac Explorer 1.0.44 或更新的兼容版本安装、调试插件。
- SDK 1.0.0 支持协议 API v1 / v2，新插件推荐 API v2。窗口 SDK 使用 Avalonia 12.0.4。

SDK 目录：

| 目录或文件 | 用途 |
| --- | --- |
| `packages/` | 基础 SDK、窗口 SDK 的本地 NuGet 包 |
| `examples/SimplePlugin/` | 最小文件处理示例 |
| `examples/AccountPlugin/` | 账号、授权和试用窗口示例 |
| `tools/MacExplorer.PluginPack.dll` | 插件打包工具 |
| `src/` | SDK 和打包工具源码 |
| `NuGet.Config` | 本地及公共依赖的包源配置 |

## 2. 运行第一个插件

在 SDK 解压目录执行：

```sh
dotnet restore examples/SimplePlugin/SimplePlugin.csproj --configfile NuGet.Config
dotnet build examples/SimplePlugin/SimplePlugin.csproj -c Release --no-restore
dotnet tools/MacExplorer.PluginPack.dll examples/SimplePlugin/bin/Release/net10.0 SimplePlugin.mexplug
```

打开“设置 → 插件 → 安装插件…”，选择 `SimplePlugin.mexplug`。右键一个本地 `.txt` 文件，选择“文本副本示例 → 生成文本副本”，检查当前目录生成的文件。

## 3. 开发自己的插件

复制 `examples/SimplePlugin` 到同级目录开始开发，保留示例的 SDK 包引用、版本属性导入和 `EnableDynamicLoading`。如果调整目录层级，需要同步修改版本属性文件路径及 NuGet 包源路径。

基础插件引用 `MacExplorer.PluginSdk`；需要独立窗口时引用 `MacExplorer.PluginUi`。修改项目名称后，同步修改清单中的入口程序集名称。

### 定义菜单和文件匹配

在构建输出中包含 `plugin.json`：

```json
{
  "id": "com.yourcompany.text-copy",
  "name": "文本副本",
  "version": "1.0.0",
  "apiVersion": 2,
  "entry": "SimplePlugin.dll",
  "icon": "document",
  "description": "为文本文件生成副本",
  "platform": "osx",
  "architecture": "arm64",
  "commands": [
    {
      "id": "copy",
      "title": "生成文本副本",
      "icon": "document",
      "match": {
        "extensions": [".txt"],
        "minSelection": 1,
        "maxSelection": 1
      }
    }
  ]
}
```

- `id`：使用自己的稳定插件 ID，发布后保持不变，不要沿用示例或官方插件 ID。
- `entry`：插件入口 DLL 文件名，与实际编译输出一致。
- `name`：右键一级菜单名称；`commands` 定义二级功能项，命令 ID 应保持稳定。
- `icon`：支持 `convert`、`document`、`image`、`apps`，对应 Fluent 图标。
- `architecture`：使用与目标环境及原生依赖一致的 `arm64` 或 `x64`。
- `match`：`extensions`、`fileNames`、`textFiles` 任意一项匹配即可；选中文件数量必须在范围内，且每个文件都适用。数量默认均为 1。

当前支持本地普通文件，不匹配目录、远程文件、废纸篓和压缩包内部文件。菜单匹配不读取文件内容，执行时仍需校验输入。

### 实现文件处理

入口程序集提供唯一的公开、非抽象 `IFileActionPlugin` 实现，并包含无参数构造函数。可直接修改示例中的 `SimplePlugin.cs`。

| 接口 | 开发时需要完成的工作 |
| --- | --- |
| `PrepareAsync` | 校验命令和文件，返回 `PluginPreparation`；不需要配置时返回空配置 |
| `ExecuteAsync` | 读取输入文件，在 `invocation.WorkDirectory` 中生成结果，返回 `PluginResult` |

调用信息 `PluginInvocation` 包含调用 ID、命令 ID、文件数组、工作目录和参数。结果通过 `PluginOutput` 返回暂存文件路径及建议名称，例如：

```csharp
return new PluginResult(
    [new PluginOutput(outputPath, "文本副本.txt")],
    []);
```

- 保留源文件，输出必须位于工作目录内，是非空普通文件，不能是符号链接。
- 建议名称只包含文件名，不包含目录。返回后由应用保存最终文件并处理重名。
- 用 `IProgress<PluginProgress>` 报告阶段；有真实百分比时传入 0–100，否则使用 `null`。
- 响应 `CancellationToken`，及时停止读写并释放资源。辅助进程使用 `PluginChildProcesses.Track` 注册，同时在取消或异常时清理。
- 不要直接写原始标准输出；使用 `Console.Error.WriteLine` 记录诊断信息，可在插件管理页查看日志。

准备阶段限时 30 秒，执行阶段限时 2 分钟，交互配置等待不计入执行时间。同一插件同时只运行一个会话；失败不会自动重试。

### 添加尺寸配置

`PrepareAsync` 可返回 `PluginConfiguration`，目前支持 `image-size`：提供标题、正数原始宽高及 `Png` / `Jpg` 格式。应用显示尺寸配置窗口，并通过调用参数返回 `width`、`height`。用户取消时停止本次调用；其他配置类型暂不支持。

## 4. 添加账号、付费或试用功能

以 `examples/AccountPlugin` 为起点，清单声明 `apiVersion: 2`、`paid`、`trialDays` 和 `hasUserInterface`，实现 `IPluginAccessProvider`。

每次点击功能菜单后，SDK 调用 `CheckAccessAsync` 检查使用资格；展开菜单不会进行检查。返回值含义如下：

| 状态 | 含义 |
| --- | --- |
| `Allowed` | 已有有效使用授权 |
| `LoginRequired` | 需要登录 |
| `PurchaseRequired` | 需要购买 |
| `TrialAvailable` | 可申请或继续试用，是否有效由应用的试用记录决定 |
| `TrialExpired` | 试用已到期 |
| `Failed` | 资格检查失败 |

付费授权有效时返回 `Allowed`，不应被试用到期阻止。网络异常应返回 `Failed` 或抛出异常，不能当作已授权。

试用按插件 ID 共享。用户主动选择“开始试用”后计时，开始和到期时间不可通过 SDK 修改；保留应用数据时，卸载重装、更新、降级及切换账号都不会重新领取。通过 `PluginAccessRequest.Trial` 读取快照。跨设备授权及账号购买资格需要接入自己的服务。

### 显示独立窗口

在 `ShowAccountAsync` 中使用窗口 SDK：

```csharp
var completed = await PluginWindows.ShowAsync(() => new YourWindow(), token);
return new PluginInteractionResult(completed);
```

窗口操作成功后调用 `PluginWindows.Complete(window)`；直接关闭窗口表示取消。`Purpose` 为 `authorize` 时用于当前调用授权，为 `manage` 时用于账号管理。授权完成后会再次检查资格，再继续准备和执行。

窗口中的登录、购买和账号状态由插件实现。示例只模拟登录与购买，授权仅在当前会话生效，不产生交易；发布前替换为真实的账号与授权逻辑。

```sh
dotnet build examples/AccountPlugin/AccountPlugin.csproj -c Release --configfile NuGet.Config
dotnet tools/MacExplorer.PluginPack.dll examples/AccountPlugin/bin/Release/net10.0 AccountPlugin.mexplug
```

## 5. 打包与本地验证

打包时传入完整构建输出目录，其中应包含清单、入口 DLL、依赖描述文件、所需依赖和资源：

```sh
dotnet tools/MacExplorer.PluginPack.dll <构建输出目录> <插件名.mexplug>
```

工具拒绝路径越界、符号链接、无效入口和覆盖已有输出。再次打包请更换输出文件名；原生辅助程序需保留执行权限，并按目标平台签名。

发布前安装生成的包，验证菜单匹配、实际输出、重名、取消、损坏输入及错误提示。有账号或试用功能时，同时检查窗口关闭、登录失败、授权到期及卸载重装行为。

## 6. 发布插件

1. 打开[开发者中心](developers/index.html)，使用用户名或邮箱登录；没有账号时先注册。
2. 选择打包好的 `.mexplug`，核对页面显示的插件名称、版本、描述和试用信息。
3. 上传并等待校验完成，确认发布状态成功。
4. 在“我的插件”查看已发布内容，再到插件市场搜索、安装验证。

登录后即可发布，无需额外验证邮箱。同一插件 ID 只能由首次绑定的账号更新，同一版本不可覆盖。发布新版时递增三段版本号（如 `1.0.1`），保持插件 ID 和命令 ID 不变，再上传新包。

“我的插件”支持发布新版和下架。下架会停止市场分发，不会自动删除用户已经安装的插件。

## 许可

SDK、示例和打包工具采用 MIT。依赖的许可说明见 SDK 包中的 `THIRD-PARTY-NOTICES.md`。
