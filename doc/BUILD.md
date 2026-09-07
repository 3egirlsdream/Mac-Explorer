# Build & Packaging Guide

<details open>
<summary><b>中文</b></summary>

## 环境要求

- .NET 10 SDK
- Xcode（含 Command Line Tools）
- macOS 15.0+

## Debug 开发构建

```bash
dotnet restore
dotnet build -c Debug
open "bin/Debug/net10.0/Mac Explorer.app"
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

### 自签名（开发/测试分发）

构建脚本默认使用 ad-hoc 签名，防止"app 已损坏"提示：

```bash
codesign --force --deep --sign - "Mac Explorer.app"
```

用户首次打开需**右键 → 打开**信任一次。

### Developer ID 签名 + 公证（正式分发）

需要 Apple Developer 账号（$99/年）。

```bash
# 签名
codesign --force --deep --sign "Developer ID Application: Your Name (TEAM_ID)" \
  --options runtime "Mac Explorer.app"

# 打包 DMG
hdiutil create -volname "Mac Explorer" -srcfolder dmg-staging -ov \
  -format UDZO -imagekey zlib-level=9 MacExplorer-x.x.x-macos.dmg

# DMG 签名
codesign --sign "Developer ID Application: Your Name (TEAM_ID)" \
  MacExplorer-x.x.x-macos.dmg

# 公证
xcrun notarytool submit MacExplorer-x.x.x-macos.dmg \
  --apple-id "your-apple-id@example.com" \
  --password "app-specific-password" \
  --team-id "TEAM_ID" \
  --wait

# 装订票据
xcrun stapler staple MacExplorer-x.x.x-macos.dmg
```

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

```bash
# 1. 构建
dotnet publish -c Release

# 2. 上传到 App Store Connect
xcrun altool --upload-app \
  -f bin/Release/net10.0/osx-arm64/MacExplorer-{Version}-macos.dmg \
  -t macos \
  -u "your-apple-id@example.com" \
  -p "app-specific-password"
```

前置条件：Apple Developer Portal 创建 App ID + 分发证书 + Provisioning Profile + App Store Connect 创建记录。

</details>

<details>
<summary><b>English</b></summary>

## Requirements

- .NET 10 SDK
- Xcode (with Command Line Tools)
- macOS 15.0+

## Debug Build

```bash
dotnet restore
dotnet build -c Debug
open "bin/Debug/net10.0/Mac Explorer.app"
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

### Ad-Hoc Signing (dev/test distribution)

The build script defaults to ad-hoc signing, which prevents "app is damaged" errors:

```bash
codesign --force --deep --sign - "Mac Explorer.app"
```

Users must **Right-click → Open** once to trust the app.

### Developer ID Signing + Notarization (public distribution)

Requires an Apple Developer account ($99/year).

```bash
# Sign
codesign --force --deep --sign "Developer ID Application: Your Name (TEAM_ID)" \
  --options runtime "Mac Explorer.app"

# Package DMG
hdiutil create -volname "Mac Explorer" -srcfolder dmg-staging -ov \
  -format UDZO -imagekey zlib-level=9 MacExplorer-x.x.x-macos.dmg

# Sign DMG
codesign --sign "Developer ID Application: Your Name (TEAM_ID)" \
  MacExplorer-x.x.x-macos.dmg

# Notarize
xcrun notarytool submit MacExplorer-x.x.x-macos.dmg \
  --apple-id "your-apple-id@example.com" \
  --password "app-specific-password" \
  --team-id "TEAM_ID" \
  --wait

# Staple ticket
xcrun stapler staple MacExplorer-x.x.x-macos.dmg
```

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

```bash
# 1. Build
dotnet publish -c Release

# 2. Upload to App Store Connect
xcrun altool --upload-app \
  -f bin/Release/net10.0/osx-arm64/MacExplorer-{Version}-macos.dmg \
  -t macos \
  -u "your-apple-id@example.com" \
  -p "app-specific-password"
```

Prerequisites: Apple Developer Portal — App ID, Distribution Certificate, Provisioning Profile, and an App Store Connect record.

</details>
