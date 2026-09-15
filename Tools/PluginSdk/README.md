# Mac Explorer Plugin SDK

## 环境

- 构建插件：.NET SDK 10.0。首次还原需要联网下载 Avalonia 等依赖。
- 安装运行：Mac Explorer {{MINIMUM_HOST_VERSION}} 或更新的兼容版本；首版示例面向 macOS Apple Silicon。
- 本 SDK 为 {{SDK_VERSION}}，支持插件 API v1 / v2；窗口 SDK 使用 Avalonia 12.0.4。SDK 包版本不等于协议版本。

## 五分钟开始

解压整个 ZIP，在解压后的 SDK 根目录执行：

```sh
dotnet build examples/SimplePlugin/SimplePlugin.csproj -c Release --configfile NuGet.Config
dotnet tools/MacExplorer.PluginPack.dll examples/SimplePlugin/bin/Release/net10.0 SimplePlugin.mexplug
```

在 Mac Explorer 的设置 → 插件中安装生成的包。右键本地 `.txt` 文件，选择“文本副本示例 → 生成文本副本”。

窗口及试用示例：

```sh
dotnet build examples/AccountPlugin/AccountPlugin.csproj -c Release --configfile NuGet.Config
dotnet tools/MacExplorer.PluginPack.dll examples/AccountPlugin/bin/Release/net10.0 AccountPlugin.mexplug
```

示例仅模拟登录和购买，不产生真实交易。正式插件需接入自己的账号服务。

## 开发自己的插件

复制一个示例，修改项目名称、清单 ID、名称及入口程序集；使用自己的稳定插件 ID。保留 `EnableDynamicLoading`，通过 PackageReference 引用 packages 中的 SDK。不要引用宿主程序源码或程序集。

打包工具只读取清单和程序集元数据，不执行插件；拒绝符号链接、错误入口、路径冲突和已有输出文件。再次打包请使用新输出名称。插件原生程序应预先签名并保留执行权限。

- `packages/`：基础和窗口 SDK 的本地 NuGet 包，对外统一版本。
- `src/`：SDK、窗口 SDK 和打包工具源码，可独立构建。
- `examples/`：使用包引用的完整示例。
- `tools/`：可通过 dotnet 执行的打包工具。
- `API.md`：接口、生命周期、试用和发布说明。

所有项目引用均位于本 ZIP 内。若只复制示例，需同时复制版本属性文件和 packages，或调整对应引用及包源路径。

## 许可与兼容

SDK、示例及打包工具采用 MIT。第三方依赖保留各自许可证，见 THIRD-PARTY-NOTICES.md。Mac Explorer 主程序保持其原有许可证。

SDK 1.x 的程序集标识保持 1.0.0.0；公开 API 的破坏性修改必须升级 SDK 主版本并同步宿主兼容策略。宿主提供 SDK 和 Avalonia 运行时，插件不得要求宿主未支持的 API 或不兼容的 Avalonia 版本。
