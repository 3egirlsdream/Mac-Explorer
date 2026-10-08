# 双渠道构建与发布

官网渠道为 `Website`，商店渠道为 `AppStore`，共用 .NET 10 / Avalonia 代码。最低系统版本由 `Directory.Build.props` 统一设置为 macOS 14.0，默认架构为 `osx-arm64`。

## 渠道差异

| 范围 | Website | AppStore |
| --- | --- | --- |
| 文件访问 | 按系统文件权限访问 | 系统选择器授权，保存安全作用域书签；设置中可撤销 |
| 数据与缓存 | Application Support / Caches，迁移旧文稿数据库 | 独立应用容器，不自动读取官网数据 |
| 搜索 | 原搜索范围 | 仅搜索已授权目录及应用内部目录 |
| Git 状态 | 使用系统 Git | 检测可执行的已安装 Git，成功时启用；缺失或沙盒拒绝时安静降级 |
| 图片回退与人脸裁剪 | 原系统图片处理 | 使用随包签名 helper 的 ImageIO，保留 EXIF 方向及原有 Skia 图片兜底 |
| 磁盘弹出 | 原 diskutil 流程 | 公开 NSWorkspace API，系统拒绝时返回失败，不请求提权 |
| 更新与外部执行 | 自更新、插件市场、外部插件、脚本及 Terminal 入口 | 禁用；保留固定内置转换和签名帮助程序 |
| AI、照片地点、SFTP | 接收方确认、地点解析许可、钥匙串和主机信任 | 使用相同控制 |
| 安装包 | DMG | 正式签名的 PKG |

## 本地构建

两渠道使用独立输出目录，避免复用另一渠道的编译结果。下列 Store 标识仅用于 QA，不能用于正式提交。

```sh
dotnet build MacExplorer.csproj -c Debug -r osx-arm64 \
  --artifacts-path /private/tmp/fkfinder-website-build \
  -p:DistributionChannel=Website -p:SkipMacOSReleaseDMG=true

dotnet build MacExplorer.csproj -c Debug -r osx-arm64 \
  --artifacts-path /private/tmp/fkfinder-store-build \
  -p:DistributionChannel=AppStore \
  -p:AppStoreBundleId=com.thankful.top.macexplorer.store.qa \
  -p:SkipMacOSReleaseDMG=true
```

官网 Release 构建默认生成 DMG；Store 不生成 DMG。托管程序集位于 `Contents/Resources/Managed`，原生库位于 `Contents/Frameworks`，原生帮助程序位于 `Contents/MacOS`。`sign-app.sh` 逐个签名原生文件，最后签名并校验主包。

包内分类为工具（`public.app-category.utilities`），版权沿用官网的 `© 2026 Mac Explorer`。`MacOSBuildNumber` 独立控制 `CFBundleVersion`，默认兼容现有版本号；每次实际上传需传入更大的构建号，例如 `-p:MacOSBuildNumber=51`。商店构建在签名前递归清除 quarantine，包审计检查分类、版权、构建号和 quarantine。

目录授权根的规范路径在恢复或重新授权时计算；每个访问目标仍实时解析符号链接，不缓存文件访问的允许结果。helper 对已知输入／输出只传所需授权，并复用对应的隐式书签；撤销、恢复和重新授权会替换或移除缓存。没有显式路径的既有内置转换仍传递当前授权集合，保持转换兼容。

商店 Git 检测在后台按需进行，每次应用运行缓存一次检测结果。探测 Command Line Tools、默认 Xcode 和 Homebrew 常见位置的真实 Git，通过签名的 `MacExplorer.Git` helper 验证版本并读取状态；不调用 `/usr/bin/git` 转发入口，不要求用户安装开发工具。新安装 Git 后重启应用重新检测。helper 先接收仓库书签，再以 `exec` 运行 Git，保留沙盒限制；只允许版本、状态、忽略、未跟踪文件和外部 filter 配置查询，并关闭 fsmonitor/hooks、可选索引锁写入及继承的 Git 环境覆盖。存在非空 clean/process filter 或 partial clone 配置时暂不显示仓库状态，避免后台查询执行外部程序、按需拉取远端对象或误报过滤后的文件状态；不递归查询子模块工作区内部改动，子模块提交变化仍由主仓库查询。仓库与 `.git` 必须仍处于授权范围，读取失败返回不可用，不缓存成“干净仓库”。非标准安装位置目前不自动扫描。

## 商店安装包

官网 Bundle ID 为 `com.thankful.top.macexplorer`，商店正式 Bundle ID 为 `com.thankful.top.macexplorer.store`，已配置于 `Tools/Distribution/release-config.json` 与项目默认值。测试构建通过 `AppStoreBundleId` 覆盖为 `.store.qa` 标识；正式构建使用默认标识。正式 Release 构建还需要 `MacOSSigningIdentity`、`MacOSTeamIdentifier` 和 `AppStoreProvisioningProfile`；设置 `StoreSubmission=true` 可在构建前检查正式标识与公开隐私政策。

```sh
python3 Tools/Distribution/verify-bundle.py '/absolute/path/Mac Explorer.app' AppStore
bash Tools/Distribution/prepare-store-pkg.sh \
  '/absolute/path/Mac Explorer.app' '/absolute/path/MacExplorer.pkg' \
  'Mac Installer Distribution: Your Name (TEAM_ID)'
```

打包脚本检查商店分发签名、profile 标识、Team、证书、有效期和分发类型，并拒绝覆盖已有输出。创建 PKG 不会上传或发布应用。

## 资源与验证

正式政策位于 [官网页面](https://3egirlsdream.github.io/Mac-Explorer/privacy/)，本地副本为 `Assets/PrivacyPolicy.txt`；未配置政策 URL 时，应用读取该正式副本。数据流见 [privacy-data-flow.md](privacy-data-flow.md)。

第三方声明通过 `ThirdParty/Notices/dependencies.json` 校验，内容相同的许可证共用一个文件，项目许可证直接复制根目录 `LICENSE`。LibRaw 使用 arm64/x64 预编译库，保留许可证及 `SOURCE.md` 中的源码下载地址和哈希，不编译或打包源码归档。

`.github/workflows/distribution-qa.yml` 检查 arm64 两渠道构建、定向回归、包布局和声明。需要默认服务或真实应用的测试必须通过 `Tools/Testing/run-isolated.sh`；原生 UI 直接启动新构建的 `.app/Contents/MacOS/MacExplorer`。构建通过不能替代真实沙盒、系统授权及原生交互验收。

历史结果保留于 [初始实现](archive/mac-app-store.md)、[渠道复核](archive/mac-app-store-review.md)、[隐私复核](archive/privacy-security-review.md)及 [可靠性复核](archive/reliability-release-review.md)。正式商店签名、账号/profile 和成功 PKG 流程仍需具备相应资源后验收；Intel 运行测试按用户要求不执行。

正式提交前还需填写 App Privacy 标签、截图、年龄分级、支持信息、审核操作说明和加密出口合规资料。真实系统 Keychain、局域网拒绝／恢复、实际 SFTP 服务、外置磁盘弹出及生产签名 Sandbox 必须单独验收。付费解锁／订阅只有采用该商业模式时才接入 StoreKit；当前不新增收费机制。

## 2026-10-01 适配验证

arm64 两渠道 Release 构建通过，各 75 项定向回归通过（0 失败、0 跳过），覆盖目录授权、撤销与符号链接边界、可靠性恢复、图片 EXIF 方向／人脸裁剪、缩略图缓存与并发、隐私和主机信任。两渠道包布局与第三方声明审计通过；向临时 Store 包添加 quarantine 后，审计正确拒绝。

新构建 Store QA 应用通过隔离启动和测试目录选择，13 项 JPG／DNG 图片显示正常，万项目录加载、图标滚动和列表切换可用。该检查使用 QA 标识和临时签名，不代表生产签名 Sandbox 或 App Store 审核通过；原生图片 helper 测试也不替代正式沙盒验收。

授权回归确认 100 次访问只解析 100 个目标，重复 10 次 helper 启动只创建一次对应隐式书签，撤销后拒绝继续使用。每个目标仍实时校验，缓存和并发门控保持不变。本次未测量两渠道的端到端性能差值，不能据此承诺固定的性能损耗比例。构建仍有既有警告，完整套件和生产环境验收未在本次重跑。

随后按用户要求将 Git 从固定关闭改为检测可用性。两渠道各 78 项定向回归通过，包含 Git 候选探测、真实仓库的修改／未跟踪／忽略状态、拒绝访问已缓存的未授权仓库、失败读取后的重试，以及阻止外部 filter 和 partial clone 查询；Store 查询没有运行 fsmonitor hook，也没有更新索引。新 Store QA 原生实例在用户选择的临时测试目录中显示 `M` 和 `?` 标记。未安装 Git／候选执行失败通过替身测试覆盖，本次没有卸载系统 Git，也没有完成生产签名或全部安装布局的实机验收。
