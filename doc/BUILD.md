# Build & Packaging Guide

<details open>
<summary><b>中文</b></summary>

## 环境要求

- .NET 10 SDK
- Xcode（含 Command Line Tools）
- macOS 14.0+

## Debug 开发构建

```bash
dotnet restore
dotnet build -c Debug
open "bin/Debug/net10.0/osx-arm64/Mac Explorer.app"
```

## Release 自包含打包

自包含发布（Self-Contained），.NET 运行时随 .app 打包，用户无需安装任何依赖。

```bash
dotnet publish -c Release
```

构建产物：

| 文件 | 路径 |
|------|------|
| .app 包 | `bin/Release/net10.0/osx-arm64/Mac Explorer.app` |
| .dmg 镜像 | `bin/Release/net10.0/osx-arm64/MacExplorer-{Version}-macos.dmg` |

关键配置（`MacExplorer.csproj`）：

```xml
<RuntimeIdentifier>osx-arm64</RuntimeIdentifier>
<SelfContained>true</SelfContained>
```

## DMG 制作流程

构建脚本自动完成：

1. `dotnet publish` → 生成 .app bundle
2. 将 .app 拷贝到 staging 目录
3. 创建 `/Applications` 快捷方式
4. `hdiutil create` 打包为 UDZO 压缩 DMG
5. 自动清理 staging

## 代码签名

### 临时签名（本地开发和测试）

本地构建默认使用 ad-hoc 签名。macOS 用本次构建的签名哈希识别这种应用，重新构建后可能再次询问隐私权限。

### Developer ID 签名 + 公证（正式分发）

官网 GitHub Release 使用 Apple Developer ID Application 证书，团队 ID 为 `AXBYDPHN88`，Bundle ID 为 `com.thankful.top.macexplorer`。签名身份规则保存在 `Tools/Distribution/website-signing.requirement`，发布流程和应用内更新共用。旧版首次换签时可能再次请求已有隐私权限。之后同一签名身份的版本可继承授权。

在 GitHub Actions 配置以下仓库密钥后，正常发布和手动重建会检查签名、公证和票据。仅测试的运行无需密钥。

- `WEBSITE_DEVELOPER_ID_P12_BASE64`、`WEBSITE_DEVELOPER_ID_P12_PASSWORD` 为含私钥的 Developer ID Application PKCS#12 文件的 Base64 内容和密码。
- `WEBSITE_NOTARY_KEY_BASE64`、`WEBSITE_NOTARY_KEY_ID`、`WEBSITE_NOTARY_ISSUER_ID` 为同一团队的 App Store Connect 团队 API 私钥 `.p8` 的 Base64 内容、Key ID 和 Issuer ID。

也可以用钥匙串中已有的 Developer ID 证书和 `notarytool store-credentials` 保存的凭据配置在本机打包。先生成两个架构的 Release `.app`（传入 `-p:SkipMacOSReleaseDMG=true`），再运行下列命令。填写实际构建版本，例如 `2.0.13`。

```bash
WEBSITE_SIGNING_IDENTITY='Developer ID Application: XinJi Jiang (AXBYDPHN88)' \
WEBSITE_NOTARY_PROFILE='MacExplorer' \
bash Tools/Distribution/package-website-release.sh 2.0.13 ./dist \
  'bin/Release/net10.0/osx-arm64/Mac Explorer.app' \
  'bin/Release/net10.0/osx-x64/Mac Explorer.app'
```

脚本逐个签名原生文件和应用包，将 DMG 送交 Apple 公证，再为 DMG 及 ZIP 内的应用装订票据。主程序启用 Hardened Runtime 和库签名验证。第三方插件通过独立工作进程加载原生依赖。发版前可用独立 macOS 用户或虚拟机检查首次换签和后续升级，分别对照「文件与文件夹」「照片」或实际遇到的系统权限。

## 图标生成

SVG 源文件：`Assets/appicon.svg`（1024×1024 透明画布，居中的 824×824 圆角图案，四边各留白 100px）。圆角和透明留白必须保留在导出的图标中，不能依赖系统自动裁切。

```bash
# 使用支持 SVG luminance mask 的渲染器（如浏览器或 resvg）导出透明 PNG
# → 生成 16、32、128、256、512 的 @1x / @2x PNG，按 Apple iconset 命名
# → iconutil -c icns appicon.iconset -o Assets/appicon.icns
# → 同步生成 Assets/appicon.ico（16、24、32、48、64、128、256px）
# → 将 SVG 同步到 docs/Assets/appicon.svg
```

笔记：
- 背景轮廓使用 Apple 连续圆角曲线，源自 `RoundedRectangle(cornerRadius: 102, style: .continuous)` 在 456×456 区域生成的路径；不要替换为普通 `rect rx`，以免直边与圆角衔接生硬。曲线已固化为 SVG，不增加运行时依赖。
- 传统 `.icns` 在旧版 macOS 的 Finder、Dock、Launchpad 中可能直接按素材显示；新版系统的自动蒙版不能作为兼容性保证。
- 每个尺寸都必须保留透明通道，不能添加不透明底色；应检查四角透明，并分别预览浅色和深色背景。
- `.icns` 用于应用包图标，`.ico` 也用于设置页；两个文件应来自同一份 SVG。
- 替换已安装应用后，如果仍显示旧图标，先退出并重新打开应用；Finder、Dock、Launchpad 的图标缓存可能需要注销后重新登录才更新。

## App Store 上架

商店渠道使用独立沙盒构建和正式签名的 PKG。构建参数、证书、profile 与 `prepare-store-pkg.sh` 用法见 [双渠道构建与发布](../docs/distribution/mac-app-store.md)。

官网 DMG 不用于商店提交。打包脚本只创建并检查本地安装包，不上传或发布。

</details>

<details>
<summary><b>English</b></summary>

## Requirements

- .NET 10 SDK
- Xcode (with Command Line Tools)
- macOS 14.0+

## Debug Build

```bash
dotnet restore
dotnet build -c Debug
open "bin/Debug/net10.0/osx-arm64/Mac Explorer.app"
```

## Release Self-Contained Build

The app is published as self-contained — the .NET runtime is bundled inside the `.app`, users need no dependencies.

```bash
dotnet publish -c Release
```

Build outputs:

| File | Path |
|------|------|
| .app bundle | `bin/Release/net10.0/osx-arm64/Mac Explorer.app` |
| .dmg disk image | `bin/Release/net10.0/osx-arm64/MacExplorer-{Version}-macos.dmg` |

Key settings (`MacExplorer.csproj`):

```xml
<RuntimeIdentifier>osx-arm64</RuntimeIdentifier>
<SelfContained>true</SelfContained>
```

## DMG Creation

The build script automates:

1. `dotnet publish` → produces the .app bundle
2. Copies .app to a staging directory
3. Creates an `/Applications` symlink
4. `hdiutil create` packages everything into a UDZO-compressed DMG
5. Cleans up staging

## Code Signing

### Ad-hoc signing (local development and testing)

Local builds use ad-hoc signing by default. macOS identifies each build by its signature hash and may ask again for access to privacy-protected resources after a rebuild.

### Developer ID Signing + Notarization (public distribution)

GitHub website releases use a Developer ID Application certificate for team `AXBYDPHN88` with bundle ID `com.thankful.top.macexplorer`. The release workflow and in-app updater share the identity requirement in `Tools/Distribution/website-signing.requirement`. The first upgrade from an ad-hoc build may ask for existing privacy permissions again. Later builds with the same signing identity can retain those permissions.

Set these GitHub Actions repository secrets before publishing or rebuilding a release. Test-only runs do not require them.

- `WEBSITE_DEVELOPER_ID_P12_BASE64` and `WEBSITE_DEVELOPER_ID_P12_PASSWORD` contain the Base64-encoded Developer ID Application PKCS#12 file with its private key and the file’s password.
- `WEBSITE_NOTARY_KEY_BASE64`, `WEBSITE_NOTARY_KEY_ID`, and `WEBSITE_NOTARY_ISSUER_ID` contain the same team’s Base64-encoded App Store Connect Team API `.p8` key, Key ID, and Issuer ID.

To package locally, build Release apps for both architectures with `-p:SkipMacOSReleaseDMG=true`. Use the installed Developer ID identity and a Keychain profile created with `notarytool store-credentials`. Pass your build version, such as `2.0.13`.

```bash
WEBSITE_SIGNING_IDENTITY='Developer ID Application: XinJi Jiang (AXBYDPHN88)' \
WEBSITE_NOTARY_PROFILE='MacExplorer' \
bash Tools/Distribution/package-website-release.sh 2.0.13 ./dist \
  'bin/Release/net10.0/osx-arm64/Mac Explorer.app' \
  'bin/Release/net10.0/osx-x64/Mac Explorer.app'
```

The script signs native code and the app, submits the DMG to Apple for notarization, and staples the tickets to the DMG and the app inside the ZIP. The main app uses Hardened Runtime with library validation. Third-party plug-ins load their native dependencies in a separate worker executable. Use a dedicated macOS account or VM to check permission behavior during the identity transition and a later same-identity upgrade.

## Icon Generation

SVG source: `Assets/appicon.svg` (1024×1024 transparent canvas with centered 824×824 rounded artwork and 100px margins). Preserve the rounded corners and transparency in exported icons instead of relying on system masking.

```bash
# Render transparent PNGs with SVG luminance mask support (e.g. a browser or resvg)
# → Generate 16, 32, 128, 256, 512 at @1x / @2x using Apple iconset filenames
# → iconutil -c icns appicon.iconset -o Assets/appicon.icns
# → Also generate Assets/appicon.ico (16, 24, 32, 48, 64, 128, 256px)
# → Copy the SVG to docs/Assets/appicon.svg
```

Notes:
- The background uses Apple's continuous corner path, exported from `RoundedRectangle(cornerRadius: 102, style: .continuous)` in a 456×456 rectangle. Keep this path instead of a plain `rect rx` so curvature transitions smoothly from the straight edges. It is baked into the SVG and adds no runtime dependency.
- Legacy `.icns` resources can appear exactly as supplied in Finder, Dock, and Launchpad on older macOS versions; automatic masking on newer systems is not a compatibility guarantee.
- Keep the alpha channel at every size without adding an opaque background. Check transparent corners and preview on both light and dark backgrounds.
- The app bundle uses `.icns`, while the settings page also uses `.ico`; generate both from the same SVG.
- If an installed app still shows its old icon after replacement, quit and reopen it first. Finder, Dock, and Launchpad caches may require logging out and back in to refresh.

## App Store Distribution

The Store channel uses a separate sandbox build and a distribution-signed PKG. See [dual-channel build and distribution](../docs/distribution/mac-app-store.md) for build properties, certificates, provisioning profiles and `prepare-store-pkg.sh`.

The website DMG is not a Store submission package. The packaging script only creates and checks the local PKG; it does not upload or publish.

</details>
