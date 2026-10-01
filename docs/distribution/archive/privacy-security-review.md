# 隐私、ZIP 与 SFTP 独立验收

> 历史执行与验收记录；当前构建和发布说明见 [双渠道说明](../mac-app-store.md)。

> 2026-10-01 后续交付与当前状态见 [可靠性与发布保障](reliability-release-followup.md)。下文保留其原审核日期与当时事实；正式隐私政策现位于 https://3egirlsdream.github.io/Mac-Explorer/privacy/ 。

验收日期：2026-10-01。执行会话：`01a0f2cc-da41-77f0-a9f0-0227a4e22ef4`（GPT-6.1 Sol、极高）。执行会话停止后，本会话独立比较源码、检查证据，并补修持久化失败反馈。

本轮三组开发已完成，针对性验证通过。真实 App Sandbox、系统 Keychain 与正式商店发布流程尚未验收，因此不能据此称为可直接上架。没有提交、推送、发布、上传或升级版本；按用户要求只构建和测试 arm64。

## 基线与归属

派发前快照为 `/private/tmp/fkfinder-privacy-sftp-baseline.oy5sjck5`，包含 `manifest.json`、`files/` 和原有 `tracked.patch`。HEAD 仍为 `6baa8b1ce775fa9616a2a9938825abaa8541ec48`，版本仍为 1.0.50。

补修之前，逐文件哈希比较确认执行会话修改 21 个已有文件、新增 18 个文件，另外 721 个基线文件未变，无删除，与执行记录一致。证据为 `/private/tmp/fkfinder-independent-privacy-review-manifest.json` 和同前缀 `.patch`。没有将整个 HEAD dirty diff 归为本轮开发。

本会话额外修改 `SettingsService.cs`、`SettingsDialog.Privacy.cs`、`CopilotPrivacyConsent.cs`、`PrivacyPolicyDraft.txt`、`PrivacySecurityFollowupTests.cs`，并新增本验收记录、在执行记录中追加入口。没有覆盖其他会话改动。

## 逐项结论

| 已授权范围 | 独立检查结果 | 验证范围与限制 |
| --- | --- | --- |
| 照片 GPS 联网许可 | 默认关闭；明确说明 GPS 发送至 Apple；关闭后清空持久凭证，旧 helper 凭证失效。Swift 在创建地理编码器前校验许可；本地 GPS、OCR 与已有结果保留。 | 测试编译生产许可函数并用 geocoder spy 验证边界，实际新 helper 读取合成 JPEG 的 GPS／相机／OCR。没有调用真实 Apple 地理编码服务。 |
| Copilot 共享与撤回 | 设置有撤回入口；每次最终 HTTP 发送校验当前接收方、模型、密钥和许可，已创建 transport 的后续发送也受限制。正文独立审批保留。 | 真实持久化失败、恢复后重试和重载通过；HTTP 发送使用可计数替身，没有向真实 AI 服务发送数据。 |
| 政策入口及数据流 | 本地草案明确“尚未发布”；合法 HTTPS 配置才启用线上政策入口，没有伪造公开 URL。补正官网启动后自动检查更新的说明。首次 Copilot 确认默认聚焦取消。 | 新构建原生设置检查及既有政策入口记录；正式 URL、联系信息、供应商处理说明和商店标签待产品负责人确认。 |
| 加密 ZIP | SharpZipLib 1.4.2 替换 DotNetZip 加密写入；AES256、UTF-8 中文名、进度、取消和临时输出清理保留，普通读写继续使用 SharpCompress。 | 旧 DotNetZip 中文 AES256 固定包读取、新包独立解压、AES 参数、错误密码和取消清理通过。没有声称所有外部归档工具兼容。 |
| SFTP 主机身份 | 首次明确确认 SHA256 公钥指纹；拒绝、取消、无回调或信任保存失败均不能进入认证。实际主机／端口为信任身份，已知换钥拒绝且不覆盖。同公钥的协商签名算法变化不误报换钥。 | SSH.NET 2026.0.0 到临时 Paramiko 服务器的真实 TCP 握手、列目录、重连、换钥和存储失败场景通过。确认回调受控；执行会话另有原生取消且认证事件为 0 的证据。 |
| SFTP 凭据与兼容 | 保存／删除候选配置成功后才变更内存；失败回滚或清楚报告；迁移失败保留旧配置和其他连接密码，有重试入口；无法读取的配置不被静默覆盖。 | Keychain 失败、迁移与多连接保护使用测试替身；未操作真实 Keychain。SFTP 密码来自远程账号，应用没有默认密码；测试值只用于生成的回环服务器。 |

没有将 SCP 的专属公告当作当前 SftpClient 已证实漏洞。执行记录列明了更新依赖的官方依据和限制。

## 独立复核中补修的问题

`SettingsService.Persist` 原先吞掉 SQLite 写入异常，可能导致 Copilot 撤回显示成功，而磁盘保留旧许可。本次仅对两项隐私许可让持久化异常向调用者报告，保留已更新的内存状态以阻止当前进程分享；设置界面显示“撤回未能保存”，明确要求修复存储后重试，并提醒重启可能恢复旧许可。

新增真实 SQLite 回归测试：先许可，再用触发器拒绝两项许可写入，确认异常、当前许可关闭及照片凭证清空；移除触发器、重试，重新加载设置后仍为撤回。将此测试配合原执行会话的旧应用 DLL 运行，结果为 **Total 1、Failed 1**（预期缺陷复现：未抛出异常），日志 `/private/tmp/fkfinder-independent-privacy-old-settings-test.log`。修复后两渠道均通过该测试；这个刻意失败的旧实现对照不算最终通过结果。

原生复核直接启动新 Release 官网 `.app/Contents/MacOS/MacExplorer`，经 `run-isolated.sh` 创建 `/private/tmp/fkfinder-test.1Aiwnc`。在该临时数据库注入 `QA storage failure` 触发器后点击撤回，界面出现完整失败与重试提示；移除触发器再点击，恢复“尚未许可；下一次发送需要确认接收方。”，数据库许可为空。这里的原生路径验证错误反馈；已有许可的持久撤回由上面的真实 SQLite 回归覆盖。

同一新实例还确认照片联网默认关闭，确认窗口展示 Apple／GPS 告知、默认聚焦取消，Escape 返回后保持关闭且照片许可文件为空。没有点击允许、发送真实 GPS、填写真实 API Key、访问用户照片或系统偏好。QA 实例已按精确 PID 停止，普通应用实例未停止。日志 `/private/tmp/fkfinder-independent-privacy-native.log`；原生可访问树及截图保留在本会话工具记录。

## 最终构建与测试证据

两渠道新 Release 构建命令：

```sh
dotnet build Tests/MacExplorer.Tests/MacExplorer.Tests.csproj -c Release --artifacts-path /private/tmp/fkfinder-independent-privacy-website -p:SkipMacOSReleaseDMG=true -p:RuntimeIdentifier=osx-arm64
dotnet build Tests/MacExplorer.Tests/MacExplorer.Tests.csproj -c Release --artifacts-path /private/tmp/fkfinder-independent-privacy-store -p:SkipMacOSReleaseDMG=true -p:RuntimeIdentifier=osx-arm64 -p:DistributionChannel=AppStore -p:AppStoreBundleId=com.macexplorer.app.store.qa
```

| 独立复核最终运行 | 结果 | 证据 |
| --- | --- | --- |
| Website Release arm64 构建 | 退出 0，470 警告、0 错误 | `/private/tmp/fkfinder-independent-privacy-website-build.log` |
| AppStore Release arm64 构建 | 退出 0，470 警告、0 错误 | `/private/tmp/fkfinder-independent-privacy-store-build.log` |
| Website 针对性测试 | Total 30，Errors／Failed／Skipped／Not Run 全为 0，5.175s | `/private/tmp/fkfinder-independent-privacy-website-final-tests.log` |
| AppStore 编译版针对性测试 | Total 35，Errors／Failed／Skipped／Not Run 全为 0，21.064s | `/private/tmp/fkfinder-independent-privacy-store-final-tests.log` |
| 两个 Release bundle 静态检查 | 两次退出 0，`errors: []` | `/private/tmp/fkfinder-independent-privacy-website-bundle.json`、`/private/tmp/fkfinder-independent-privacy-store-bundle.json` |
| 当前依赖公告审计 | 退出 0，当前给定源未报告已知易受攻击包 | `/private/tmp/fkfinder-independent-privacy-audit.log` |

测试直接使用生成的 xUnit 运行器及有效 `-class` 参数，均经隔离脚本启动并确认非零 Total。Website 选择 `PrivacySecurityFollowupTests`、`SecurityDialogTests`、`SftpLoopbackTests`、`CopilotCoreTests` 和 `CopilotWindowReviewTests`；Store 选择前三类及 `DistributionCommonTests`。设定 `MACEXPLORER_TEST_REPOSITORY`、指向相应新 app 的 `MACEXPLORER_TEST_APP_OUTPUT`，以及 `/private/tmp/fkfinder-sftp-qa-venv/bin/python` 的 `MACEXPLORER_SFTP_TEST_PYTHON`，真实握手测试没有跳过。

执行会话此前的 Website 17 项新增、111 项相关回归、Store 34 项测试日志也已检查；历史结果见 [执行记录](privacy-security-followup.md)，不要与补修后的 30／35 重复相加或称为全量测试。原有构建警告仍存在，不称无警告构建。

两新包的部署声明均为 macOS 14.0；所有原生文件包含启动器所需的 arm64 架构，部分上游 dylib 仍含 x86_64 切片，这不代表测试了 Intel。商店包使用独立 QA 标识，沙盒／书签／网络 entitlements 及 helper 的 inherit 配置经静态脚本检查，深度严格签名完整性检查通过。当前签名仍是 **ad hoc、无 TeamIdentifier**，不能当作正式 Apple Distribution 签名或真实沙盒运行验证。

两包 `Contents/Resources/Managed/MacExplorer.deps.json` 均含 SharpZipLib 1.4.2／SSH.NET 2026.0.0，不含 DotNetZip、System.Drawing.Common、System.Security.Permissions、System.Windows.Extensions。两份随包 notice 与源码字节一致，并核对精确版本的 MIT 原文：[SharpZipLib](https://github.com/icsharpcode/SharpZipLib/blob/v1.4.2/LICENSE.txt)、[SSH.NET](https://github.com/sshnet/SSH.NET/blob/2026.0.0/LICENSE)。当前依赖审计只覆盖已知公告，不保证所有依赖不存在问题。

## 尚未完成的外部验收

- 本轮没有真正以正式商店身份验收 App Sandbox 下的目录选择、跨重启书签、真实文件操作和 helper 授权。商店编译测试的临时根授权、路径隔离和 entitlements 静态检查不能代替它。
- 系统 Keychain 的锁定、拒绝、恢复、旧凭据迁移仍需原生验收；当前使用替身，不触碰用户钥匙串。外部 SFTP 服务、真实私钥、IPv6 和真实密钥轮换未验收。
- 正式证书、账号、profile、发布标识及签名 PKG 成功流程尚待具备对应资源后验证；未生成或上传正式商店交付物。前轮渠道和打包实现详见 [商店改造记录](mac-app-store-review.md)。
- 隐私政策公开 URL、开发者联系信息、第三方处理事实与 App Store Connect 隐私标签仍需产品负责人落实。本文和随包草案不代表政策已上线。
- 用户明确不测试 Intel，本轮未构建或运行 Intel 验证；这不影响本轮约定的 arm64 开发验收，但不提供 Intel 兼容证明。

本轮可由代码补齐的开发事项已闭环；剩余上述项目应分别记录真实验证结果，不能由测试替身或构建成功推定通过。
