# 双渠道实现与验收记录（2026-09-30）

> 历史执行与验收记录；当前构建和发布说明见 [双渠道说明](../mac-app-store.md)。

本次在 `main` / `6baa8b1ce775fa9616a2a9938825abaa8541ec48` 基础上实现。版本保持 **1.0.50**，未提交、推送、上传或发布。Store 测试身份 `com.macexplorer.app.store.qa` 仅用于隔离验证；正式 Bundle ID、Team、签名及 provisioning profile 由构建参数指定。

执行结束后的补修和最终独立验收见 [mac-app-store-review.md](mac-app-store-review.md)。下列测试数字保留执行会话原有记录；补修后的验证范围与剩余项以独立验收记录为准。

## 实现边界

| 范围 | 公共实现 | 官网渠道 | Store 渠道 |
| --- | --- | --- | --- |
| 导航与授权 | `DirectoryAccess` 统一持有原生 scope；系统 picker 保存 bookmark；支持失效、移动别名、离线重试、撤销与损坏记录备份 | 直接访问保留 | 仅授予位置及应用内部目录；授权列表在通用设置；服务再次检查路径 |
| 文件与索引 | 导航、收藏、首页、页签、索引、搜索、内容读取、预览、标签、文件操作及 SFTP 本地端、LocalSend 接收复用访问检查 | 保留原搜索范围 | “这台 Mac”聚合授权根；不绕过沙盒；规范化现有和未创建路径，防止符号链接逃逸 |
| 数据目录 | Application Support / Caches / Logs；用户导航 home 从账户信息取，应用容器从系统 API 取 | 迁移旧 `~/Documents/MacExplorer/index.db` | 系统容器内独立数据，不擅自访问官网数据 |
| 数据迁移 | SQLite Backup + integrity check + 唯一暂存文件 + 非覆盖发布；包含已提交 WAL，保留旧库与已存在的新库 | 启动时执行；失败不静默重建空库 | 无跨渠道自动导入 |
| 删除与打开 | 本地删除用 `NSFileManager.trashItemAtURL`；公开 NSWorkspace、Foundation plist、MDItem、图标接口 | Finder 全局废纸篓原功能保留 | 全局废纸篓浏览/清空在 UI 和服务均禁止；打开、定位和 Quick Look 使用公开原生接口 |
| 转换/OCR/PDF/缩略图/RAW | 复用现有转换库、Swift 原生帮助程序及 LibRaw；帮助程序随包签名，保留进度、取消和输出提交 | 原插件体系保留 | 固定内置转换在进程内；签名帮助程序继承沙盒并接收隐式 bookmark；不下载执行新代码 |
| AI 隐私 | 首次发送说明元数据及接收 Endpoint / Model；正文继续逐次审批；HTTP 边界重新检查 | 相同 | 相同；配置或 key 改变后原许可失效 |
| SFTP 凭据 | SecItem Keychain；旧 JSON 密码成功存入后才去除，失败保留连接供重试；私钥来自授权 picker | 相同 | 相同；正式身份的 Keychain 迁移仍需设备验收 |
| LocalSend | 双栈监听、IPv6 地址处理；v1/v2 协议及 IPv4 发现保留；证书迁移不覆盖新证书 | 默认接收到下载目录 | 默认接收到容器内 Received；用户选择目录后才能写入 |
| 系统集成 | 统一渠道开关，功能入口与执行边界对应 | 更新、外部插件、市场、默认管理器、Terminal、首页脚本保留 | 禁止这些能力；Copilot 不暴露脚本/外部插件能力；内置转换保留 |

Store 启动器先初始化 AppKit，再通过与 [.NET standalone apphost 相同的公开启动入口](https://github.com/dotnet/runtime/blob/v10.0.5/src/native/corehost/apphost/standalone/hostfxr_resolver.cpp) `hostfxr_main_startupinfo` 启动随包的运行时。该顺序修复真实沙盒中 CoreCLR 启动前 LaunchServices 注册的 SIGABRT，未添加临时 sandbox exception。保持自包含 JIT；没有强制 AOT。

## 构建与打包

两种架构的最低版本统一为 macOS **14.0**，与 [.NET 10 支持范围](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)一致。原生 Mach-O 放在 `Contents/MacOS` / `Frameworks`；托管 DLL、JSON、技能与配置放在 `Resources/Managed`，原生库通过包内相对链接解析。

```sh
# 官网；将 RID 替换为 osx-x64 得到 Intel 包
 dotnet build MacExplorer.csproj -c Debug -r osx-arm64 \
   --artifacts-path /private/tmp/fkfinder-website-build -p:SkipMacOSReleaseDMG=true

# Store；QA ID 不用于正式提交
 dotnet build MacExplorer.csproj -c Debug -r osx-arm64 \
   --artifacts-path /private/tmp/fkfinder-store-build -p:SkipMacOSReleaseDMG=true \
   -p:DistributionChannel=AppStore -p:AppStoreBundleId=com.example.product.store

python3 Tools/Distribution/verify-bundle.py '/absolute/path/Mac Explorer.app' AppStore
```

正式构建再设置 `MacOSSigningIdentity`、`MacOSTeamIdentifier` 和 `AppStoreProvisioningProfile`。`sign-app.sh` 从内部原生文件逐个签名，最后签主包并 strict 校验。Store 主程序只有 sandbox、用户所选读写、app-scope bookmark、网络客户端/服务端和 allow-jit 权限；帮助程序只有 sandbox + inherit。没有临时例外、关闭库验证或 unsigned-executable-memory 权限。

`prepare-store-pkg.sh <已正式签名的.app> <新输出.pkg> <Mac Installer Distribution身份>` 在 productbuild 前校验 profile 的标识、Team、有效期及商店发布类型，并拒绝 ad-hoc、Developer ID 和已存在输出。已验证 ad-hoc 被拒绝，未生成冒充正式提交的 PKG。没有 App Store Connect 校验、上传、公证或发布。官网 Release 的 DMG 流程保留，Store 不生成 DMG。当前分别产出两种 RID；未提供未经验证的 Universal 合并脚本。

## 已执行验证

- 官网完整回归：`Total 1201, Errors 0, Failed 0, Skipped 2, Not Run 1`，268.808 秒。日志 `/private/tmp/fkfinder-website-regression-latest.log`。真实相机 RAW 动态测试需要 `FKFINDER_REAL_RAW_SAMPLE`，本次无样本；生成 DNG 的回归已执行。另一次跳过为只在真实 Store 沙盒运行的原生管线测试，该测试已在独立签名沙盒宿主中通过。
- 最后两处路径检查后的官网针对性回归：35 项全部通过，覆盖公共适配、默认路径及文件移动/重命名/完成状态；2.001 秒，日志 `/private/tmp/fkfinder-website-targeted-final.log`。
- Store 公共适配：13 项全部通过，覆盖 WAL 迁移、新库保护、损坏恢复、scope 生命周期、移动/重启/离线/撤销、现有与新路径规范化、符号链接逃逸、SFTP 凭据迁移、AI 许可、固定转换、IPv4/IPv6 HTTPS 实际文件回传及旧协议、服务禁止项。日志 `/private/tmp/fkfinder-store-common-final.log`（最终构建，1.902 秒）。常规测试进程的 fixture scope 使用替身；不能将其当成原生 App Sandbox 证据。
- 原生 Store arm64 应用：经 `run-isolated.sh` 启动新 `.app/Contents/MacOS/MacExplorer`，系统 NSOpenPanel 仅授予临时目录。已验证启动、导航、授权设置、浅/深色、真实复制、公开原生移到废纸篓、生成 DNG 缩略图/内容预览及公开 Quick Look 面板。早期测试目录 `/private/tmp/fkfinder-test.ONhazD`。复制走查发现 `/private/tmp` 与 `/tmp` 的现有/新路径差异，已修复并加入原生路径回归。
- 最终干净构建的原生复核：`/private/tmp/fkfinder-test.Fja0uM`，真实复制单文件到新目标、重命名为 `renamed.txt`、确认移到废纸篓，目标目录为空且所有源文件保留；设置内移除授权后重新通过 NSOpenPanel 授予同一根目录成功。浅/深色及 780px 最小宽度下，授权路径、移除和选择按钮完整可见。日志 `/private/tmp/fkfinder-store-ui-final.log`。关闭后自动化观察意外重新启动了 QA 身份的测试应用，立即终止该进程；此次非隔离重启不计为验收证据。
- 菜单诊断确认右键事件和 `ContextMenu.IsOpen` 正常；当前自动化接口无法读取该独立 native popup，未宣称完成菜单内转换点击验收。临时诊断代码已移除。
- 四份官网/Store × arm64/x64 包：构建零错误；严格签名、24 个原生文件的架构与 deployment target、包布局和 Store helper entitlements 已检查。对应 `/private/tmp/fkfinder-final-{website,store}-{arm,intel}-audit.json`。最终输出位于 `/private/tmp/fkfinder-clean-{website,store}-{arm,intel}/bin/MacExplorer/debug_osx-{arm64,x64}/Mac Explorer.app`，对应记录以这些文件为准。arm64 测试项目构建各 482 个警告，x64 主项目构建各 34 个警告，均为 0 错误。
- 用户已明确不做 Intel 测试，最终验收以 arm64 为准。此前 x64 构建/包检查仅保留为产物记录，没有 Intel 运行验收。没有修改或复用真实用户的数据库、收藏、桌面或文稿；隔离临时目录与日志保留供复核。

所有默认路径测试和原生 QA 均使用 `bash Tools/Testing/run-isolated.sh <可执行文件> [参数]`。当前新增的 `StoreSandboxNativePipelineTests` 仅在签名 Store 沙盒并授予隔离根时执行，普通回归会明确跳过；真实沙盒执行已通过：1 项，零失败/跳过，14.533 秒；临时 QA 测试宿主使用相同 Store entitlements、公开 apphost 启动入口及随包签名帮助程序，经 NSOpenPanel 授予 `/private/tmp/fkfinder-test.P7VwFE`，实际完成 TXT→DOCX→PDF、PDF 文字提取和 PNG OCR，断言识别到 4826 且源文件不变。日志 `/private/tmp/fkfinder-sandbox-native-pipeline.log`；输出保留在该目录 `native-pipeline`。QA 测试宿主仅用于测试，不属于提交包。

## 许可与正式提交剩余项

移除了 Apple GenericFolderIcon 的构建复制和运行时资源回退，文件夹回退使用已有 Fluent SVG。Fluent 图标按 [上游 MIT 许可](https://github.com/microsoft/fluentui-system-icons/blob/main/LICENSE)使用，既有 Markdown 图标 notice 保留。

LibRaw 的实际 arm64/x64 二进制为 0.22.1。本次明确选择上游 CDDL 1.0 选项，保留原双许可文本及 COPYRIGHT，随两渠道包携带 `SOURCE.md`，提供对应上游源码的下载地址及 SHA-256；源码压缩包不进入 Git，也不随应用打包，没有修改覆盖源代码。源档案 SHA-256 为 `a789dc4e2409e2901d93793a4e0b80c7b49d0d97cf6ad71c850eb7616acfd786`，二进制构建参数/原哈希在 `ThirdParty/LibRaw/README.md`。详见 [上游源码](https://www.libraw.org/data/LibRaw-0.22.1.tar.gz)及 [0.22.1 release](https://github.com/LibRaw/LibRaw/releases/tag/0.22.1)。发布方需持续保证对应源码可获取；上游地址失效时提供同一归档的替代下载。签名会改变包内二进制哈希。

主要 NuGet 许可元数据已核对：Avalonia、CodeWF.Markdown、OpenXML、Microsoft.Agents.AI、SharpCompress、SkiaSharp、SSH.NET、Svg.Skia、System.Drawing.Common 为 MIT，SQLitePCLRaw.core 为 Apache-2.0。DotNetZip 的 [上游 LICENSE](https://github.com/haf/DotNetZip.Semverd/blob/master/LICENSE)包含 Ms-PL 及其 BSD/Apache/zlib/MIT 衍生代码 notice，已加入两渠道资源。已有 LiquidGlass、Vex、LocalSend 和转换资源 notices 保留。此项是主要依赖审核，不替代正式包全部传递依赖的 license/notice 清单及扫描。

构建仍有既有 NuGet 安全告警：DotNetZip 1.16.0（GHSA-xhg6-9j5j-w4vf）、SSH.NET 2025.0.0（GHSA-mggc-4xg6-vcxf、GHSA-q939-rpr3-3284）、传递 System.Drawing.Common 4.7.0（GHSA-rxg9-xrhp-64gj），另有 NU1510、Avalonia 资源/弃用及 xUnit analyzer 警告。未为消除告警盲目升级而改变兼容性；正式发布前必须单独修复并回归。

[privacy-data-flow.md](../privacy-data-flow.md)记录代码的数据流。尤其原有 CLGeocoder 会将照片 GPS 发给 Apple 地理编码服务，不能声称全部分析离线；其许可/控制及正式政策仍待产品决定。尚缺已发布隐私政策 URL、联系信息、AI 接收方处理/保留说明及 App Store 隐私标签。真实 SFTP 对端、正式签名 Keychain 迁移、跨网 LocalSend 发现、离线/移盘设备验收未完成。Intel 运行测试按用户要求不执行。

上述为可审核实现及本机证据，尚未达到正式上架完成状态；商店规则以提交时 [Apple 审核指南](https://developer.apple.com/cn/app-store/review/guidelines/)及平台扫描结果为准。未加入收费、订阅或收据验证逻辑。
