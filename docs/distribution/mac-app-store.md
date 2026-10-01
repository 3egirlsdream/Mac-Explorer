# 双渠道构建与发布

官网渠道为 `Website`，商店渠道为 `AppStore`，共用 .NET 10 / Avalonia 代码。最低系统版本由 `Directory.Build.props` 统一设置为 macOS 14.0，默认架构为 `osx-arm64`。

## 渠道差异

| 范围 | Website | AppStore |
| --- | --- | --- |
| 文件访问 | 按系统文件权限访问 | 系统选择器授权，保存安全作用域书签；设置中可撤销 |
| 数据与缓存 | Application Support / Caches，迁移旧文稿数据库 | 独立应用容器，不自动读取官网数据 |
| 搜索 | 原搜索范围 | 仅搜索已授权目录及应用内部目录 |
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
  -p:AppStoreBundleId=com.macexplorer.app.store.qa \
  -p:SkipMacOSReleaseDMG=true
```

官网 Release 构建默认生成 DMG；Store 不生成 DMG。托管程序集位于 `Contents/Resources/Managed`，原生库位于 `Contents/Frameworks`，原生帮助程序位于 `Contents/MacOS`。`sign-app.sh` 逐个签名原生文件，最后签名并校验主包。

## 商店安装包

先在 `Tools/Distribution/release-config.json` 填写实际 App Store Bundle ID，构建时传入相同的 `AppStoreBundleId`。正式 Release 构建还需要 `MacOSSigningIdentity`、`MacOSTeamIdentifier` 和 `AppStoreProvisioningProfile`；设置 `StoreSubmission=true` 可在构建前检查正式标识与公开隐私政策。

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
