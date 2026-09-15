# Mac Explorer 文件操作插件（API 1 / 2）

插件是可信的本地 .NET 10 代码，拥有当前用户权限。独立进程隔离崩溃和依赖，不是文件或网络权限沙箱。

## 使用

设置 → 插件中安装 `.mexplug`，或启用、禁用、卸载已有插件。文件转换插件随应用默认安装；卸载后可通过“恢复内置文件转换插件”恢复。升级应用保留启用和卸载选择。

适用插件以自己的名称显示为文件右键一级菜单，二级菜单是适用于整个选择的命令。文件转换目前只接受一个本地文件，保持既有格式和转换结果。

运行中的任务固定使用启动时的版本。更新只影响下一次调用。禁用和卸载禁止新调用，已有任务完成后清理旧版本；取消任务可提前结束。首版每个插件同时执行一个任务。

## 开发

引用 `Plugins/SDK/MacExplorer.PluginSdk.csproj`，实现唯一的公开、非抽象 `IFileActionPlugin` 类型及无参数构造函数。SDK 不引用 Avalonia 或主程序。启用 `EnableDynamicLoading`，将插件程序集、`.deps.json`、依赖和资源与 `plugin.json` 一起打成 ZIP，扩展名改为 `.mexplug`。原生可执行文件必须保留 Unix 执行位，并按目标平台签名。

参考 `Plugins/FileConversion/plugin.json` 与 `ConversionPlugin.cs`。清单包含 `id`、`name`、`version`、`apiVersion: 1`、`entry`、`icon` 和 `commands`；命令包含 `id`、`title`、`icon`、`match`。图标支持 `convert`、`document`、`image`、`apps`，均由宿主提供 Fluent 图标。

匹配规则中 `extensions`、`fileNames`、`textFiles` 为“或”，选择数量 `minSelection`、`maxSelection` 和各文件条件为“且”。默认选择数量均为 1。API 1 仅支持本地普通文件。打开菜单只读取清单和已有文件元数据，插件需在执行时重新校验内容。

- `PrepareAsync`：检查文件并返回配置描述，或无配置。API 1 的 `image-size` 配置包含正数原始宽高、标题、`Png`/`Jpg` 格式；宿主复用比例联动的尺寸窗口并返回 `width`、`height` 参数。其他配置类型会显示不兼容错误。
- `ExecuteAsync`：读取传入的文件，向宿主创建的 `WorkDirectory` 写入结果，返回输出路径、建议文件名和警告。不要修改源文件。输出必须是工作目录内的非空普通文件，不能使用符号链接或带目录的建议名称。
- 宿主负责最终保存和重名处理。多输出按顺序保存；首个结果提交后完成其余结果，取消不回滚已提交的完整文件。文件转换仅返回一个结果。
- 通过 `IProgress<PluginProgress>` 报告阶段或真实的 0–100 进度；没有可靠百分比时使用 `null`。异常作为任务错误返回，不自动重试。
- 响应 `CancellationToken`，启动辅助进程时用 `PluginChildProcesses.Track` 注册，并在插件自身的取消/异常处理里等待和清理子进程。

## 通信与生命周期

应用通过 `--plugin-worker <插件版本目录>` 启动自身的独立进程，在初始化 Avalonia 前分流。工作进程使用独立 AssemblyLoadContext 加载插件依赖，与宿主共享 SDK 类型标识。

通信为每行一条 UTF-8 JSON-RPC 2.0 消息。`initialize` 返回 API 版本与 PID；`prepare`、`execute` 的参数为 `PluginInvocation`；`progress` 为工作进程通知；`cancel` 为宿主通知。标准输出专用于协议，`Console.WriteLine` 被工作进程重定向到标准错误日志。不要直接写原始标准输出。

准备超时 30 秒，执行超时 2 分钟，配置窗口等待不计入执行时间。取消后给予 2 秒退出时间，再终止进程树。通信关闭会取消操作并终止已注册辅助进程。

安装目录位于 `LocalApplicationData/MacExplorer/Plugins`（可通过 `MACEXPLORER_PLUGIN_PATH` 指定独立测试目录），启用、版本和卸载状态保存在现有设置数据库的 `plugins_state_v1`。日志可在管理页查看；每个插件保留最近一次调用日志，最多 100 万字符。

## 构建与验证

`dotnet build MacExplorer.csproj -p:SkipMacOSReleaseDMG=true` 同时构建插件、Swift 辅助程序并生成 `bin/<配置>/net10.0/osx-arm64/BundledPlugins/FileConversion.mexplug`，应用包内也包含该文件。更新转换插件代码时应递增独立的插件版本，已安装相同版本不会在启动时被覆盖；开发时可手动安装生成的包或恢复内置插件。

转换引擎回归测试继续验证内容、布局、图片和文件完整性。`PluginSystemTests` 从构建后的 `.app` 启动真实工作进程，覆盖打包资源、转换、热更新/卸载、超时、崩溃、协议故障和子进程清理。

## API 2：账号、授权与试用

免费 API 1 插件继续可用。付费或自有窗口插件声明 `apiVersion: 2`、`paid`、`trialDays`（0 表示不提供试用）、`hasUserInterface`，并实现 `IPluginAccessProvider`。可选展示字段为 `description`、`developer`、`platform`（目前 osx）、`architecture`（arm64/x64）。市场中的开发者名称由发布账号确定。

宿主每次命令调用执行 `check-access`，参数 `PluginAccessRequest` 包含固定的调用和只读试用快照。状态按 SDK 枚举编码：Allowed=0、LoginRequired=1、PurchaseRequired=2、TrialAvailable=3、TrialExpired=4、Failed=5。Paid 有效时返回 Allowed；免费试用路径返回 TrialAvailable，宿主仅在自己的记录仍有效时放行。网络失败应返回 Failed 或抛出异常，不能返回 Allowed。菜单展示不执行账号检查。

用户主动确认“开始试用”后，宿主在独立设置键 `plugin_trials_v1` 中原子保存首次开始和固定到期时间。重装、更新、降级和账号切换不会重新发放试用。SDK 不提供删除或重置试用的接口；损坏的记录报错，不自动清空。时间检查保留历史最大值，普通回拨不会延长试用；这不是防篡改或跨设备授权系统。付费授权由插件自己的服务端负责。

`account` 请求调用 `ShowAccountAsync`，Purpose 为 authorize 或 manage；返回 Completed=false 表示用户取消。交互时间不限，主程序退出、设置窗口关闭或用户取消任务时仍会发送取消并回收进程。授权通过后继续同一调用的 prepare 和 execute。

### 插件自己的 Avalonia 窗口

引用 `Plugins/UI/MacExplorer.PluginUi.csproj`，使用 `PluginWindows.ShowAsync(() => new YourWindow(), token)` 在插件进程 UI 线程创建窗口；成功后调用 `PluginWindows.Complete(window)`，普通关闭表示取消。当前 UI SDK 使用宿主提供的 Avalonia 12.0.4，不要携带不兼容版本。账号窗口不在主界面进程创建，也不能访问其窗口对象。

参考 `Plugins/Examples/AccountPlugin`：该插件仅模拟登录和购买，授权只在当前会话生效，不会扣款。生产插件需替换为自己的账号 API、安全保存会话并实际检查购买资格，不能把示例按钮当作生产授权。

```sh
dotnet build Plugins/Examples/AccountPlugin/AccountPlugin.csproj -c Release
# 将 bin/Release/net10.0 的内容（不是外层文件夹）压成 .mexplug。
```

## 市场与开发者发布

设置 → 插件左侧管理已安装插件，右侧搜索、查看详情和下载安装。下载先验证长度、SHA-256 和清单身份，再原子切换版本。市场下架不主动删除本地插件。市场服务暂不可用时仍可本地安装、管理和使用已有插件。

GitHub Pages 开发者入口为 `developers/index.html`，接口地址配置在 `developers/config.js`。默认 HTTPS API 为 `https://thankful.top/api/PluginMarket/`；客户端可通过 `MACEXPLORER_MARKET_URL` 配置相同服务。HTTP 仅允许本机回环地址用于隔离验收。

开发者使用现有 Watermark 账号，邮箱验证后上传 `.mexplug`。包直传七牛，后端验证后自动上架；同一 ID 归属首次申请上传的账号，同一版本不可覆盖。发布页支持登录、注册、验证码、找回密码、查看本人插件和下架。浏览器会话保存在 sessionStorage，密码不会保存。平台不参与插件最终用户的付费订单。

后端代码、SQL 迁移和 API 说明位于 Server.NetCore 仓库的 `docs/plugin-market.md`。本次交付不自动部署生产后台、执行生产迁移或发布 GitHub Pages。
