# FKFinder

基于 .NET MAUI Blazor 的 macOS Finder 替代应用（Mac Catalyst）。

## 环境要求

- .NET 10 SDK
- Xcode（含 Command Line Tools）
- macOS 15.0+

## 打包命令

### 一、本地安装版本（直接运行，无需上架）

适用于开发测试或直接分发给用户安装的场景。不需要 Apple 开发者账号。

```bash
# 1. Release 构建（生成 .app 和 .pkg）
dotnet publish src/FKFinder/FKFinder.csproj \
  -f net10.0-maccatalyst \
  -c Release \
  -r maccatalyst-arm64

# 构建产物位于:
# .app 包: src/FKFinder/bin/Release/net10.0-maccatalyst/maccatalyst-arm64/FKFinder.app
# .pkg 安装包: src/FKFinder/bin/Release/net10.0-maccatalyst/maccatalyst-arm64/publish/FKFinder-1.0.pkg

# 2. 如需创建 DMG 安装包（可选）
hdiutil create -volname "FKFinder" \
  -srcfolder src/FKFinder/bin/Release/net10.0-maccatalyst/maccatalyst-arm64/publish/FKFinder.app \
  -ov -format UDZO \
  FKFinder.dmg

# 3. 安装：将 .app 拖入 /Applications 目录即可
```

### 二、发布到 App Store

需要有效的 Apple 开发者账号和相应证书/描述文件。

#### 前置准备

1. 在 Apple Developer Portal 创建 App ID：`com.fkfinder.app`
2. 创建 Mac Catalyst 分发证书（Apple Distribution）和描述文件（Provisioning Profile）
3. 在 App Store Connect 创建应用记录
4. 需要开启 App Sandbox（当前 Entitlements.plist 中已禁用，上架需修改）

#### 修改 Entitlements（上架 App Store 必须启用沙箱）

将 `src/FKFinder/Platforms/MacCatalyst/Entitlements.plist` 中的沙箱设置改为 `true`，并按需添加权限：

```xml
<key>com.apple.security.app-sandbox</key>
<true/>
<key>com.apple.security.files.user-selected.read-write</key>
<true/>
```

#### 打包命令

```bash
# 1. 发布构建（生成签名的 .pkg）
dotnet publish src/FKFinder/FKFinder.csproj \
  -f net10.0-maccatalyst \
  -c Release \
  -r maccatalyst-arm64 \
  -p:CreatePackage=true \
  -p:CodesignKey="Apple Distribution: Your Name (TEAM_ID)" \
  -p:CodesignProvision="Your_Provisioning_Profile_Name" \
  -p:PackageSigningKey="3rd Party Mac Developer Installer: Your Name (TEAM_ID)"

# 产物位于:
# src/FKFinder/bin/Release/net10.0-maccatalyst/maccatalyst-arm64/publish/FKFinder-1.0.pkg

# 2. 使用 Transporter 或 xcrun 上传到 App Store Connect
xcrun altool --upload-app \
  -f src/FKFinder/bin/Release/net10.0-maccatalyst/maccatalyst-arm64/publish/FKFinder-1.0.pkg \
  -t macos \
  -u "your-apple-id@example.com" \
  -p "app-specific-password"

# 或者使用 xcrun notarytool（推荐）
xcrun notarytool submit FKFinder-1.0.pkg \
  --apple-id "your-apple-id@example.com" \
  --password "app-specific-password" \
  --team-id "TEAM_ID" \
  --wait
```

### 三、常用开发命令

```bash
# Debug 构建并运行
dotnet build src/FKFinder/FKFinder.csproj -f net10.0-maccatalyst -c Debug
open src/FKFinder/bin/Debug/net10.0-maccatalyst/maccatalyst-arm64/FKFinder.app

# 清理构建产物
dotnet clean src/FKFinder/FKFinder.csproj

# 还原依赖
dotnet restore src/FKFinder/FKFinder.csproj
```

## 项目结构

```
src/FKFinder/
├── Components/        # Blazor 组件
├── Models/            # 数据模型
├── Services/          # 业务服务
├── ViewModels/        # 视图模型
├── Platforms/         # 平台特定代码
│   └── MacCatalyst/   # macOS 平台配置
├── Resources/         # 资源文件（图标、字体、图片）
└── wwwroot/           # Web 静态资源
```
