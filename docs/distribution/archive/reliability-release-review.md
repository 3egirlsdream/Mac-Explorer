# 失败恢复、加密私钥与正式隐私政策：独立验收

> 历史执行与验收记录；当前构建和发布说明见 [双渠道说明](../mac-app-store.md)。

验收日期：2026-10-01。执行会话 `01a0f347-14eb-7521-b2f3-2f99db858df4`，GPT-6.1 Sol / high。本会话在执行结束后检查源码、产物、测试记录和公网部署，并补齐两个遗漏。

结论：本轮授权的开发工作和官网政策上线完成；arm64 官网版与商店 QA 版的本地定向复核通过。正式商店签名、钥匙串和 App Sandbox 全链路验收仍有外部条件，不能称为可直接上架。

## 范围与归属

对照 `/private/tmp/fkfinder-recovery-policy-baseline.f7dow4dw` 的 761 个文件快照，未将整个 HEAD dirty diff 当成本轮工作。开始复核时，31 个基线文件变化、107 个新增文件，与执行会话的 138 个路径完全一致，730 个基线文件未变化，无基线文件删除。

独立复核另改了 `Services/Impl/RemoteConnectionService.cs`、`Tests/MacExplorer.Tests/SftpLoopbackTests.cs`、`ThirdParty/Notices/dependencies.json`、`ThirdParty/Notices/README.md`，以及本记录和实施记录的链接。未提交、推送应用或 CI，未升级 1.0.50、发布软件或上传商店。复核期间另出现 `.gitignore` 的变化和 `Folder.DotSettings.user` 的暂存删除；未触碰这些其他操作，不计入本轮。main HEAD 仍为 `6baa8b1ce775fa9616a2a9938825abaa8541ec48`。

## 开发验收与补修

| 项目 | 结果与边界 |
| --- | --- |
| LocalSend 关闭失败恢复 | 写入关闭设置失败时先禁止新操作，再停止监听、发现及活动会话；保持关闭并提示重启风险和重试。修复存储后重试能持久保存。隔离 SQLite 故障测试通过；执行会话的原生记录显示关闭后相关 TCP/UDP 监听为零。 |
| 目录授权恢复 | 读取、损坏记录备份或恢复写入失败不终止启动、不覆盖原记录；失败时不发布临时 scope、不扩大授权。恢复、撤销、重试和新授权会更新索引。scope 计数、原记录保留及索引重启测试通过。 |
| 加密 SFTP 私钥 | 默认不保存口令，显式勾选才写入凭据存储，口令不进 JSON。取消、错误口令、主机指纹拒绝和变化拒绝、保存/删除失败、旧密码迁移及无口令私钥兼容通过。真实回环 SFTP 使用独立临时目录和内存凭据替身。 |
| 私钥口令生命周期补修 | 原实现不持久保存，但断开后内存对象仍可能持有一次使用口令。本次补上断开、关闭全部连接及重连最终失败时清理。关闭全部连接也覆盖连接已丢失但仍重连的记录。同一进程、同一已保存对象连续连接的回环断言确认：断开后口令为空，第二次连接重新询问，凭据替身没有记录。活跃连接自动重连期间仍可使用当前连接口令。 |
| CI 与正式预检 | Website/AppStore arm64 矩阵使用有效 `-class`/`-method-` 参数，检查非零 Total 和零失败/跳过。测试项目复制本次构建的原生 helper。`macos-15` arm64 标签已对照 [GitHub 官方运行器说明](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)。CI 尚未推送，没有远端 CI 成功运行的结论。 |
| 许可清单补修 | 新许可门禁遗漏官网既有 Intel 发布步骤需要的两份运行时记录，会拒绝该版本。本次比较缓存的官方 .NET / ASP.NET 10.0.5 两架构 LICENSE 与 THIRD-PARTY-NOTICES，字节一致；补齐 x64 记录，复用相同 notice。没有进行 Intel 构建、测试或运行。 |
| 包与资源 | 新 arm64 两渠道包均通过结构、部署版本和渠道限制审计；许可清单覆盖 83 个包记录、412 个代码制品名称及 9 组资源，errors 为空。商店包保留内置转换，排除外部插件包装。许可材料检查不能代替 Apple 最终审核。 |

## 独立构建与测试证据

构建均为 Release / osx-arm64，带 `-p:SkipMacOSReleaseDMG=true`，版本 1.0.50、最低系统版本 14.0。Website 标识 `com.macexplorer.app`；商店 QA 标识 `com.macexplorer.app.store.qa`。

- 官网首轮补修构建 468 个现有警告、0 错误；许可补齐后增量构建 6 个警告、0 错误。日志 `/private/tmp/fkfinder-independent-reliability-build-website.log` 和 `...-build-website-final.log`。
- 商店 QA 构建 468 个现有警告、0 错误。日志 `/private/tmp/fkfinder-independent-reliability-build-store.log`。
- 最终新构建运行器复跑 `ReliabilityRecoveryTests`、`SftpLoopbackTests`、`SecurityDialogTests`、`PrivacySecurityFollowupTests`、`DistributionCommonTests`：**两渠道各 Total 42，Errors/Failed/Skipped/Not Run 均为 0**。经 `Tools/Testing/run-isolated.sh`，根目录分别为 `/private/tmp/fkfinder-test.MdhxHP` 和 `/private/tmp/fkfinder-test.hDR2Zo`；fixture Python 为 `/private/tmp/fkfinder-sftp-qa-venv/bin/python`。
- 输出 `/private/tmp/fkfinder-independent-reliability-tests-website.log`、`...-tests-store.log`；`verify-test-summary.py` 确认非零 Total。
- 新包审计 `...-bundle-website.json`、`...-bundle-store.json`、`...-notices-website.json`、`...-notices-store.json` 全部 errors 为空；`...-apps.json` 保存实际标识、政策 URL、随包政策与源码一致性及主程序集 SHA256。上述省略前缀均为 `/private/tmp/fkfinder-independent-reliability`。
- 正式预检 3 项 Python 测试通过（`...-preflight.log`）。QA 包进入 PKG 流程被明确拒绝，未生成 PKG，原因是实际商店标识尚未配置（`...-pkg-rejection.log`）。拒绝条件验证不等于正式 PKG 成功验收。
- 执行会话之前两渠道各 Total 105、全部通过（`/private/tmp/fkfinder-reliability-logs/tests-website-final.log`、`tests-store-final.log`）。这是补修前的完整记录；本次按改动风险复跑 42 项，没有将旧 105 项计作最新全量重跑。

最终产物：`/private/tmp/fkfinder-reliability-website/bin/MacExplorer/release_osx-arm64/Mac Explorer.app`、`/private/tmp/fkfinder-reliability-store/bin/MacExplorer/release_osx-arm64/Mac Explorer.app`。临时证据可能被系统清理，本记录保留结果及复现路径。

## 官网正式政策

地址：[https://3egirlsdream.github.io/Mac-Explorer/privacy/](https://3egirlsdream.github.io/Mac-Explorer/privacy/)，联系邮箱 `xulezuo@hotmail.com`。

网站提交 `aed0f854c6ef0e6c5f6f013ee862d49f5fb87c35` 仅含首页和政策 HTML/CSS/JS；后续 `d6586243ffe1c290032a53134f3b02d20abff530` 仅更正政策 HTML 的插件权限边界。独立查询 Pages 最新构建：后一个提交、`status=built`、error 为空，更新时间 `2026-09-30T17:28:08Z`。

独立公网 GET：政策和首页均 HTTP 200，正文与工作区 HTML 字节一致，邮箱及首页入口存在；政策 CSS/JS、共用样式和 favicon 也均 HTTP 200。证据 `/private/tmp/fkfinder-independent-reliability-live-policy.json`、`...-policy-resources.json` 及抓取 HTML。

随包政策、程序集入口和 Info.plist URL 一致。内容涵盖本地分析、Apple GPS 默认关闭及撤回、Copilot 接收方/正文独立审批、SFTP 凭据与信任、LocalSend、官网版插件与 HTTP 更新接口、网站托管、保存删除及联系权利。说明功能随版本/渠道而异，网站政策上线不代表未发布的应用改造已可下载。

## 验证边界与剩余事项

本次真实文件、SQLite、网络 fixture 和默认路径经测试根隔离；目录书签/凭据故障部分使用替身。原生 helper OCR 实际执行，Swift 网络入口另有替身调用计数。这些运行器测试**不是 App Sandbox 运行验收**。

执行会话完成新应用的浅/深色私钥表单、真实 picker/书签失败及恢复、LocalSend 关闭失败与端口停止的原生 QA。本会话核对 `native-observations.json` 等记录，未重复原生操作。窄窗口只有 headless 验证，原生调整尺寸返回 `AXError.notImplemented`。执行记录披露一次按名称误启动旧捕获应用并立即停止，不能保证该旧应用启动期间未读默认配置；本次未复用该启动方式。

仍需真实系统钥匙串的保存/撤回/锁定体验、实际部署服务，以及正式签名商店包的 Sandbox 文件访问、辅助进程、网络全流程验证；还需实际 App Store 标识、Apple 账号、分发证书及匹配 profile，正式签名 PKG 创建及 Apple 验证。`release-config.json` 正式标识保持未配置，门禁拒绝 QA 身份及缺项。本次未执行上述外部验证或上传发布软件。

交付后暂停 `mac-app-store` 跟进，避免重复检查。
