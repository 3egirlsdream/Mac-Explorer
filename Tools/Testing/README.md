# 测试入口

日常验证用 `bash Tools/Testing/run-tests.sh fast`。入口会先增量构建，再运行全部 Unit 和共享组件 UI 冒烟检查；目标是本机已还原依赖后的增量流程不超过 15 秒。结果目录打印在终端，保留每个程序集的 XML、日志和包含构建/执行/总时长的 `summary.json`。失败时额外从 XML 输出每个失败用例的名称、消息和堆栈；发布 CI 无论测试结果如何都会上传完整报告，保留 7 天。

```bash
# 构建 + 日常检查；可把 15 秒作为可验证的整个流程预算
bash Tools/Testing/run-tests.sh fast --budget 15

# 当前二进制已由本轮构建更新时，直接运行
bash Tools/Testing/run-tests.sh fast --no-build

# 局部单元测试或页面交互（Area 可重复，取并集）
bash Tools/Testing/run-tests.sh fast --area Search
bash Tools/Testing/run-tests.sh ui --area FileList --area Sidebar

# 所有自动用例：Unit、UI、Integration、Stress；保留真实原生 helper 和 bundle
bash Tools/Testing/run-tests.sh full
bash Tools/Testing/run-tests.sh full --configuration Release

# 集成/压力场景；设备间手动互通需单独选择 manual
bash Tools/Testing/run-tests.sh integration --area LocalSend
bash Tools/Testing/run-tests.sh stress
```

`--no-build` 只用于当前代码已构建的情况。首次还原、干净构建和完整原生验证的时间另外取决于 SDK、编译与机器负载，不通过隐藏构建或跳过失败来满足预算。

## 分类与隔离

- `Kind=Unit`：纯逻辑，或使用显式临时目录/测试替身的服务行为测试。
- `Kind=UI`：Avalonia Headless 控件与页面交互。共享组件冒烟另标 `Smoke=UI`。
- `Kind=Integration`：真实网络、原生 helper、插件进程、系统元数据或后台索引调度。
- `Kind=Stress`：大规模真实文件、长期/大量重复操作。
- `Kind=Manual`：需要人工操作其他应用或设备的验证；`full` 不包含这些用例。

完整页面 UI、Integration、Stress 和 Manual 用例为显式测试。直接 `dotnet test` 默认只运行所属项目的非显式用例；推荐使用统一入口，确保两个测试程序集都被运行。发布 CI 使用 `full`。

`MacExplorer.UnitTests` 通过显式编译列表收纳不使用 dispatcher、全局语言服务或共享应用状态的测试，最多 4 个集合并行。其余测试保持串行和 Avalonia PerTest 隔离。异步 Avalonia 用例在测试入口保存 `TestContext.Current.CancellationToken`，并传给等待与子进程，避免等待过程中重新读取已释放的测试上下文。不要直接对 UI 程序集打开集合并行或改成 PerAssembly。

旧 `FileListViewModelCreateTests` 的测试已按文件拆成各功能类，创建方法与替身复用 `FileListViewModelTestBase`。使用 `Area` 筛选需要考虑调用链；共享主题或输入样式变化应运行 `ui`，LocalSend/插件/native 改动应运行对应 `integration`，发布或公共基础设施变化运行 `full`。

每个测试进程都经过 `run-isolated.sh`，使用独立 `MACEXPLORER_TEST_ROOT`。平台/发行渠道不匹配的用例报告为 Skip；可选 SFTP、真实相机 RAW、签名 Store sandbox 验证仍需要原有环境。

## 构建与时间控制

日常入口设置 `SkipMacOSBundle=true`、`SkipMacOSNativeHelpers=true`、`SkipMacOSReleaseDMG=true`，不为逻辑测试重新打包签名；文件路径与访问检查所需的轻量 macOS 平台桥仍按输入/输出增量构建并复制，图片/文档等独立 helper 仅在完整检查中构建。完整检查保留 bundle 构建。原生目标按源文件、共享 helper、脚本及构建配置的输入/输出做增量判断。快速构建跳过常规分析器，仍运行必须的源码生成器；发布 CI 的干净完整构建开启全部分析器。

LocalSend 的发现时钟与网络重连延迟使用注入的 `TimeProvider`。重试场景推进虚拟时间，实际 HTTP/TLS 仍在集成组验证。首页动画也使用可推进时钟；侧栏悬停测试注入可取消调度并核对默认 600ms，取消后无需固定等待 700/750ms。RAW 排队使用明确的排队信号；插件崩溃由进程退出通知结束 RPC，不能让超时冒充崩溃处理成功。

文件覆盖、取消后完整内容、路径越界、证书/指纹与资源回收的检查继续保留。有限 watchdog 在正常完成时立即返回；无需为了“减防御代码”删除清理或文件安全测试。

## 本机验收（2026-10-12）

- 自动全量：1362 个用例，1355 通过、7 个环境/渠道 Skip、0 失败；执行约 119 秒（原来约 185 秒）。
- 日常入口：734 个用例，732 通过、2 个渠道 Skip、0 失败；执行约 6–9 秒。一次测试源码增量构建 4.44 秒，加执行 6.88 秒，总流程 11.31 秒，通过 15 秒预算。
- 触发产品程序集重新编译：分析器优化后构建约 10.17 秒，整个流程约 18.72 秒，仍超过 15 秒；此前约 21.93 秒。预算检查会如实失败，不隐藏这部分成本。
- UI 按 FileList + Sidebar：109 个用例全部通过，执行约 6.39 秒。Search 日常选测：57 个用例全部通过，约 1.71 秒。
- 移除测试目录中的三个独立 helper，并指定不存在的应用/插件 fixture 路径时，日常入口仍全部通过（保留轻量平台桥）；证明它不依赖旧 bundle 或这些 helper。
- 主要慢场景：LocalSend 重试 35.89 → 0.67 秒，8 台设备上限 10.35 → 0.44 秒；RAW 排队 3.19 → 0.14 秒；动画循环 3.80 → 0.01 秒；插件带子进程崩溃 5.22 → 0.39 秒。

以上为单机样本，运行时间会受编译缓存和机器负载影响。15 秒适用于日常执行及缓存增量流程；首次构建、产品重编译和真实原生全量检查不能保证在 15 秒内。

## 窗口首开与设置按需加载验收

macOS Debug 构建完成后，用独立配置运行真实窗口检查：

```bash
dotnet build MacExplorer.csproj -c Debug -p:SkipMacOSReleaseDMG=true
MACEXPLORER_WINDOW_OPEN_SMOKE=1 bash Tools/Testing/run-isolated.sh \
  'bin/Debug/net10.0/osx-arm64/Mac Explorer.app/Contents/MacOS/MacExplorer'
```

该入口仅在 Debug 且设置了 `MACEXPLORER_TEST_ROOT` 时生效；依次打开设置、确认、压缩、任务、远程连接、重命名、图片转换、删除、密码、LocalSend、首页编辑、新主窗口和文件速递，不执行文件操作。报告写入终端打印的临时目录 `window-open-smoke.json`，随后退出。设置首开必须仅创建一个页面，逐页进入后检查延迟内容已创建；每个窗口显示调用返回时，外框必须已经铺满客户端区域。

`constructMs` 是构造耗时，`showMs` 包括构造和同步显示，`commitMs` 记录首轮合成提交在渲染线程应用后的时间，不等同于显示器实际呈现时间。首次与再次打开分别记录，不以单机毫秒阈值作为跨机器单元测试断言；配合保留四周外沿的原生录屏，检查没有白边或中心扩散。浅／深色和窄窗口通过 UI 检查补充覆盖。

## 标签与剪贴板原生验证

```bash
dotnet build MacExplorer.csproj -c Debug -p:SkipMacOSReleaseDMG=true
MACEXPLORER_TAG_CLIPBOARD_SMOKE=1 bash Tools/Testing/run-isolated.sh \
  'bin/Debug/net10.0/osx-arm64/Mac Explorer.app/Contents/MacOS/MacExplorer'
```

该入口仅在 Debug 隔离实例中运行，检查标签间复制／剪切、真实子目录复制／移动、取消副本标签，以及外部进程写入的多文件 Finder 引用 URL。使用独立命名剪贴板，报告与窗口截图保存到测试目录的 `tag-clipboard/`，结束后退出。自动回归使用 `fast`、`ui --area UnifiedTagInteraction --area Localization` 和 `integration --area UnifiedFileTag --area Clipboard --area FileDrop`。

## 后台任务圆环原生验证

使用新构建的 Debug 应用及独立配置执行，合成任务仅用于验证显示，不操作用户文件：

```bash
dotnet build MacExplorer.csproj -c Debug -p:SkipMacOSReleaseDMG=true
MACEXPLORER_TASK_RING_SMOKE=1 bash Tools/Testing/run-isolated.sh \
  'bin/Debug/net10.0/osx-arm64/Mac Explorer.app/Contents/MacOS/MacExplorer'
```

报告及浅／深色、0%／25%／50%／100%、流动、暂停和不确定进度截图保存在打印的测试目录下 `task-progress-ring/`。报告检查运行中的实际动画帧增长，暂停、隐藏、最小化、移除和关闭后帧数停止，以及关闭后的绘图资源释放；结束后自动退出。普通逻辑与交互回归使用 `fast --area FileTasks` / `ui --area FileTasks`。

## WebDAV 原生验证

WebDAV 使用 macOS NetFS 和系统文件系统。原生检查以新的 Debug 应用、macOS 自带 Apache 的临时 HTTP WebDAV 服务、用于证书拒绝及取消的 Python 服务和独立配置目录运行；服务只监听 loopback，测试账号、文件和自签名证书均为一次性数据，不导入系统信任，不读取真实远程配置。执行前准备独立 Python 环境并安装 `WsgiDAV==4.4.0`、`cheroot==11.1.2`：

```bash
python3 -m venv /tmp/macexplorer-webdav-qa
/tmp/macexplorer-webdav-qa/bin/pip install WsgiDAV==4.4.0 cheroot==11.1.2
dotnet build MacExplorer.csproj -p:SkipMacOSReleaseDMG=true
MACEXPLORER_WEBDAV_TEST_PYTHON=/tmp/macexplorer-webdav-qa/bin/python \
  bash Tools/Testing/run-tests.sh integration --area Network
```

该检查覆盖认证失败、未受信任证书、进行中取消、实际挂载目录、应用内浏览、下载内容、上传／重命名／删除、重复连接、两个窗口同步和弹出。`webdav-progress.json` / `webdav-smoke.json` 写入隔离应用目录；服务请求日志保留在打印的 fixture 目录。超时或系统挂载失败按失败报告，不能把文件系统出现临时卷当作挂载成功。未配置 Python 环境或非 Debug 构建时跳过此可选检查；忙碌卷失败同时验证真实打开文件和替身行为，地址／账号／卷身份匹配由单元测试覆盖。

可设置 `MACEXPLORER_WEBDAV_TEST_SERVER=wsgidav` 复现 WsgiDAV 4.4.0 兼容性检查。当前 macOS 27.0.1 对该服务认证成功后仍阻塞在系统挂载调用，须如实记录失败；Apache 的成功不能替代该兼容性结果。Apache 使用独立配置和进程，仅监听临时 loopback 端口，不启用或修改系统 Web 共享服务。

## 文件速递暂存原生验证

```bash
dotnet build MacExplorer.csproj -c Debug -p:SkipMacOSReleaseDMG=true
MACEXPLORER_DELIVERY_SMOKE=1 bash Tools/Testing/run-isolated.sh \
  'bin/Debug/net10.0/osx-arm64/Mac Explorer.app/Contents/MacOS/MacExplorer'
```

该入口仅在 Debug 与独立测试目录中运行，生成文件、长文件名、文件夹和不可用路径，检查暂存展示、清空后源文件不变，并输出四语浅／深色及窄窗口截图。结果位于打印的测试目录 `file-delivery/`，`report.json` 的 success 为验收结果。设置 `MACEXPLORER_DELIVERY_SMOKE=interactive` 会保留实例及 `Documents/DeliveryFixture`，用于 Finder／本程序实际拖入、拖出和取消验证；`state.json` 仅在该显式测试模式下记录测试路径、窗口可见性和原生拖拽计数。普通自动回归运行 `fast`、`ui --area FileDelivery --area Localization`、`integration --area FileDelivery`。
