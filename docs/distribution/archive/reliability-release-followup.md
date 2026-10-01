# 可靠性与发布保障后续交付

> 历史执行与验收记录；当前构建和发布说明见 [双渠道说明](../mac-app-store.md)。

独立复核及补修结果见 [独立验收记录](reliability-release-review.md)；包含断开后清除一次使用口令、运行时许可归属补齐及最终两渠道各 42 项定向复核。下面的 105 项记录为这些补修前的执行证据。

日期：2026-10-01。工作目录：`/Users/jiangxinji/Documents/FKFinder`。版本保持 1.0.50。本次只发布用户明确授权的官网隐私页面；应用代码、CI、分发工具尚未提交或推送，没有上传 Apple、发布软件或生成正式商店安装包。

## 基线与增量归属

开始时 HEAD 为 `6baa8b1ce775fa9616a2a9938825abaa8541ec48`，工作区已有大量未提交渠道、隐私、Copilot、ZIP 与其他改动。逐文件原始快照：`/private/tmp/fkfinder-recovery-policy-baseline.f7dow4dw/manifest.json`、`files/`、`tracked.patch`。不将全部当前脏文件算作本次成果。

本次修改的既有路径为：

- `Services/Impl/SettingsService.cs`、`Services/Impl/LocalSendService.cs`、`Views/Dialogs/SettingsDialog.axaml`、`Views/Dialogs/SettingsDialog.axaml.cs`。
- `Services/DirectoryAccess.cs`、`Services/Search/SearchIndexer.cs`。
- `Models/RemoteServerInfo.cs`、`Services/IRemoteConnectionService.cs`、`Services/Impl/RemoteConnectionService.cs`、`Views/Dialogs/RemoteConnectionDialog.axaml`、`Views/Dialogs/RemoteConnectionDialog.axaml.cs`、`App.axaml.cs`。
- `Tests/MacExplorer.Tests/DistributionCommonTests.cs`、`SecurityDialogTests.cs`、`SftpLoopbackTests.cs`、`FileListLoadingPipelineTests.cs`、`LocalSendServiceTests.cs`、`LocalSendReviewTests.cs`、`TestData/sftp-loopback.py`。
- `Directory.Build.props`、`MacExplorer.csproj`、`Views/Dialogs/PrivacyPolicy.cs`、`README.md`、`docs/index.html`、`Tools/Distribution/verify-bundle.py`、`prepare-store-pkg.sh`、`ThirdParty/Notices/README.md`。
- `docs/distribution/privacy-data-flow.md`、`privacy-security-followup.md`、`privacy-security-review.md`、`mac-app-store-review.md` 仅添加当前交付入口，保留历史审计日期和结果。

新增路径：`Views/Dialogs/SftpPrivateKeyConsent.cs`、`Tests/MacExplorer.Tests/ReliabilityRecoveryTests.cs`、`Assets/PrivacyPolicy.txt`、`docs/privacy/{index.html,policy.css,policy.js}`、`.github/workflows/distribution-qa.yml`、`Tools/Distribution/{verify-notices.py,verify-release-config.py,verify-test-summary.py,test_release_config.py,release-config.json}`、本报告，以及 `ThirdParty/Notices/dependencies.json`、`Project.LICENSE.txt`、`Dependencies/` 的完整声明文件。

精确逐文件 SHA256 清单与相对原始快照的补丁记录在 `/private/tmp/fkfinder-reliability-logs/relative-baseline.json`、`relative-baseline.patch`；这些只列上述路径，不包含无关已有脏文件。

## 可靠性修复

### LocalSend

`localsend_enabled` 作为隐私开关必须反馈持久化错误。关闭先设置进程内关闭状态，再尝试写入；无论写入成功与否，都等待监听、发现、网络变化及传输关闭。关闭失败不会重新启动网络，设置页保持关闭并显示重启可能恢复旧配置的风险，提供“重试保存关闭”。启用必须先成功保存，保存失败保持停止并报告失败。

真实临时 SQLite 触发器模拟保存失败：已启动的监听和发现关闭、重复 Start 不复活、重新读取数据库仍可看到旧值；恢复存储后重试关闭，重载保持关闭。既有 LocalSend 传输／发现／取消回归一并运行。

### DirectoryAccess

不可读记录、损坏 JSON、备份失败或恢复写入失败均进入可恢复的未授权状态。原记录不被空授权覆盖；损坏 JSON 只有备份完成且用户明确重新选择后才改写。备份失败与读取失败保留原件并拒绝后续覆盖。设置支持重新加载重试及重新选择目录。

恢复先开启临时 scopes、完成保存，再发布授权。任一步失败关闭临时和旧 scopes，清除路径别名并通知索引停止旧 watcher；重试成功后发布当前授权，重新建立 watcher，也能启动之前从未索引的新授权目录。选择失败和无路径的无效 scope 都会关闭已开的 scope。

测试使用真实临时文件权限／只读目录／临时写入冲突，原内容保留；安全作用域后端使用可计数替身验证开关平衡，索引使用真实临时目录和 watcher。此项不等于 OS 安全作用域授权验收。

### 加密 SFTP 私钥

采用 SSH.NET 的 `PrivateKeyFile(path, passphrase)`，没有自写解密。私钥口令和账户密码使用独立字段与 Keychain account；默认仅本次使用，显式勾选才保存私钥口令。私钥口令通过 `JsonIgnore` 排除，未选记住时保存的连接模型也不保留口令；切换私钥路径立即清理口令及记住选择。切换密码认证、取消记住后保存或删除连接会清理对应 Keychain 条目。

缺口令的已保存加密连接会打开紧凑的掩码输入对话框，默认聚焦取消、默认不记住。取消或关闭在建连前结束，不保存凭据。错误口令报告解密失败，可重试。认证成功后才保存提示中明确勾选的口令。保存和删除保留两类旧凭据快照，失败回滚；保留原有 SHA256 主机信任、变更拒绝、迁移错误及多连接保护。

Paramiko 5.0.0 在本次隔离目录生成 ECDSA 加密／未加密客户端私钥及 RSA 主机私钥，只监听 127.0.0.1。验证正确口令、错误口令、缺口令、取消、一次使用不持久化、记住后重载连接、切换认证删除和主机信任既有回归。未使用真实 SSH 配置、SSH agent、账户密码或用户 Keychain。测试服务拒绝旧 SHA1 的 ssh-rsa 认证，故客户端 fixture 使用 ECDSA，未放宽服务器算法。

## 正式隐私政策与官网部署

正式页面：[https://3egirlsdream.github.io/Mac-Explorer/privacy/](https://3egirlsdream.github.io/Mac-Explorer/privacy/)，首页页脚有独立入口。隐私联系人使用用户提供的 `xulezuo@hotmail.com`，没有编造主体身份。页面说明本地索引／OCR／图像分析、目录授权、Apple 地点解析、Copilot、SFTP、LocalSend、更新与插件市场、插件、网站托管、保存／删除和联系途径；区分许可撤回与已发出请求，未宣称第三方统一保留期限。插件说明明确其继承宿主权限，未承诺不存在的独立文件沙盒。

应用默认 URL、程序集政策入口、随包 `PrivacyPolicy.txt` 和 Info.plist 的 PrivacyPolicyURL 同步。历史草案保留为源代码历史，当前应用不再将其作为政策。

官网在独立 worktree `/private/tmp/fkfinder-policy-website` 创建，仅推送 4 个网站文件。官网提交：`aed0f854c6ef0e6c5f6f013ee862d49f5fb87c35`、最终 `d6586243ffe1c290032a53134f3b02d20abff530`。GitHub Pages 最终部署为 built，提交匹配最终版本。实际 GET 返回 HTTP 200，最终 URL 匹配、内容逐字匹配源码；首页返回 200，页脚链接正确。证据：`pages-deployment-final.json`、`live-policy-check.json`、`policy-online-final.html`、`home-online.html`。

Playwright 验证桌面 1280×900、手机 390×844 浅深色共 4 种布局，无水平溢出、无 JavaScript 错误；主题按钮、邮箱和首页链接均通过。证据：`site-qa.json`、`site-qa-final.log`、`policy-*.png`。报告内所有未另注明的日志均位于 `/private/tmp/fkfinder-reliability-logs/`。

## 完整依赖与资源声明

`ThirdParty/Notices/dependencies.json` 固定 81 个已解析包（包含宿主／插件不同版本、传递／原生依赖及 .NET／ASP.NET 运行时）和 9 类资源；每条声明包含版本、归属、许可证来源、artifact 清单及 SHA256。保留实际 LICENSE、NOTICE、THIRD-PARTY-NOTICES；另含 TextMate 内嵌 grammar 的 51 个 cgmanifest／57 个组件、数学字体的 GUST／SIL OFL／LPPL 等许可与实际字体归属。

两渠道构建自动运行 notice gate，审核实际托管／原生二进制及网站内置插件压缩包，未知 artifact／版本／缺声明或散列变化立即失败。包括 Fluent 图标、LibRaw 双许可与对应可分发源码、LiquidGlass、Vex、LocalSend logo、Conversion/Highlight.js 等资源。历史 DotNetZip 声明不再随包。

这是依赖归属与构建完整性检查，不替代新版本许可证人工审核或 Apple 对 LGPL 等分发条件的审核。

## arm64 CI 与正式提交门禁

新增 `.github/workflows/distribution-qa.yml`，PR／代码 push 和手动执行分别构建 Website、AppStore 的 macos-15 arm64。固定 global.json SDK、独立 artifacts 和 Paramiko fixture；所有构建带 `SkipMacOSReleaseDMG=true`，AppStore QA 只用测试 Bundle ID 和 ad-hoc 签名，不需要真实分发证书。

直接执行生成的 xUnit runner `-class`，用隔离脚本和仓库环境变量；`verify-test-summary.py` 强制 Total > 0、Errors／Failed／Skipped／Not Run 均为 0。审计包布局、渠道限制、全部 notice，运行提交配置单元测试并保存日志。既有 `release.yml` 未修改。CI 文件尚未推送，未声称远端 Actions 已通过。

正式 `StoreSubmission=true` 构建及 `prepare-store-pkg.sh` 必须配置真实 App Store Bundle ID、匹配 HTTPS 正式政策并实际返回可验证的 200 页面。QA／test 身份、身份不匹配、未配置真实身份、不可访问／跳转／空政策被拒绝；打包还要求既有分发签名、有效匹配 profile、团队及签名证书、Mac Installer Distribution 身份。`release-config.json` 的实际商店身份保留 null，测试身份不能混作正式身份。

## 最终验证结果

| 最终验证 | 结果 | 证据 |
| --- | --- | --- |
| Release osx-arm64 Website 应用及测试构建 | 468 warnings、0 errors，退出 0 | `build-website-final.log` |
| Release osx-arm64 AppStore QA 应用及测试构建 | 468 warnings、0 errors，退出 0 | `build-store-final.log` |
| 最终测试替身适配后单独构建 runner | 两渠道退出 0；未改应用二进制 | `build-tests-website-final.log`、`build-tests-store-final.log` |
| Website 完整选定回归 | Total 105；Errors / Failed / Skipped / Not Run = 0 / 0 / 0 / 0 | `tests-website-final.log` |
| AppStore 完整同一集合 | Total 105；Errors / Failed / Skipped / Not Run = 0 / 0 / 0 / 0 | `tests-store-final.log` |
| 两渠道 bundle 审计及 notice gate | errors = []；81 packages / 412 code artifacts / 9 resources | `bundle-*-final.json`、`notices-*-final.json` |
| 发布配置单元验证 | 3 tests，OK | `release-preflight-tests.log` |
| 正式提交流程拒绝 QA / 缺真实身份 | 命令非零退出，未产生正式包 | `submission-qa-rejection.log`、`formal-build-rejection.log` |
| CI YAML 结构解析 | Website / AppStore matrix 正确 | `workflow-yaml.log` |

可重现构建命令：

```sh
dotnet build Tests/MacExplorer.Tests/MacExplorer.Tests.csproj -c Release -r osx-arm64 --artifacts-path /private/tmp/fkfinder-reliability-website -p:SkipMacOSReleaseDMG=true
dotnet build Tests/MacExplorer.Tests/MacExplorer.Tests.csproj -c Release -r osx-arm64 --artifacts-path /private/tmp/fkfinder-reliability-store -p:DistributionChannel=AppStore -p:AppStoreBundleId=com.macexplorer.app.store.qa -p:SkipMacOSReleaseDMG=true
```

两个渠道分别以自身 runner 执行下列命令（`<channel>` 为 website 或 store）：

```sh
bash Tools/Testing/run-isolated.sh env MACEXPLORER_TEST_REPOSITORY=/Users/jiangxinji/Documents/FKFinder MACEXPLORER_SFTP_TEST_PYTHON=/private/tmp/fkfinder-sftp-qa-venv/bin/python /private/tmp/fkfinder-reliability-<channel>/bin/MacExplorer.Tests/release_osx-arm64/MacExplorer.Tests -class MacExplorer.Tests.ReliabilityRecoveryTests -class MacExplorer.Tests.SftpLoopbackTests -class MacExplorer.Tests.DistributionCommonTests -class MacExplorer.Tests.PrivacySecurityFollowupTests -class MacExplorer.Tests.SecurityDialogTests -class MacExplorer.Tests.LocalSendServiceTests -class MacExplorer.Tests.LocalSendReviewTests -class MacExplorer.Tests.FileListLoadingPipelineTests -method- MacExplorer.Tests.LocalSendServiceTests.SendsToOfficialLocalSendClient
```

最后一个排除选择器仅排除本来标为 Explicit、需要人工启动外部官方客户端的互通测试，未将其计作通过。完整自动集合没有跳过或未运行项；真实设备互通仍是外部验收项。旧 LocalSend 回归原先自行切换进程临时根，Store 授权单例与 HTTP execution context 会失配；测试现在显式注入仅覆盖 fixture 根的 FileAccessOverride。纯文件列表 pipeline 使用服务与路径替身，独立替换其 DirectoryAccess，不放宽生产授权。

第一次扩大回归出现 3 个失败：独立 artifacts 输出未传仓库环境变量 1 项，异步 TextChanged 清理导致路径变化时口令未立即清空 2 项。已添加明确环境变量，并改用同步 TextProperty 通知；再运行最终同一完整集合。中途 fixture 的 RSA/SHA1 不兼容问题已通过 ECDSA fixture 解决。没有以跳过回环测试或删除失败断言充当通过。

原生验证经 `run-isolated.sh` 直接启动新 arm64 bundle 的 `Contents/MacOS/MacExplorer`。Website 额外复制相同最终构建到 `/private/tmp/FKFinderReliabilityWebsiteQA.app`，仅改为唯一测试 Bundle ID `com.macexplorer.reliability.website.qa` 并 ad-hoc 重签，避免 LaunchServices 在多份同名 bundle 中选错实例；包审计仍针对原始正式 Website 输出。

- Website 最终私钥表单已检查浅色及深色、口令行顺序、默认未勾选记住、滚动后全部字段与底部按钮、取消返回。深色真实保存 `.tmp` 路径冲突显示长错误，原表单及主机保留，底部按钮可见。400×450 窄尺寸浅深色布局由 headless UI 测试验证；原生拖拽缩放接口返回 AXError.notImplemented，不能把该窄尺寸算作原生已验收。
- AppStore QA 的系统选择器明确展示 `/private/tmp/fkfinder-test.3JSqKs`，选择“打开”后主窗口进入本次临时根。设置可看到该真实 bookmark 授权。
- 只在此测试根制造 `directory-bookmarks.json.tmp` 冲突，原生“重试授权恢复”显示当前未授权及原件保留，授权行标记离线或失效。原记录逐字节未变；移除临时冲突后再次重试恢复成功，失效标签及错误消失。证据：`native-grants-before.json`、`native-observations.json`、本会话原生截图与 AX 状态。
- 在此测试根的实际隔离数据库 `.macexplorer/index.db` 添加临时 SQLite 触发器；原生关闭 LocalSend 后开关保持 off，显示重启风险和“重试保存关闭”。同一进程关闭前有 TCP *:50144 / UDP *:53317，关闭后 lsof 无 TCP/UDP socket。移除触发器并点击重试后状态为“已停止发现和接收”，实际数据库保存 False。证据：`native-network-before.log`、`native-network-after.log`、`native-observations.json`。
- 中途 QA 预置数据库路径曾误写为 DataDirectory 的 index.db（测试真实数据库路径优先为 `.macexplorer/index.db`），导致默认 LocalSend 启动；已纠正辅助脚本，并利用该实例验证完整关闭行为。测试数据、数据库和授权均在临时根。
- 一次通过显示名绑定应用时 LaunchServices 意外启动了已有旧 capture bundle；立即终止本次新启动的该进程，未对其执行界面操作。后续均按唯一 QA 身份绑定，未复用普通实例；该误启动不能作为隔离验证证据，也不能据此宣称旧实例启动期间完全未读取默认配置。

原生启动日志 `native-website-light.log`、`native-website-dark-unique.log`、`native-website-light-unique.log`、`native-store.log`。IMK mach-port 日志未导致启动失败；全部本次测试进程已关闭。实际 SFTP 加密握手由上述 loopback 测试验证；没有用原生窗口访问真实 SFTP 服务器或保存真实 Keychain 凭据。

## 仍需外部补齐及发布边界

- 正式 App Store Bundle ID、开发者主体信息与 App Store Connect 配置，Mac App Distribution／Installer 证书和匹配 provisioning profile 尚未提供；本次不编造、不发布、不上传。
- CI 尚未提交推送，远端两渠道 job 需要在代码获准提交后实际运行。
- QA ad-hoc bundle、模拟作用域与 headless 测试不证明正式签名包的原生 picker、重启后的 security-scoped bookmark、helper、Quick Look、受保护目录、Keychain 锁定／拒绝、真实设备 LocalSend 多网卡互通及 App Review 完整接受。保留这些正式环境验收项。
- 本次仅构建／运行 arm64。依赖提供的 universal dylib 仍可含 x86_64 切片；没有执行 Intel 构建或测试。所有构建保留既有 warning，未描述为无警告。
