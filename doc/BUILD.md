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

SVG 源文件：`Assets/appicon.svg`（全出血 456×456 设计，macOS 自动施加 squircle 圆角蒙版）

```bash
# 浏览器渲染 SVG → 10 个 PNG（16~1024px，含 @2x）
# → iconutil 打包为 .icns → 复制到 Assets/
```

笔记：
- 不要在 SVG 内预制圆角——macOS 系统层面自动圆角化
- 不要在运行时调用 `setApplicationIconImage:`——会绕过系统圆角蒙版

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

SVG source: `Assets/appicon.svg` (full-bleed 456×456 design — macOS applies the squircle mask automatically).

```bash
# Browser renders SVG → 10 PNGs (16–1024px, @2x variants)
# → iconutil packages into .icns → copied to Assets/
```

Notes:
- Do **not** pre-round corners in the SVG — macOS applies the system squircle mask
- Do **not** call `setApplicationIconImage:` at runtime — it bypasses the system mask

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
