# 原生 LocalSend 实现与审查记录

日期：2026-09-29。本文记录开发与审查当时的工作区状态；提交、推送及发布状态以 Git 和 GitHub 为准。

## 功能范围

- 文件右键菜单：发送到 → LocalSend（官方图标）→ 设备。异步发现、刷新、取消加载和过期请求隔离复用现有菜单渲染器。“通过 IP 连接…”接受 IPv4 和端口（默认 53317），核对设备信息与 TLS 证书指纹后加入本次运行的设备列表；连接成功不发送文件，用户重新打开菜单选择设备发送。
- 本地文件、多选和目录按相对路径发送；父子选区去重。远程位置、压缩包内部、废纸篓和只读速递面板不提供入口。
- 运行期间接收，用户确认后写入；默认下载目录，测试模式使用隔离 Downloads。重名保留两份，取消/失败保留已完成文件、清理临时文件。
- 设置“发送到”提供统一 LocalSend 开关、设备名称、接收目录和只读的实际监听端口。关闭同时停止发现、接收和未完成传输；全部禁用不保留空父菜单。
- 展开或刷新 LocalSend 设备菜单时自动搜索，无须填写网段；设备在搜索尚未结束时会逐步出现并可直接选择。设置中保留可选的“高级扫描网段”（单个 IPv4 CIDR，/24–/32），仅用于额外指定范围。
- 空目录、符号链接及特殊文件跳过并提示。首版不含自动接收、断点续传、文本、浏览器分享或多设备群发。

## 实现边界

`ILocalSendService` 是唯一应用入口。`LocalSendService` 的主体与 Discovery / Send / Receive 部分分别负责生命周期、发现、协议发送和接收文件处理。界面通过确认/PIN 回调与服务协作，网络实现不进入文件列表 ViewModel。

接收端使用 .NET 10 Kestrel；自包含包携带 `Microsoft.AspNetCore.App`。客户端独立配置 HTTPS 证书 SHA-256 指纹校验，不修改全局校验。证书身份保存在应用数据目录。UDP 保持默认组播组 `224.0.0.167:53317`，按活动 IPv4 网卡发送公告；TCP 优先监听官方默认端口 `53317`，已被占用时由系统分配动态端口，并在公告、`/info` 和设置页显示实际端口。我方先占用默认端口时，随后启动的官方客户端可能需要选择其他端口。手动 IP 连接先从指定 HTTPS 端点读取 `/info`，核对响应指纹与实际证书，再用固定指纹向同一端点注册；官方 `/info` 可不含 `port` 或 `protocol`，此时使用用户输入的端口和 HTTPS。已知设备在菜单刷新时定向注册以更新状态；菜单展开和刷新默认执行下述有界自动扫描。同一时刻允许一个发送会话和一个接收会话，接收兼容会话内并行上传；接收元数据限制为 4 MiB、最多 10,000 项。

高级 CIDR 填写后加入快速搜索候选；快速阶段最多 10 秒，端口开放后的 HTTPS 核验每地址限 800 ms，网络失败不逐条显示错误。未知端点沿用手动 IP 连接的 HTTPS 信息读取、证书指纹核对及固定指纹注册，info/register 响应限制在 64 KiB。

自动搜索先用组播、已知设备地址、当前有 IPv4 网关的活动物理接口所在 `/24`（实际前缀更窄则遵从），以及同网络环境下已核验设备的旧 `/24`。随后在这些物理接口所属的 RFC1918 `/16` 内按需扩大候选：优先本机 `/20` 与历史 `/24`，多接口交错，再试余下地址；不扫描 `10/8` 或公网，也不由 DHCP 地址臆造手机所在子网。扩大阶段最多 65,536 个候选、150 秒总预算，TCP 53317 预探测最多 128 并发、每地址 250 ms；端口开放后仍须经过 HTTPS 信息、证书指纹和注册核验，HTTPS 最多 32 并发。时间上限不保证扫完整个 `/16`。网络切换、关闭菜单或关闭 LocalSend 会取消旧轮次；旧批次进度不得回写新菜单。扫描进度仅在设备集合变化时更新，保留已显示的设备行与当前点击动作。

设备历史仅在 HTTPS 证书指纹核对和注册成功后写入现有设置，最多 8 条，按接口子网及网关标识匹配当前网络；UDP 公告和未核验的入站注册不写历史。相同指纹在新 IP 上再次核验后更新该网络的地址，此指纹核对并不构成额外的用户安全配对。旧地址不变时一天内不重复写设置。高级 CIDR 仍兼容旧配置；非默认端口设备仍可经组播或手动 IP 连接发现。

配置复用 `ISettingsService`，进度复用后台任务面板；不新增数据库表、通用传输框架或第二套菜单系统。UI 复用 `DialogWindow`、设置分组、紧凑按钮/开关和现有弹出层样式。

协议依据：[LocalSend v2 协议](https://github.com/localsend/protocol)。图标来源及许可随 `Assets/localsend-ATTRIBUTION.txt`、`Assets/localsend-LICENSE.txt` 打包。

## 开发后审查

开发由独立会话 `原生 LocalSend 收发与发送到菜单开发` 执行，模型 `gpt-6-sol`，推理 `xhigh`。当前会话检查实际差异，将问题交回修复并复查。

已修复的主要问题：

- Kestrel 默认请求大小限制阻止大文件上传；文件流上传单独解除该限制，元数据仍有界。
- 文件枚举阻塞 UI、FIFO 被当成普通文件读取；改为后台可取消枚举并识别 macOS 普通文件。
- 巨量文件长度求和溢出遗留任务；在创建任务前验证元数据。
- 接收断流、校验失败和取消未及时释放会话；结束会话、注销回调并在活动上传退出后释放资源。
- 固定整文件超时会截断活跃大文件；上传按无进展时间限制。
- 官方客户端占用 IPv4 端口时 Kestrel 仅绑定 IPv6，导致错误公告；显式绑定 IPv4。最终按默认端口优先、已占用回退动态端口处理，设置页显示实际端口，便于另一端手动指定。
- 手机与本机分处经路由连接的不同子网，组播发现未找到对方，而定向注册成功；加入单次手动 IP 连接，证书指纹核对通过后可在设备列表选择发送，不改变组播 socket 策略。未进行网络设备抓包，不将这一现象写成已确认的具体网关丢包规则。
- Rider 的 Avalonia 设计器也执行 `App.OnFrameworkInitializationCompleted`；修复前多个预览进程各自监听 LocalSend，并共用正式应用证书指纹。现仅在非设计模式且实际创建经典桌面主窗口时注册回调并启动，设置页的启动与开关入口采用相同边界。
- 官方 LocalSend 1.16.1 的 HTTP 后备扫描按旧版地址请求 `/api/localsend/v1/info`；原服务仅提供 `/v2/info`，该请求实测返回 404。两个 info 地址现共用处理器，返回声明 `version=2.2` 的设备信息以供官方选择 v2 后续路由；查询指纹等于本机时返回 412。未添加 v1 文件传输路由。
- 默认组播网卡与官方客户端加入网卡不同导致发现失败；按活动 IPv4 网卡加入和公告。
- 64 KiB 一次进度回调频繁刷新任务面板；约 100 ms 节流，文件切换/结束保留即时更新。
- 新请求忙碌处理与任务取消存在反向锁顺序；任务面板操作移出接收锁。
- 官方客户端在接收确认前发送不带会话 ID 的取消请求；兼容同一来源取消待确认请求，正式会话仍校验 ID。
- 整个菜单重建影响异步子菜单与原有打开方式；只在异步设备子菜单展开时延后整体替换，关闭取消加载并拒绝迟到结果。
- 迟到结果测试原先可能在异步结果返回前断言；现在等待发现返回再验证，并补充设备菜单触发发送与正确选区路径的测试。

审查判断：职责划分与本次功能规模相符，没有发现需要保留的通用框架或无用途服务接口。并发会话清理状态用于处理并行上传和取消，仍需通过真实使用持续观察。

## 已完成验证

| 验证 | 结果 | 证据 |
| --- | --- | --- |
| LocalSend 服务自动测试 | 17 项发现，16 通过；另 1 个显式官方互通测试不随普通批次运行。新增默认端口占用回退、并发启动、手动 IP 缺省字段/指纹核对、关闭时取消连接 | `/private/tmp/fk-localsend-manual-service-tests.log` |
| 旧版 info 发现回归 | 18 项发现，17 通过；另 1 项显式官方互通测试未运行。新增测试按 `/v1/info?fingerprint=...` → v2 版本 → `/v2/register` 检查，并断言自身指纹返回 412 | `/private/tmp/fk-localsend-legacy-info-service-tests.log` |
| 指定网段扫描回归 | 20 项发现，19 通过；另 1 项显式官方互通测试未运行。覆盖 CIDR 规范化与拒绝无效范围、证书指纹不匹配、64 KiB 响应上限、取消前不注册、成功后可见及单地址超时保留已知设备；菜单关闭取消定向测试通过 | `/private/tmp/fk-localsend-cidr-service-tests.log`、`/private/tmp/fk-localsend-cidr-menu-cancel-test.log` |
| 自动搜索定向回归 | 服务 22 项发现，21 通过、1 项显式官方互通未运行；菜单 5/5 通过。新增候选范围/去重、跨网络历史过滤、重复核验不重写设置、网络切换取消连接、搜索中选择设备与保留同一菜单行、刷新及关闭后拒绝旧进度 | `/private/tmp/fk-localsend-auto-service-tests.log`、`/private/tmp/fk-localsend-auto-menu-tests.log` |
| 原 Wi-Fi 空配置自动发现 | 隔离新 profile 仅设置设备别名，未配置 CIDR 或历史设备，未向扫描算法传手机 IP；在约 12,035 ms 的进度回调中发现手机 `172.20.235.80:53317`，别名“快速的土豆”。整轮在 121,844 ms 正常结束，退出码 0，结果仍保留手机，临时监听已释放；未发送文件 | `/private/tmp/fk-localsend-auto-audit/real-auto.log` |
| 共享菜单渲染回归 | 新建菜单图标、转换子菜单右键展开、打开方式行为共 7 项通过 | `/private/tmp/fk-localsend-auto-renderer-regression.log` |
| 自动搜索最终原生实例 | 隔离新 Debug 应用 PID 95295，profile `fkfinder-test.r05Fx2`；设置显示高级网段为空、占位提示“自动搜索，无需填写”，监听 53317，接收目录为隔离 Downloads。已打开测试文件所在目录；独立子菜单仍受 CUA 可见性限制，等待用户确认设备显示 | `/private/tmp/fk-localsend-auto-audit/native-app.log`；CUA 原生设置检查与 `lsof` |
| TCP 默认端口与回退 | 官方占用 53317 时我方回退动态端口；官方退出、53317 空闲时我方监听 53317，真实手机手动连接均成功 | `/private/tmp/fk-localsend-discovery-audit/after.log`、`/private/tmp/fk-localsend-discovery-audit/default-port.log` |
| 手动 IP 连接真实手机 | 连接 `172.20.235.80:53317`，核对证书指纹后注册成功；三轮设备发现保留对端，未发送文件 | `/private/tmp/fk-localsend-discovery-audit/after.log` |
| 原生设置状态与同进程页签 | 隔离新包显示实际端口 53317；关闭 LocalSend 后 TCP 监听消失，官方占用默认端口后重新开启显示 60847 且与 `lsof` 一致，未见预期 `Hosting failed`；新增第二页签仍只有该进程一个 TCP 和一个 UDP 监听 | `/private/tmp/fk-localsend-ui-audit/native.log`，原生设置页与 `lsof` 检查 |
| Rider 设计器启动边界 | 修复前多个预览进程分别监听 LocalSend TCP/UDP；修复后用同一 `Avalonia.Designer.HostApp.dll`、最终 Debug DLL 和隔离 profile 启动新 Host，日志确认设计模式及 `Sending StartDesignerSessionMessage`，存活 6 秒仅有测试传输连接，无该 Host 的 LocalSend TCP/UDP 监听。Avalonia 源码中此日志发生在 `SetupWithoutStarting` 完成之后，故已执行应用初始化。53317 的另一个监听者是独立真实 MacExplorer 进程 | `/private/tmp/fk-localsend-designer-audit/before-processes.log`、`before-tcp.log`、`before-udp.log`、`after-host.log`、`after-host-sockets.log`；[Avalonia 12.0.4 设计器入口](https://raw.githubusercontent.com/AvaloniaUI/Avalonia/12.0.4/src/Avalonia.DesignerSupport/Remote/RemoteDesignerEntryPoint.cs) |
| 无桌面生命周期 | 临时 headless 启动器以隔离 profile 执行当前 `App` 的 `SetupWithoutStarting`，输出 `ApplicationLifetime=null`；存活期间该进程无 TCP/UDP socket。探针位于 `/private/tmp`，未加入项目 | `/private/tmp/fk-localsend-designer-audit/no-desktop-run.log`、`no-desktop-sockets.log` |
| 真实 Debug 应用启动 | 隔离的新 Debug 应用监听 TCP/UDP 53317，`/info` 公告 53317；与设计器无监听的结果分别核对 | `/private/tmp/fk-localsend-designer-audit/real-debug-app.log` |
| 菜单资格、关闭后迟到结果、设备项发送选区、长状态对话框布局 | 4/4 通过 | `/private/tmp/fk-localsend-manual-menu-tests.log` |
| 原有打开方式、输入/下拉样式、路径隔离 | 15/15 通过 | `/private/tmp/fk-localsend-review-regression.log` |
| 独立真实 HTTPS 探针 | 异常元数据返回 400 且无遗留任务；断流后新请求返回 200 | `/private/tmp/fkfinder-localsend-audit-vxka0li6/final.log` |
| 独立 PIN 与证书探针 | 401 后 PIN 重试成功且内容一致；错误指纹在 HTTP 元数据前被拒绝 | `/private/tmp/fkfinder-localsend-pin-audit-dq2ub2p7/final.log` |
| 官方 LocalSend 1.16.1 | 同一台 Mac 上独立客户端双向传输通过；显式发送互通测试 1/1 | 使用独立 portable 客户端副本与隔离下载目录，未改动安装版配置 |
| arm64 最终构建 | 自包含构建 0 错误、36 条警告 | `/private/tmp/fk-localsend-manual-arm64-build.log` |
| x64 最终构建 | 自包含构建 0 错误、32 条警告 | `/private/tmp/fk-localsend-manual-x64-build.log` |
| 设计器修复构建 | Debug arm64 与 Release arm64 均 0 错误、36 条警告 | `/private/tmp/fk-localsend-designer-debug-arm64-build.log`、`/private/tmp/fk-localsend-designer-release-arm64-build.log` |
| 旧版 info 修复构建 | Debug arm64 0 错误、36 条警告；Release arm64 0 错误、18 条警告 | `/private/tmp/fk-localsend-hotspot-audit/debug-build.log`、`/private/tmp/fk-localsend-legacy-info-release-build.log` |
| 指定网段构建 | Debug arm64 与 Release arm64 均 0 错误、37 条警告 | `/private/tmp/fk-localsend-cidr-debug-build.log`、`/private/tmp/fk-localsend-cidr-release-build.log` |
| 自动搜索 Release arm64 构建 | 0 错误、37 条警告 | `/private/tmp/fk-localsend-auto-release-build.log` |
| 自动搜索 Debug arm64 与 Release x64 构建 | Debug arm64 0 错误、37 条警告；Release x64 自包含 0 错误、33 条警告 | `/private/tmp/fk-localsend-auto-debug-build.log`、`/private/tmp/fk-localsend-auto-x64-build.log` |
| 原 Wi-Fi 指定网段真实探针 | 隔离 profile 只配置 `172.20.235.0/24` 后调用 `DiscoverAsync`，约 7105 ms 找到手机 `172.20.235.80:53317`，指纹与此前手动连接所见一致；没有预先手动加入设备或发送文件 | `/private/tmp/fk-localsend-subnet-audit/real-subnet.log` |
| 指定网段原生设置 | 新隔离 Debug 实例中，输入 `172.20.235.83/24` 后回车规范为 `172.20.235.0/24`；`/16` 被拒绝，恢复有效值后显示监听 53317。切换设置页后值保留，只读查询 `app_settings` 确认持久化。浅色、深色均检查了字段与控件完整显示 | `/private/tmp/fkfinder-test.RgsuZn`，`/private/tmp/fk-localsend-subnet-audit/native-app.log`；CUA 原生界面检查 |

指定网段最终主审：扫描复用既有设备缓存和定向 HTTPS 注册，删除了重复结果集合；未新增接收端、通用扫描框架或数据库表。发现响应限制为 64 KiB，扫描有并发、单地址与总时限，菜单关闭取消沿用既有链路。原生检查发现无效 CIDR 的错误提示附带参数名，已去掉该参数名并完成最终 Debug/Release 构建；该轮原生实例早于这一提示文字修正，其余扫描实现一致。该轮仅隔离 profile 配置了网段，未修改正式用户设置；后续自动搜索验证使用新的空配置实例。

服务测试覆盖目录/多文件/零字节/同名保留、非法路径与令牌、长度与哈希不符、忙碌与拒绝、部分接受、超过 30 MiB 文件、并行上传、取消保留完成文件、断流恢复、符号链接和 FIFO 跳过，以及监听端口与公告端口一致。

所有真实应用验证及使用默认服务路径的测试通过 `Tools/Testing/run-isolated.sh` 启动。构建使用 `-p:SkipMacOSReleaseDMG=true`。测试采用生成的 xUnit 可执行文件及 `-class` / `-method` 选择器，检查实际执行数量。

菜单定向测试使用 `FileListViewModelCreateTests.LocalSendMenuAppearsOnlyForEnabledLocalEntries`、`ClosingMenuCancelsDiscoveryAndDiscardsLateDeviceResults`、`SelectingDiscoveredDeviceSendsTheSelectedFile` 和 `ManualAddressDialogKeepsActionsVisibleWithLongStatus`。服务测试类为 `MacExplorer.Tests.LocalSendServiceTests`。菜单测试使用 headless 真实控件和服务替身，不连接局域网发送文件；官方互通测试必须显式执行并由接收方确认。

主审再次核对了官方互通文件：官方客户端发送的源文件 `/private/tmp/fkfinder-test.YAdQT1/Interop.txt` 与接收文件 `/private/tmp/fkfinder-test.zVX8NC/Downloads/Interop.txt` 的 SHA-256 均为 `15ffd86989008813978ee391834a3d471069a4646b34b8b2e603da53df080214`。反向发送落入官方客户端的 `/private/tmp/fkfinder-localsend-interop-lxmfnqz1/Downloads/MacExplorer-to-LocalSend.txt`，39 字节，SHA-256 为 `67ba0fd16f315e838e153459af4d79fbf665d234bd00b1bd03f482ae30a0f30d`。

两个构建的 `runtimeconfig.json` 均包含 .NET 与 ASP.NET Core 10.0.5；启动器分别为 arm64 和 x86_64，官方图标、许可和来源说明均位于应用包 Resources/LocalSend。最终源码和构建中已移除临时菜单诊断。最终 `git diff --check` 通过。

此前较大批次发现的失败不能视为全套回归通过；本记录只列出明确复跑通过的批次。构建仍有包/API/测试分析器警告，不声明零警告。

## 包体影响

当前 Release `.app` 内的 87 个 `Microsoft.AspNetCore.*.dll` 文件长度合计：arm64 为 24,026,824 字节（22.91 MiB），x64 为 21,577,488 字节（20.58 MiB）。这是可直接归属 ASP.NET Core 的未压缩程序集体积，不含其他共享依赖的差额，也不是 DMG 压缩后净增量。初始工作区未保留可直接比较的旧发布包。

## 原有工作区保护

开发前记录了 Git 状态、已有补丁和 15 个文件的 SHA-256。复查时 14 个文件完全一致；共享 `MacExplorer.csproj` 仅叠加 LocalSend 框架引用和资源复制，保留既有 LibRaw 配置。RAW 缩略图、原有测试、第三方库和发布工作流改动未被覆盖。

## 待完成或需外部环境的验收

热点验证后切回原 Wi-Fi，用户确认手机掩码为 `255.255.255.0`，因此手机位于 `172.20.235.0/24`，与 Mac Wi-Fi 的 `172.20.227.0/24` 不同。新隔离实例仍正常监听 53317，旧发现入口返回 200；Mac 从 Wi-Fi 去手机也需经过网关 `172.20.227.1`。链路本地组播与官方本机 `/24` 后备扫描的范围均不能覆盖这种跨子网场景；未确认无线控制器的具体 VLAN 或过滤配置。用户选择在程序内配置指定网段后，新增按需 CIDR 扫描，并在隔离 profile 的原 Wi-Fi 探针中发现手机；正式用户配置尚未自动写入。本轮未执行全网扫描、修改路由或网络权限。

自动发现旧网络现场复查：手机为 `172.20.235.80`，本机 `en0=172.20.227.63/24`、`en4=172.20.202.24/24`，到手机的单播路由经 `en4` 网关 `172.20.202.1`；手机 `/info` 可达。20 秒有界 UDP 探针在两张本机物理网卡上重播 Mac Explorer 公告，监听收到这些重播包和本机官方客户端公告，没有收到手机公告，记录在 `/private/tmp/fk-localsend-discovery-live/probe.log`。重播包不能证明 Mac Explorer 进程自身已获组播发送权限；该监听与官方客户端启动时间重叠，也可能影响其 UDP 绑定，因此不将官方当次 UI 结果当作独立对照。随后探针退出、官方客户端独立重启并独占 UDP/TCP 53317 后，主审在其发送页连续两次看到设备列表为空；该对照证实官方在当时也未找到手机，未证明具体哪台网络设备阻断了包。LocalSend 默认组 `224.0.0.167` 属于 IANA 的链路本地控制段；[IANA 地址登记](https://www.iana.org/assignments/multicast-addresses)指出该段不应跨路由转发。官方 1.16.1 的 HTTP 后备扫描只扫本机接口所在 `/24`，另可探测已收藏的具体地址；本机两个 `/24` 都不包含手机地址。此旧网络现场没有观察到可确定的发现代码错误。应用包已有 `NSLocalNetworkUsageDescription`；[Apple 本地网络隐私说明](https://developer.apple.com/documentation/technotes/tn3179-understanding-local-network-privacy)指出 macOS 需要本地网络授权、但不要求 iOS 的组播 entitlement；此进程的实际授权状态尚未核对。此轮未进行全网段扫描或文件传输。

手机热点的新现场与上述旧网络不同：手机 `10.78.244.84`、本机 `en0=10.78.244.209/24`。官方客户端能发现 Mac，但 Mac Explorer 的旧运行实例未被手机自动发现；旧实例 `/v1/info` 返回 404，而 `/v2/info` 返回 200。新隔离 Debug 应用启动后，从热点地址请求 `/v1/info?fingerprint=手机指纹` 返回 200 和 `version=2.2`，`/v2/info` 同为 200；前后证据分别在 `/private/tmp/fk-localsend-hotspot-audit/before-info.json`、`after-info.json`。用户在手机发送页刷新后明确确认“现在能看到了”，因此热点下手机发现修复版 Mac Explorer 已通过真实设备复验；这不代表已验证跨设备文件传输或纯 UDP 组播发现。主审通过 Rider 停止旧调试实例后，以 `Tools/Testing/run-isolated.sh` 启动新 Debug 应用，测试目录为 `/private/tmp/fkfinder-test.2V0aji`。

- 官方客户端取消待确认请求已通过原生复验：确认窗自动关闭、任务显示取消、下载目录未写入文件。设置页浅色/深色已由主审独立检查，默认目录正确、文字和控件完整；新包设置页的实际监听端口和开关/页签行为亦已复验。
- 原生右键诊断记录了按下、释放和 `menu.Open`，菜单保持 `IsOpen=True`，未出现立即关闭事件。但当前 CUA 截图/无障碍树未能可靠呈现独立 ContextMenu 窗口，新增 IP 对话框入口、三级菜单的真实视觉、键盘逐级展开和点击设备发送仍未完成原生验收；日志和 headless 布局测试不能替代这些项目。窄窗口、长设备名称的完整视觉矩阵也未完成。
- 跨物理设备手动 IP 定向注册和空配置跨子网自动扫描已验证；本轮真实文件传输和 Intel 实机运行尚未验证；同机官方客户端互通与自包含构建不能替代这些项目。
- 真实磁盘写满、物理断网/网络切换、进程强制退出及完整长文件名矩阵尚未全部验证。自动测试覆盖对应的部分错误处理路径，不等同完整场景验收。

## 后台发现开发补充（2026-09-29）

本轮依照 `docs/LOCALSEND_BACKGROUND_DISCOVERY_PLAN.md`，在现有服务内加入一个按网络代次管理的后台发现任务。启动监听后即异步公告、复查同进程已成功注册的设备及当前网络匹配的历史设备，再按批渐进扫描。菜单只读取共享在线结果并订阅增量；关闭菜单不取消后台任务。候选和在线缓存各限 128，陌生 TCP 由单一协调任务限为 16 并发和每秒最多 64 次，所有注册/设备信息 HTTP 共用 4 并发。陌生扫描按至多 64 个地址一批推进，连续约 20 秒后休息至少 5 秒，整轮完成后冷却 5 分钟。传输期间暂停陌生扫描，已知设备仍按 60 秒注册和失败退避复查。UDP 公告只加入有界候选，不再为每包创建任务。关闭与网络重建会取消并等待 UDP、协调任务和外部手动连接；旧代次结果在同一锁下检查后才写缓存或历史。HTTP 明文设备保留非持久的注册兼容性，只有 HTTPS 证书指纹核对通过后才写历史。

本轮相对于开发前基线只修改 `LocalSendService.cs`、`LocalSendService.Discovery.cs`、`LocalSendService.Scan.cs`、`LocalSendServiceTests.cs` 和本实施记录，并新增 `LocalSendService.Background.cs`；RAW/LibRaw、发布脚本及其他既有工作文件 SHA-256 未变化。服务代码净增 520 行，定向测试净增 252 行；未提交、推送或发布。

| 验证 | 结果与证据 |
| --- | --- |
| 最终 Debug arm64 构建 | `dotnet build MacExplorer.csproj -p:SkipMacOSReleaseDMG=true -v:q`：0 错误、18 条现有警告；`/private/tmp/fk-localsend-background-final-debug-build.log`。`git diff --check` 通过。 |
| LocalSend 服务测试 | 31 项发现，30 项运行通过，1 项显式官方互通测试未运行；`/private/tmp/fk-localsend-background-final-service-tests.log`。覆盖不打开菜单自动注册、异常响应和 UDP 突发、候选容量、HTTP 明文注册、换 IP 旧成功/失败迟到、菜单跨网络代次、探测限速/并发、停用后无新探测、多轮网络重建和重复 Dispose。测试使用显式回环地址或本地端点，测试初始化将扫描接口限制为空。 |
| 菜单定向回归 | 5/5 通过；`/private/tmp/fk-localsend-background-final-menu-tests.log`。此前一次 headless `Window.Close` 抛出 Avalonia 集合异常，最终组合复跑及主审独立复跑均未复现；未单独定位该 Avalonia 关闭异常的根因，不把它记为已修复。 |
| 实际探测预算 | 96 个显式回环候选的测试前 1 秒发起 63 次 TCP 探测、总计 96 次，TCP 峰值 16、HTTP 峰值 1；代码上限为 16/4。`/private/tmp/fk-localsend-background-probe-stats-test.log`。 |
| 未展开菜单的隔离应用 | 新 Debug `.app` 经 `run-isolated.sh` 启动，PID 10113、profile `/private/tmp/fkfinder-test.HdKyHx`，启动时无附加 CIDR 或历史设备。约 18:22:18（北京时间），隔离数据库写入已验证的另一 Mac Explorer 实例 `10.37.129.2:53317`；本机实际监听 57529（默认 53317 被既有实例占用）。这证明该现场无需菜单触发即可后台核验与注册另一实例；它不是手机 `172.20.235.80` 的验证。`/private/tmp/fk-localsend-background-native.log` 及该 profile 的 `index.db`。未发送文件。 |
| 原生进程资源采样 | 功能关闭 profile `/private/tmp/fkfinder-test.zYNbpX`、PID 11155，与空配置扫描 profile `/private/tmp/fkfinder-test.HdKyHx`、PID 10113，各连续采样 60 次、约 61 秒。关闭时 CPU 中位数 0%、线程中位数 18、FD 396、socket 0；扫描时 CPU 中位数 4.85%、线程中位数 30、FD 中位数 558、socket 2–18。原始数据：`/private/tmp/fk-localsend-background-off-resources.csv`、`/private/tmp/fk-localsend-background-scan-resources.csv`。采样包早于最后的网络事件竞态修复；两个进程启动和 GC 阶段不同，RSS 波动大，不能由此计算 LocalSend 净内存或断言无泄漏。 |

锁屏后再启动的已知设备 profile `/private/tmp/fkfinder-test.SH3vH6` 停在应用既有的 macOS 显示初始化等待，未进入 LocalSend 服务，不能作为已知设备稳定期原生采样。该隔离进程和本地 HTTPS 测试端点已清理；用户解锁后应由主会话重新验证。手机 UI 是否自动看到电脑、`172.20.235.80` 是否由本轮后台扫描找到、真实手机文件传输、物理网络切换及原生菜单完整交互，本轮均未确认。没有向手机发送文件，也没有修改正式用户配置。

主会话完成最终差异复查：本轮继续使用应用级单例，没有新增公共接口、数据库表或通用调度框架。候选记录负责重试与历史优先级，在线快照负责已核验且未过期的展示，职责有区别；多余的 ConcurrentDictionary 已精简为受同一锁保护的 Dictionary。审查中发现的候选满额、异常响应终止循环、手动连接未等待收尾、旧地址迟到成功/失败覆盖新地址、已释放取消源访问及网络事件竞态均已交回开发会话修正并复核。主审独立菜单复验日志为 `/private/tmp/fk-localsend-background-review-menu-recheck.log`（5/5）；早先失败日志 `/private/tmp/fk-localsend-background-review-menu.log` 仍保留。未发现需要继续扩展架构的理由；原生验收、菜单关闭偶发现象与长期资源稳定性仍按上述边界保留，不能承诺绝无泄漏或已完成手机互通。

## 已知设备快速复查补充（2026-09-29）

用户确认上一版在不展开菜单时，手机约 60–90 秒后能看到电脑；这是用户的实机观察，不是本轮自动测试测得的耗时。本轮将同进程已成功注册或当前网络匹配的历史设备中最近的最多 8 台，改为约 5 秒的定向注册调度目标，失败或暂时离线也继续此间隔。其余候选保留原有成功 60 秒、失败 15/30/60/120/300 秒退避。快速集合随最新成功时间变化时，刚进入集合的候选也安排在约 5 秒内到期；退出集合的候选恢复普通到期时间。单个请求仍有 4 秒超时，共享 HTTP 并发上限 4；慢 HTTP 批次和并发竞争可能使实际请求多等几秒。没有增加逐设备定时器或持久化字段。

协调任务按所有候选最近到期时间唤醒，因此无可扫描网卡时的 30 秒等待、扫描休息和普通退避不会延误已到期的已知设备。陌生地址扫描每批由 64 缩为 4，批次间及时处理已知设备；陌生 TCP 仍受 16 并发和每秒最多 64 次约束。较小批次和已知设备优先级可能延长陌生网段的首轮完整扫描时间，因此不能据此承诺未见过的跨子网手机也在 5 秒内出现。

定向回环 HTTPS 测试不调用 `DiscoverAsync`：连续注册间隔为 5.03、5.02、5.02、5.02 秒，第 3 次返回 HTTP 503 后仍按约 5 秒复查；第 5 次超过客户端 4 秒超时后，第 6 次请求相隔 9.01 秒。停用后再等 6 秒没有新请求，监听端口归零。9 个回环 HTTP 已验证端点在 10 秒观察窗中恰好 8 个进入快速复查，剩余 1 个未提前复查。测试将服务的组播接口限制为回环，避免现场设备影响结果；未连接手机，也未发送文件。日志：`/private/tmp/fk-localsend-fast-known-interval-test.log`、`/private/tmp/fk-localsend-fast-known-cap-test.log`。手机侧显示速度仍需用户用这版重新观察。

完整 `LocalSendServiceTests` 发现 33 项，32 项通过、1 项显式官方互通未运行，包含停用及网络重建收尾回归；日志 `/private/tmp/fk-localsend-fast-known-full-service-tests.log`。主会话另行串行复跑快速上限及停用/网络代次收尾两项通过，日志 `/private/tmp/fk-localsend-fast-known-root-final-review.log`。菜单定向测试 5/5 通过，日志 `/private/tmp/fk-localsend-fast-known-menu-tests.log`。Debug arm64 构建 0 错误、18 条既有警告，日志 `/private/tmp/fk-localsend-fast-known-debug-build.log`；测试项目构建 0 错误、458 条警告，日志 `/private/tmp/fk-localsend-fast-known-test-build.log`。相对本次跟进前的文件快照，只有 `LocalSendService.Background.cs`、`LocalSendService.Discovery.cs`、`LocalSendServiceTests.cs` 和本记录变化；没有提交、推送或发布。
