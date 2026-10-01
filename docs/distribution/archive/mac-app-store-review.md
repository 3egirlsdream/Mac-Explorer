# Mac App Store 双渠道改造独立验收（2026-09-30）

> 历史执行与验收记录；当前构建和发布说明见 [双渠道说明](../mac-app-store.md)。

> 2026-10-01 后续交付与当前状态见 [可靠性与发布保障](reliability-release-followup.md)。下文保留其原审核日期与当时事实；正式隐私政策现位于 https://3egirlsdream.github.io/Mac-Explorer/privacy/ 。

**结论：主要公共改造和渠道限制已落地，独立补修及本机针对性验证通过；正式上架验收尚未全部完成。**

审查对象为会话“Mac App Store 公共改造与双渠道适配”（`01a0f1aa-5e8c-79c2-81fb-a719370923a2`）停止后的工作区，派发基线为 `main` / `6baa8b1ce775fa9616a2a9938825abaa8541ec48`。版本保持 1.0.50。未提交、推送、发布或上传。本轮没有读写真实用户数据库、凭据或系统偏好；测试均通过 `Tools/Testing/run-isolated.sh` 使用临时数据。

## 独立复核后补齐的问题

1. **撤销授权后的索引生命周期**：原实现只在 `EnsureRoot` 检查授权，已经启动的监听和扫描仍可能处理后续事件。现在授权变化会停止失去权限的监听，扫描在读取及写入批次前复查，撤销后状态为不可访问，重新授权可重新建立索引。测试验证已启动的监听被释放、撤销后事件不写入新文件、重新授权后恢复。
2. **多台 SFTP 连接的迁移失败保护**：某台连接写入凭据存储失败后，随后保存另一台连接可能从 JSON 删除尚未迁移的密码。现在写配置前先保证所有剩余密码保存成功，失败时保留原配置；测试覆盖失败、后续保存、重试及重新加载。
3. **现有数据库初始化失败保护**：旧 `SqliteFileIndex` 在初始化异常时删除数据库并重建，会连带清空配置、收藏和历史。已移除此行为，保留原库并抛出错误；初始化失败的连接也会释放。测试覆盖两个构造入口，并检查已有数据仍在。此改动不提供自动修复损坏数据库的功能。
4. **正式 PKG 的签名条件**：除了拒绝 ad-hoc / Developer ID，现在要求明确的商店分发证书类型，并核对实际签名证书是否包含在 provisioning profile 的 `DeveloperCertificates` 中。语法检查和真实 ad-hoc 包拒绝路径通过；正式证书成功路径仍待验证。证书与安装包用途参考 [Apple 分发签名文档](https://developer.apple.com/documentation/xcode/creating-distribution-signed-code-for-the-mac/)和 [Mac 软件打包文档](https://developer.apple.com/documentation/xcode/packaging-mac-software-for-distribution)。

## 授权范围逐项核对

| 项目 | 代码复核结论 | 验证边界 |
|---|---|---|
| 旧数据与官网兼容 | SQLite backup 包含提交的 WAL，新库存在时不覆盖；迁移源保留；初始化失败不重置用户库 | 临时真实 SQLite 数据验证，未读取真实用户数据；官网相关回归通过 |
| 目录授权与索引 | 原生 bookmark 持有到应用退出，移动别名、离线重试、撤销、损坏记录备份均有处理；Store 搜索和服务检查授权路径 | 生命周期及路径边界使用替身测试；执行会话另有真实选择器与沙盒操作证据，实际离线/移盘设备尚未验收 |
| Copilot 分享许可 | 首次发送前需许可，端点/模型/密钥变化后 HTTP 发送边界再次阻止；正文保留原有独立审批 | 代码与隔离回归通过；真实第三方服务的数据处理/保留政策仍需落实 |
| SFTP 凭据 | 公共 SecItem API，成功保存后才从配置去掉密码；失败和重试保留连接信息 | 凭据存储替身验证通过，正式签名身份下 Keychain 与真实 SFTP 对端未验收 |
| 辅助进程与内置转换 | Store 使用随包固定转换代码；辅助程序继承沙盒，父进程传递 bookmark | 新 Release 包的辅助程序权限核对通过；执行会话的真实沙盒转换/PDF/OCR 测试通过，菜单内转换点击未完成原生验收 |
| Store 禁止项 | 自更新、插件市场/安装/外部执行、默认文件管理器偏好修改、任意终端脚本均有服务或进程入口限制；菜单隐藏之外还存在执行检查 | 服务负向测试及代码复核通过；官网功能路径保留 |
| 部署与资源许可 | 两渠道声明 macOS 14.0；原生代码和托管资源分区；移除 Apple 文件夹图资源打包；随包保留 LibRaw 源码/许可及相关 notice | 本轮 arm64 Debug/Release 产物核对通过；Intel 按用户要求不做本轮构建/运行复核；既有 Intel 记录不代表补修后的验证 |
| 网络 | 网络 client/server 权限、IPv4/IPv6 文件回传及旧 LocalSend API 兼容有处理 | 临时本机 HTTPS 回传通过，跨网发现与真实外部设备互通尚未验收 |

## 本轮独立验证证据

- 官网针对性测试：**Total 124，Errors 0，Failed 0，Skipped 0，Not Run 0**，6.911 秒。覆盖公共渠道、数据库启动保护、目录索引增量、搜索优化/生命周期/可见性、文件标签、首页工作区和 Copilot 索引搜索。日志：`/private/tmp/fkfinder-independent-website-tests.log`，临时目录 `/private/tmp/fkfinder-test.4B6662`。
- Store Release 公共适配测试：**Total 17，Errors 0，Failed 0，Skipped 0，Not Run 0**，3.400 秒。日志：`/private/tmp/fkfinder-independent-store-release-tests.log`，临时目录 `/private/tmp/fkfinder-test.0Gwcug`。该测试运行器用于路径隔离及服务行为验证，不能当成真实 App Sandbox 运行。
- 两渠道 arm64 Release 构建均为 **0 错误**，使用 `-p:SkipMacOSReleaseDMG=true`。官网主项目构建仍有 34 条警告，Store 测试项目及依赖构建仍有 482 条警告。日志：`/private/tmp/fkfinder-independent-{website,store}-release-build.log`。
- 两渠道新 Release `.app` 均通过 `verify-bundle.py` 及 `codesign --verify --deep --strict`；各有 24 个实际原生文件，架构、部署下限、原生代码所在位置及 Store 主/辅助进程权限核对无错误。报告：`/private/tmp/fkfinder-independent-{website,store}-release-audit.json`。签名仍为本地 ad-hoc，Store ID `com.macexplorer.app.store.qa` 仅作 QA。
- PKG 拒绝路径退出码 1，未创建 `/private/tmp/fkfinder-independent-submit.pkg`。日志：`/private/tmp/fkfinder-independent-pkg-refusal.log`。未向 Apple 发送校验或上传请求。
- `git diff --check` 通过。

最终 Release app：

```text
/private/tmp/fkfinder-independent-website/bin/MacExplorer/release_osx-arm64/Mac Explorer.app
/private/tmp/fkfinder-independent-store/bin/MacExplorer/release_osx-arm64/Mac Explorer.app
```

执行会话原有的完整回归 **1201 项、0 失败、2 跳过、1 未执行**及补充测试记录已核对，发生在上述独立补修之前，不能称为补修后重跑的完整回归。其真实沙盒测试日志 `/private/tmp/fkfinder-sandbox-native-pipeline.log` 显示 1 项通过，临时输出中有 TXT→DOCX→PDF、PDF 提取及 OCR 4826 的对应验证；原生 UI 操作记录和 `/private/tmp/fkfinder-test.Fja0uM` 中的源文件/空目标目录与复制、重命名、废纸篓验收记录相符。本轮核对这些原有证据，没有再次运行原生 UI 或真实沙盒流水线。原生帮助程序与 UI 代码没有在本轮补修中变更。

## 尚未完成

- Apple 开发者账号、正式 Bundle ID/Team、分发证书、profile，以及正式签名 PKG 的成功流程和 App Store Connect 校验。
- 已发布的隐私政策 URL、开发者联系信息、第三方 AI 处理/保留说明及隐私标签；现有照片 `CLGeocoder` 会把 GPS 发给 Apple，其独立许可/控制尚待产品决定。详见 [数据流记录](../privacy-data-flow.md)。
- 既有依赖安全告警：DotNetZip、SSH.NET、传递 System.Drawing.Common。需要修复并做兼容回归；本轮未把构建通过当成告警已解决。
- 正式身份下的 Keychain 迁移、真实 SFTP、跨网 LocalSend、实际离线/移盘设备，以及内置转换菜单点击的原生验收。
- Intel 运行测试按用户要求不执行，本轮不对该项作完成声明。

因此可以验收本次主要实现及已列明的本机验证，但不能声明软件已经可直接上架。后续跟进在交付本结论后暂停，避免重复检查。
