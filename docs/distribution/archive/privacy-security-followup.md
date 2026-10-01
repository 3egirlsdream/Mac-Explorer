# 隐私、ZIP 与 SFTP 后续修复交付记录

> 历史执行与验收记录；当前构建和发布说明见 [双渠道说明](../mac-app-store.md)。

> 2026-10-01 后续交付与当前状态见 [可靠性与发布保障](reliability-release-followup.md)。下文保留其原审核日期与当时事实；正式隐私政策现位于 https://3egirlsdream.github.io/Mac-Explorer/privacy/ 。

日期：2026-09-30。工作目录：`/Users/jiangxinji/Documents/FKFinder`。本次只做本地实现与验证，没有提交、推送、升级应用版本或发布。

2026-10-01 独立复核已完成，并补修隐私许可持久化失败反馈；新 Release arm64 两渠道构建及 30／35 项针对性测试通过。见 [独立验收记录](privacy-security-review.md)。下文保留执行会话当时的结果；本轮按用户要求未测试 Intel。

## 交付范围与基线

开始时工作区已有大量商店渠道、Copilot、LocalSend 等未提交改动。本次以 `/private/tmp/fkfinder-privacy-sftp-baseline.oy5sjck5/manifest.json` 和 `files/` 的逐文件快照为基线，HEAD 为 `6baa8b1ce775fa9616a2a9938825abaa8541ec48`。下列是相对该快照的变更，不将原有脏文件全部计为本次实现。原版本 1.0.50 保留。

### 照片地点与 Copilot

- 照片地点联网解析默认关闭，设置开关先说明 GPS 坐标将交给 Apple 地理编码服务，用户明确同意才写入许可。
- C# 仅在许可有效时传递进程专属凭证；Swift 在创建 `CLGeocoder` 之前读取并比较当前凭证。缺失参数、文件缺失、错误凭证、关闭、重新授权或主进程重启后的旧凭证都不能继续触发联网。撤回不能收回已发出的请求。
- 本地 GPS 与坐标回退、OCR、分类、人脸、日期和相机处理保留。新增 ImageIO GPS／相机回退，保证尚未被 Spotlight 索引的照片也可读取本地元数据。没有升级分析版本、批量重分析或清除旧结果。
- 现有 Copilot 接收地址／模型／密钥许可及每次 HTTP 发送检查保留，设置中可撤回；已有 HTTP 客户端后续发送也会拒绝。正文仍走既有审批流程。
- 设置“关于”和首次 Copilot 许可均有政策入口。默认明确显示“尚未发布”，查看随包本地草案；`PrivacyPolicyUrl` MSBuild 属性只有合法 HTTPS URL 才启用线上入口。未填虚构 URL、联系方式或第三方保留期限。
- 对话框复用 `DialogWindow`、`SettingsStyles.axaml`、主题资源和紧凑按钮。确认默认聚焦取消，标题栏透明，正文不重复标题。远程窗口初始高度按是否有已保存连接调整为 500／600，窄尺寸正文滚动，底部按钮固定。具体数据流见 [privacy-data-flow.md](../privacy-data-flow.md)。

### 加密 ZIP

仅替换加密 ZIP 写入分支为 SharpZipLib：明确 `AESKeySize = 256`、UTF-8 名称，保留原相对目录、重复名称处理、修改时间、进度、临时输出与成功后原子替换。复制每 64 KiB 检查取消，写入或取消异常删除临时文件。普通压缩及所有读取仍由原有 SharpCompress 承担；没有重新实现解压。

旧 DotNetZip 生成的中文 AES256 固定包可解压；新包经过 SharpZipLib 加密参数检查和 SharpCompress 独立解压验证。没有声称 Finder 自带归档工具支持 WinZip AES，也未做所有第三方归档工具验收。

### SFTP 与 Keychain

- 首次 SSH 握手展示主机、端口、算法与 OpenSSH 形式的 SHA256 指纹；提示通过独立渠道核对。取消、关闭或缺少确认回调均拒绝；信任记录保存成功后才允许 SSH 认证。
- 信任以实际 `ConnectionInfo.Host/Port` 建立身份键，主机做 IP／IDN／大小写规范化，不以用户填写的显示名称为键。已知主机核对密钥；变化时显示旧／新指纹并拒绝，不覆盖。自动重连仅接受已知匹配密钥。
- 比对公钥原始字节的 SHA256，不将协商签名算法变化误判为换密钥；例如同一 RSA 公钥从 rsa-sha2-512 切换到 rsa-sha2-256 仍保持信任。依据 [RFC 8332](https://www.rfc-editor.org/rfc/rfc8332.html)，两算法复用 ssh-rsa 公钥格式和指纹。
- 撤销指定主机信任必须明确确认，并断开该主机活动连接；不删除其他主机信任。不提供静默重置不可读信任文件的途径。
- 保存／删除先准备候选配置，持久化成功才改内存状态；文件保存失败会恢复目标旧凭据，恢复失败也明确显示。未迁移的其他密码先成功写入 Keychain，才可从 JSON 移除。损坏的原配置不能被后来保存或删除覆盖。
- 迁移失败保留配置与密码，窗口显示原因并有重试入口。Keychain 区分锁定、拒绝、取消和不可用。失败后表单保留；切换连接同步密码／私钥区域，凭据重试后重载当前连接。

## 官方依赖核查

核查日期为 2026-09-30，事实依据如下；自动审计结果只覆盖当时源中的已知公告。

| 依赖 | 最终用途与版本 | 官方依据与限制 |
| --- | --- | --- |
| DotNetZip 1.16.0 | 已移除生产引用与随包 DLL | [GHSA-xhg6-9j5j-w4vf](https://github.com/advisories/GHSA-xhg6-9j5j-w4vf) 涉及 `ZipEntry.Extract.cs` 路径穿越，受影响范围包含 1.16.0，DotNetZip 无修补版本。原项目仅用它写 AES ZIP，不能据此认定应用已经走过该漏洞提取路径。 |
| SharpZipLib 1.4.2 | WinZip AES256 ZIP 写入 | [官方项目](https://github.com/icsharpcode/SharpZipLib) 说明纯 C#、AES 与 ZIP64 支持；[API](https://icsharpcode.github.io/SharpZipLib/api/ICSharpCode.SharpZipLib.Zip.ZipOutputStream.html)；[官方最新稳定发布](https://github.com/icsharpcode/SharpZipLib/releases/tag/v1.4.2)。[NuGet](https://www.nuget.org/packages/SharpZipLib/1.4.2) 提供 net6.0／netstandard2.0／2.1，net6.0 资产不增加包依赖，已在本项目 .NET 10 arm64 两渠道编译和实测。 |
| SSH.NET 2026.0.0 | SftpClient 传输 | [官方发布](https://github.com/sshnet/SSH.NET/releases/tag/2026.0.0) 为 2026-08-09，包含 .NET 10 支持和安全修复，未列出已知破坏性改动。替换原 2025.0.0；现有 SFTP API 编译及真实握手验证通过。 |

SharpZipLib 仓库当前未显示归档标记，但稳定包更新日期仍是 2023-01-30，发布页仅显示此后 5 次提交；这只能支持采用其现有正式稳定实现，不能描述成近期高频维护。正式发版仍应复查公告与上游维护状态。没有引入本地 fork 或自写密码学。

SSH.NET 的 [GHSA-h5q6-2gr6-3g3m](https://github.com/sshnet/SSH.NET/security/advisories/GHSA-h5q6-2gr6-3g3m) 是认证前标识交换无界缓冲，[GHSA-vhpg-4g9v-rppq](https://github.com/sshnet/SSH.NET/security/advisories/GHSA-vhpg-4g9v-rppq) 是通道最大包为零导致循环，两者修补版本均为 2026.0.0，与共用 SSH 传输相关。另有 [SCP 递归下载路径问题](https://github.com/sshnet/SSH.NET/security/advisories/GHSA-q939-rpr3-3284) 和 [SCP 路径转换问题](https://github.com/sshnet/SSH.NET/security/advisories/GHSA-mggc-4xg6-vcxf)；应用使用 SftpClient，没有据 SCP 公告宣称 SFTP 已发生任意写入。

两库均使用精确版本的 MIT 许可证：[SharpZipLib v1.4.2 LICENSE](https://github.com/icsharpcode/SharpZipLib/blob/v1.4.2/LICENSE.txt)、[SSH.NET 2026.0.0 LICENSE](https://github.com/sshnet/SSH.NET/blob/2026.0.0/LICENSE)。原文保存在 `ThirdParty/Notices/` 并复制到两渠道 `Contents/Resources/Notices/`；历史 DotNetZip LICENSE 留在源代码作出处记录，不再随包复制。本次 notices 是补充，不代表全部第三方依赖许可证清单。

`dotnet list MacExplorer.csproj package --vulnerable --include-transitive` 退出 0，报告当前给定源中无已知易受攻击包。实际两个新包的 `MacExplorer.deps.json` 均含 `SharpZipLib/1.4.2`、`SSH.NET/2026.0.0`；包内未发现 DotNetZip、System.Drawing.Common、System.Security.Permissions，许可证文件均存在。日志：`/private/tmp/fkfinder-followup-audit.log`。

## 构建与自动验证

全部默认服务／真实文件测试经隔离脚本启动，每个进程独立 `MACEXPLORER_TEST_ROOT`。测试数据是生成的 JPEG、旧加密 ZIP、临时数据库和回环服务器；未用用户照片、真实历史、SSH 配置、SSH agent 或 Keychain。商店编译的文件测试用仅限本次临时根的测试授权，不能据此认定真实 OS 沙盒已经验收。

构建命令：

```sh
dotnet build Tests/MacExplorer.Tests/MacExplorer.Tests.csproj --artifacts-path /private/tmp/fkfinder-followup-website-final -p:SkipMacOSReleaseDMG=true -p:RuntimeIdentifier=osx-arm64
dotnet build Tests/MacExplorer.Tests/MacExplorer.Tests.csproj --artifacts-path /private/tmp/fkfinder-followup-store-final -p:SkipMacOSReleaseDMG=true -p:RuntimeIdentifier=osx-arm64 -p:DistributionChannel=AppStore -p:AppStoreBundleId=com.macexplorer.app.store.qa
```

两渠道最终均退出 0、468 个警告、0 错误，主要为既有 NU1510、MVVMTK／Avalonia、平台及 xUnit 分析器警告，不能称为无警告构建。完整日志：
`/private/tmp/fkfinder-followup-website-final-build.log`、
`/private/tmp/fkfinder-followup-store-final-build.log`。

为可重现独立 artifacts 输出，测试指定已有的仓库／应用输出环境变量。回环测试需要独立 virtualenv 的 Paramiko（本机临时测试环境为 5.0.0），缺少显式 Python 路径时会跳过，因此不能把跳过当通过：

```sh
task_repo=/Users/jiangxinji/Documents/FKFinder
task_web=/private/tmp/fkfinder-followup-website-final/bin
task_store=/private/tmp/fkfinder-followup-store-final/bin
task_python=/private/tmp/fkfinder-sftp-qa-venv/bin/python

MACEXPLORER_TEST_REPOSITORY="$task_repo" MACEXPLORER_TEST_APP_OUTPUT="$task_web/MacExplorer/debug_osx-arm64/Mac Explorer.app/Contents/MacOS" MACEXPLORER_SFTP_TEST_PYTHON="$task_python" bash Tools/Testing/run-isolated.sh "$task_web/MacExplorer.Tests/debug_osx-arm64/MacExplorer.Tests" -class MacExplorer.Tests.PrivacySecurityFollowupTests -class MacExplorer.Tests.SecurityDialogTests -class MacExplorer.Tests.SftpLoopbackTests

MACEXPLORER_TEST_REPOSITORY="$task_repo" MACEXPLORER_TEST_APP_OUTPUT="$task_web/MacExplorer/debug_osx-arm64/Mac Explorer.app/Contents/MacOS" bash Tools/Testing/run-isolated.sh "$task_web/MacExplorer.Tests/debug_osx-arm64/MacExplorer.Tests" -class MacExplorer.Tests.ArchiveServiceTests -class MacExplorer.Tests.ArchivePathHelperTests -class MacExplorer.Tests.ArchiveExtractionPathHelperTests -class MacExplorer.Tests.CopilotCoreTests -class MacExplorer.Tests.CopilotReviewRegressionTests -class MacExplorer.Tests.CopilotWindowReviewTests -class MacExplorer.Tests.CopilotIndexedSearchTests -class MacExplorer.Tests.CopilotReasoningReplayTests -class MacExplorer.Tests.CopilotEmptyReasoningTests -class MacExplorer.Tests.DistributionCommonTests -class MacExplorer.Tests.PdfAnalysisTests -class MacExplorer.Tests.FileListLoadingPipelineTests

MACEXPLORER_TEST_REPOSITORY="$task_repo" MACEXPLORER_TEST_APP_OUTPUT="$task_store/MacExplorer/debug_osx-arm64/Mac Explorer.app/Contents/MacOS" MACEXPLORER_SFTP_TEST_PYTHON="$task_python" bash Tools/Testing/run-isolated.sh "$task_store/MacExplorer.Tests/debug_osx-arm64/MacExplorer.Tests" -class MacExplorer.Tests.PrivacySecurityFollowupTests -class MacExplorer.Tests.SecurityDialogTests -class MacExplorer.Tests.SftpLoopbackTests -class MacExplorer.Tests.DistributionCommonTests
```

| 最终运行 | Total | Errors / Failed / Skipped | 日志 |
| --- | ---: | --- | --- |
| 官网新增隐私／ZIP／SFTP／浅深色对话测试 | 17 | 0 / 0 / 0 | `/private/tmp/fkfinder-followup-security-final-tests.log` |
| 官网既有相关回归 | 111 | 0 / 0 / 0 | `/private/tmp/fkfinder-followup-regression-tests.log` |
| 商店编译同一测试及渠道回归 | 34 | 0 / 0 / 0 | `/private/tmp/fkfinder-followup-store-tests.log` |

17 项含 12 个隐私／ZIP／存储测试、浅／深色 4 个 headless 对话测试，以及 1 个真实 SSH.NET→Paramiko TCP 握手测试。握手测试证实首次取消不进入密码认证，信任后连接并列目录，重复连接不提示，换同端口密钥拒绝且不改记录，明确忘记后拒绝仍不认证，信任文件保存失败不认证，修复后可连接。指纹确认回调在握手测试中是可控替身，真实确认界面需另行原生验收。

Swift 边界测试编译生产中的许可函数，使用 CLGeocoder spy 验证调用计数；另一个测试实际启动本次编译的图片 helper，从合成 JPEG 读取 GPS／相机及 OCR。没有调用真实 Apple 地理编码网络服务，也没有使用真实 AI 接收方。

浅深色 headless 验证确认按钮／取消／Escape 结果、owner 保持打开、400×450 远程窗口底部按钮可见，以及保存／删除失败保留表单、私钥区域切换、凭据重新加载。Keychain 失败为替身模拟，尚不是系统钥匙串权限窗口验收。

初期构建修正了 Avalonia Headless `KeyPress` 参数编译错误；首次独立输出测试因未指定仓库路径失败；第一次商店测试因临时数据未授权失败。上述问题已修正，表格只记录重跑后的非零最终通过结果，没有计入失败或跳过为通过。

静态包检查：

```sh
python3 Tools/Distribution/verify-bundle.py '/private/tmp/fkfinder-followup-website-final/bin/MacExplorer/debug_osx-arm64/Mac Explorer.app' Website
python3 Tools/Distribution/verify-bundle.py '/private/tmp/fkfinder-followup-store-final/bin/MacExplorer/debug_osx-arm64/Mac Explorer.app' AppStore
```

两次退出 0、`errors: []`，日志分别为 `/private/tmp/fkfinder-followup-website-bundle.log` 和 `/private/tmp/fkfinder-followup-store-bundle.log`。只证明脚本列出的包结构、渠道及 native 架构／最低系统信息，未证明商店签名、entitlements 生效、真实 security-scoped picker 或 Store 发布。

## 原生验证与外部限制

首次隔离官网实例 `/private/tmp/fkfinder-test.UMjKGi` 中原生查看了设置默认关闭、Apple GPS 告知、Copilot 撤回入口、“尚未发布”及本地草案；应用内切换深色后查看了隐私设置。未修改系统主题或复用普通运行实例。原生自动化取消／Escape 的窗口目标曾不稳定，不将其计为通过；最终取消路径由 headless 覆盖。

随后修正分组圆角、政策正文滚动条边距等，并直接启动最终官网包到新的隔离根 `/private/tmp/fkfinder-test.gsNzmO`。Mac 一度锁定，恢复访问后已原生验证浅色／深色隐私分组、透明标题栏、默认关闭、取消按钮键盘激活和 Escape 取消均返回设置并保持关闭，政策正文无重复标题且为滚动条留有边距。

同一隔离实例的深色远程窗口连接本地生成的回环 SFTP 服务器，展示完整 SHA256 指纹、真实主机端口及 `rsa-sha2-512`，默认聚焦取消。取消返回表单并明确提示未保存主机信任，输入保留；服务器日志的认证事件为 0，应用未创建信任记录。服务端单独经隔离脚本启动于 `/private/tmp/fkfinder-test.AY6Pj6`，没有填写真实或测试密码，也没有授予服务器信任。CUA 拖动原生缩放返回 `AXError.notImplemented`，因此 400×450 布局仍只计 headless 通过；不把尝试缩放当原生通过。未访问真实受保护目录或真实 Keychain，不将锁屏或工具限制计为普通自动测试失败。

初始高度调整后的官网新实例 `/private/tmp/fkfinder-test.uPqXkt` 中原生查看了 480×500 浅色远程窗口：主字段与底部按钮完整，切换私钥后字段显示正确，滚动可到达凭据重试与撤销信任操作，底部按钮始终可见。所有本次启动的原生 QA 实例与回环服务器已停止，普通应用／设计器实例未停止。

正式上架仍需：真实商店沙盒进程与目录选择器／书签／文件操作／helper 授权验收；系统 Keychain 锁定、拒绝和恢复流程；外部 SFTP 服务器、私钥、IPv6 与真实密钥轮换；Intel 包和实体 Intel 运行；确认政策 URL、联系方式、第三方数据处理与 App Store Connect 隐私标签。未执行发布、提交审核或隐私政策上线。

## 相对本次快照的文件清单

“修改”包括开始时已未跟踪但有快照的文件。未删除原文件。

```text
修改 AGENTS.md
修改 App.axaml.cs
新增 Assets/PrivacyPolicyDraft.txt
修改 Copilot/CopilotSettings.cs
修改 Copilot/PrivacyConsentHandler.cs
修改 MacExplorer.csproj
修改 Platforms/MacCatalyst/Services/MacImageAnalysisService.cs
修改 Platforms/MacCatalyst/Services/MacThemeService.cs
修改 Platforms/MacOS/ImageAnalysisHelper.swift
修改 Services/IRemoteConnectionService.cs
修改 Services/Impl/ArchiveService.cs
修改 Services/Impl/KeychainCredentialStore.cs
新增 Services/Impl/PhotoLocationConsent.cs
修改 Services/Impl/RemoteConnectionService.cs
新增 Services/Impl/SftpHostKeyStore.cs
修改 Tests/MacExplorer.Tests/FileListLoadingPipelineTests.cs
修改 Tests/MacExplorer.Tests/MacExplorer.Tests.csproj
新增 Tests/MacExplorer.Tests/PrivacySecurityFollowupTests.cs
新增 Tests/MacExplorer.Tests/SecurityDialogTests.cs
新增 Tests/MacExplorer.Tests/SftpLoopbackTests.cs
新增 Tests/MacExplorer.Tests/TestData/gps-ocr-fixture.jpg
新增 Tests/MacExplorer.Tests/TestData/legacy-dotnetzip-aes256.zip
新增 Tests/MacExplorer.Tests/TestData/security-fixtures.md
新增 Tests/MacExplorer.Tests/TestData/sftp-loopback.py
新增 ThirdParty/Notices/README.md
新增 ThirdParty/Notices/SSH.NET.LICENSE.txt
新增 ThirdParty/Notices/SharpZipLib.LICENSE.txt
新增 Views/Dialogs/ConfirmDialog.cs
修改 Views/Dialogs/CopilotPrivacyConsent.cs
新增 Views/Dialogs/PrivacyPolicy.cs
修改 Views/Dialogs/RemoteConnectionDialog.axaml
修改 Views/Dialogs/RemoteConnectionDialog.axaml.cs
修改 Views/Dialogs/SettingsDialog.Copilot.cs
新增 Views/Dialogs/SettingsDialog.Privacy.cs
修改 Views/Dialogs/SettingsDialog.axaml
修改 Views/Dialogs/SettingsDialog.axaml.cs
新增 Views/Dialogs/SftpHostKeyConsent.cs
修改 docs/distribution/privacy-data-flow.md
新增 docs/distribution/privacy-security-followup.md
```

字节比较确认相对基线修改 21 个已有文件、新增 18 个文件，另外 721 个基线文件保持原样，没有删除文件；HEAD 保持不变，暂存区为空。本次相对基线 diff 留在 `/private/tmp/fkfinder-followup-relative-baseline.patch`，路径列表在同名前缀的 `.json` 文件。

测试主题探测只增加 `RuntimePaths.TestRoot` 下本机系统偏好读取的跳过；生产主题行为未改。fixture 来源见 [security-fixtures.md](../../../Tests/MacExplorer.Tests/TestData/security-fixtures.md)。基线外完整 diff 与日志均未包含真实密码；测试密码是公开固定的合成值。
