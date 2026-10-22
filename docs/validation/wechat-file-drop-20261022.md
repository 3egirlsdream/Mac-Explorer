# 微信文件拖入调查（2026-10-22）

## 当前验收：未预先开启微信权限，文件夹授权及重启复用通过

用户要求正式版直接接收可读取的拖放；系统拒绝读取时主动弹出授权窗口，不能要求用户预先进入系统设置开启微信访问。授权范围已从单个文件扩大为用户选择的文件夹及其子文件夹，并通过安全作用域书签保存，供后续拖入和重启后复用。历史上手动启用微信数据访问后的成功，不代表这一要求已验收。

新实现只在打开源文件明确返回 Cocoa 读取权限错误或 POSIX EPERM/EACCES 时显示系统 `NSOpenPanel` 文件夹选择面板。目标目录写入失败不触发源目录授权。微信附件默认定位到当前账号的附件缓存 `temp` 目录，覆盖其拖放、图片缓存子目录；用户也可选择包含源文件的其他上级目录。面板说明授权包括子目录，确认后自动重试原保存；取消不保存、不记录授权。书签仅写入应用数据目录，权限为 0600。

新的独立签名测试标识为 `com.macexplorer.drop-fresh-folder-20261022`，没有预先启用微信数据访问，未替换正式版。首次真实测试窗口为 `fkfinder-test.sBmoZR`，进程 85882。用户解锁电脑后完成真实微信拖放及系统文件夹确认：

- 16:15:28，JSON 附件打开源流返回 EPERM。相同时刻 TCC 记录 `SystemPolicyAppDataDetailed` 不允许自动提示，结果 `authValue=0, authReason=2`。这验证初始身份没有微信应用数据访问许可。
- 应用自动打开文件夹选择面板；用户选择微信当前账号的附件缓存 `temp` 文件夹。16:15:31，原附件自动重试，源流 `status=2, error=null`，保存 14,087 字节。
- 16:15:42–16:15:53，再拖入三个 PNG、一个 HTML 和一个 PDF，全部源流打开成功，无新的复制失败；三个 PNG 均由系统图片工具解码成功。用户确认授权后文件和图片均保存成功。
- 应用仅保存一份文件夹书签（1,408 字符），记录文件权限 0600；选择范围确为 `temp`，覆盖不同图片及文件缓存子目录。

16:16 重启同一签名、同一路径的可执行文件，旧进程退出，新进程为 2080。仍通过隔离测试启动脚本运行，生成全新目录 `fkfinder-test.X4WuRt`，只把上一进程保存的书签记录复制到新隔离配置；不复制旧窗口、数据库或文件结果。该方式验证跨进程恢复持久授权，而非沿用旧进程的临时作用域。

16:16:36–16:16:47，重启后再次真实拖入六次，全部 `status=2, error=null`，0 次复制失败；实际保存两个 JPEG、一个 PNG（分别保存到根目录和隔离的 Pictures 目录）、HTML 与 PDF。所有图片解码成功；HTML 与 PDF 的 SHA-256 与重启前完全一致，书签记录也保持一致。用户确认直接保存，未重复弹出授权。日志和落盘样本中没有 ZIP，因此本轮重启验收只报告实际覆盖的上述类型；历史 ZIP 成功另见后文。

完成后只读检查系统设置中独立的 `Mac Explorer Drop Folder Test` 条目，微信访问开关仍为 **off**。本轮没有开启该开关、完全磁盘访问或修改 TCC 数据库；保存依赖用户选择文件夹及书签恢复的访问能力。

已确认的前置证据：另一个未授微信数据访问的独立原生探针，选择单个 ZIP 后由 NSCocoaErrorDomain 257／底层 EPERM 转为读取成功，保存 21,592,150 字节。用户确认已选择并授权保存。该结果验证系统文件选择授权可用，但不代替文件夹授权与完整应用流程验收。

本轮回归：fast 897 通过、2 跳过、0 失败；相关 UI 67/67；原生拖放集成 1/1。新增测试覆盖文件夹后代范围、相似前缀隔离、错误目录拒绝、书签存储与重载、失效及损坏记录，以及源权限错误和目标写入错误的区分。真实跨进程恢复已按上述步骤通过；真实用户取消面板、授权文件夹移动或系统撤销授权后的恢复仍未人工验收，当前自动化只覆盖无有效所属窗口时的取消回调及失效书签。

Apple 的 [macOS 27 发布说明](https://developer.apple.com/documentation/macos-release-notes/macos-27-release-notes)明确指出，跨开发团队访问应用数据容器不再自动弹出许可提示，默认拒绝。这是应用主动提供系统选择面板的依据，不是要求用户手动修改系统设置的理由。文件夹范围与书签机制参考 [Apple 文件访问文档](https://developer.apple.com/documentation/security/accessing-files-from-the-macos-app-sandbox)。

## 定位原因后的代码精简

按用户要求移除未经证实能解决权限问题的主线程延后打开源流、跨线程传递已打开流及额外 `NSFileCoordinator` 层；现在在后台复制队列内直接打开源文件并复制内容。保留原始拖放 URL 的访问租约、真实读取权限错误识别、文件夹授权与持久书签，以及不覆盖提交和失败清理。

同时合并临时目录创建及内容复制入口，删除原生 URL 转路径后再逐项匹配回 URL 的重复处理，将拖放诊断缩为一条包含源、结果和 NSError 的记录。删除只服务于旧“预先打开流后移除路径”方案的测试。原生实现由 423 行减为 392 行，相关测试减少 10 行；本轮清理未修改其他进行中的功能。

上述真实文件夹授权和重启验证发生在精简前。精简后再次执行 fast：897 通过、2 跳过、0 失败；原生集成仍覆盖内容校验、源读取拒绝、授权后重试入口、目标写入拒绝与重名保护。精简后未重复进行人工微信拖放。

## 历史诊断：权限重建后真实拖放通过，用户已确认

原生格式识别和仅复制操作已补齐。早期重复测试仍有图片、ZIP、RAR 失败，现已从同一事件的 TCC 日志定位到失效的旧签名权限记录；定向重置并以稳定签名重新授权后，原应用已连续保存 10 个拖放结果。单次成功和自动测试通过不能证明所有来源场景，以下保留失败、修复及复测的证据边界。

微信拖拽提供 `public.file-url`、`NSFilenamesPboardType`、Qt URI-list，实测部分事件中 Avalonia `TryGetFiles()` 返回空；来源操作掩码为 `1`（仅复制）。桥接保留原生 URL 和访问租约，接收文件承诺及图片数据，使用临时目录和不覆盖提交。普通文件按内容流复制，不继承源应用的受保护元数据。上述处理解决格式和复制语义，不能覆盖系统强制访问控制。

## 已确认的失败证据

环境：macOS 27.0.1，build 26A434；独立测试标识 `com.macexplorer.drop-test`。

15:09:59 的同一 RAR 事件中，应用日志和系统日志分别记录：

```text
NSPOSIXErrorDomain Code=1 "Operation not permitted"
System Policy: MacExplorer deny(1) file-read-data <WeChat container source>
service=kTCCServiceSystemPolicyAppDataDetailed
Failed to match existing code requirement for subject com.macexplorer.drop-test
Service kTCCServiceSystemPolicyAppDataDetailed does not allow prompting; recording denied.
authValue=0, authReason=5
```

后续 ZIP 和多个 PNG/JPEG 同样在打开源流时返回 EPERM；其中原生安全作用域启动计数为 1。因此“已取得作用域”不等于“系统允许读取”。错误发生在源文件读取阶段，不能归因于目标目录写入或图片解码。

测试包此前每次重建均临时签名，designated requirement 绑定变化的 cdhash；系统明确报告已有权限记录与当前签名不匹配。测试包现已使用现有 Developer ID 证书签名，requirement 绑定 bundle ID、证书及团队，不再绑定单次构建哈希。签名稳定化尚不能证明权限已恢复，仍需针对微信访问授权验证。

目前没有证据证明“延后到下一次主线程循环打开源文件”或“提前读取原生 URL”能够消除 TCC 拒绝。运行中的签名验证包保留上一版复制逻辑，只调整测试签名，避免混入新的复制变量。

## 公开资料与验证方向

- [ChromeDevTools 已确认 issue #3089](https://github.com/ChromeDevTools/chrome-devtools-mcp/issues/3089)：相同 OS 版本及 build、相同 `SystemPolicyAppDataDetailed` 无提示拒绝。报告通过“文件与文件夹”中针对被访问 App 的单独授权恢复访问。这是相关证据，尚非本项目授权后实测结论。
- [Apple DTS：访问其他 App 容器](https://developer.apple.com/forums/thread/768040?answerId=813217022)：MAC 限制与沙箱作用域不同；用户通过系统文件选择面板选择具体文件时可以取得对应文件访问能力。Finder 具有不同的系统权限，不能据其成功推断第三方应用也有相同访问能力。

用户已允许仅为该测试包启用微信数据访问。系统最初无法解析 `/private/tmp` 内测试包的应用 URL，界面只有其他同名条目。一次误匹配曾开启正式版的微信权限，核对授权日志后已立即恢复关闭。随后将同一已签名测试包放入用户 Applications 目录并注册，系统出现独立的 `Mac Explorer Drop Test` 条目。

15:23:13 虽然权限开关写入 `Allowed (System Set)`，15:27:35 的真实拖放仍失败。详细 TCC 日志证实，此次仍在对比旧 `cdhash 684058…` 与当前 Developer ID requirement，校验失败后返回 `Denied (Service Policy)`。因此单纯切换开关不足以修复旧签名记录。

执行系统工具 `tccutil reset SystemPolicyAppDataDetailed com.macexplorer.drop-test` 成功；只重置测试包的这一服务，未使用 `reset All`、直接修改数据库或开启完全磁盘访问。随后以相同稳定签名身份运行临时 AppKit 读取探针，针对本次失败的两个 ZIP 与一个 PNG 建立新记录，再按用户已有授权开启测试包的微信访问。

15:31:30 同一探针由 EPERM 转为 3/3 成功：

| 文件类型 | 字节数 | 内容校验 |
| --- | ---: | --- |
| ZIP 1 | 21,592,150 | 2 个条目，CRC 全部通过 |
| ZIP 2 | 7,475,233 | 20 个条目，CRC 全部通过 |
| PNG | 7,768 | 有效 PNG，220×125 |

探针源码和源文件路径清单仅在本地临时目录，不加入项目；保存结果位于隔离测试目录。探针已停止，原应用可执行文件已恢复，重新签名后的 designated requirement 与探针相同。原应用现通过隔离脚本运行于 `fkfinder-test.6HxRcz`。15:32:46 起的真实微信拖放已保存 10 个结果，涵盖两个不同 ZIP（各含重复副本）、RAR、两个 PNG 和三个 JPEG；应用日志为 10 次 `open status=2 error=(null)`、0 次复制失败。ZIP 校验通过；PNG/JPEG 均能由系统图片工具解码。重复 ZIP 的 SHA-256 相同，与原生探针副本也相同。

真实拖放的同一时刻 TCC 日志明确记录当前 Developer ID requirement 校验 `status: 0`，`SystemPolicyAppDataDetailed` 返回 `Allowed (User Consent)`；这与此前旧 cdhash 校验失败形成直接对照。用户随后明确确认 ZIP 和 PNG 两类均保存成功。临时原生读取探针与验证启动项已停止，隔离测试结果及日志保留；测试包仍为原应用，无探针入口。

源码移除未获实测支持的“转发 Avalonia Drop 前预读 URL”实验缓存，使交付逻辑与正在验证的应用一致；保留原生 URL 生命周期、后台内容复制及格式支持。

## 自动回归与边界

- `bash Tools/Testing/run-tests.sh fast`：890 通过、2 跳过、0 失败。
- `bash Tools/Testing/run-tests.sh ui --area FileDrop --area FileDragStart --area Issue11Interaction --area Sidebar --area CrossTabMoveRefresh --no-build`：67 通过。
- `bash Tools/Testing/run-tests.sh integration --area FileDrop --no-build`：1 通过，覆盖原生格式、Qt URI-list、旧文件名列表、文件承诺、内容复制及重名保护。模拟拖放不复现真实 TCC 决策。
- Debug 构建成功；重跑 fast 为 890 通过、2 跳过，原生集成为 1/1 通过，`git diff --check` 通过。测试包仅位于用户 Applications 目录，未替换 `/Applications` 中正式版，未提交或推送。
- 临时诊断由 `MACEXPLORER_DROP_DIAGNOSTICS` 显式开启，会包含本地路径；默认关闭。原始私人路径和文件内容不写入本报告。
