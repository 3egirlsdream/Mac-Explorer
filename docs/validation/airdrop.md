# 隔空投送接入与验收

文件列表右键“发送 → 隔空投送”支持本地单选与多选，不依赖 LocalSend 开关；LocalSend 启用时两项并列显示。远程路径、虚拟项目、归档内容、废纸篓及只读浏览面板不提供此入口。

中英文菜单名称均不带省略号。`Assets/airdrop-logo.png` 于 2026-10-19 从 macOS 的 `NSSharingService(named: .sendViaAirDrop).image` 原样导出，使用 Apple 系统提供的官方图标，复用现有菜单图片尺寸与渲染。

菜单先关闭，再通过 AppKit 的 `NSSharingServiceNameSendViaAirDrop` 展示系统发送窗口。接收方发现、选择、确认和传输由 macOS 处理。菜单记录打开时的选择快照，不随之后的选中项变化。取消不显示错误，失败使用现有窗口 toast。商店版在整个异步分享期间持有独立安全作用域授权。

## 自动验证

```sh
bash Tools/Testing/run-tests.sh ui --area AirDrop --area LocalSend
bash Tools/Testing/run-tests.sh integration --area AirDrop --no-build
bash Tools/Testing/run-tests.sh fast --no-build
```

- UI：单选、多选、右键未选中项、选择快照、LocalSend 开关／缺省服务、平台支持、非法上下文及混合选择、中英文切换、失败提示、菜单先关闭再开始分享。
- 原生集成：真实文件 URL（含中文、空格和引号）、多文件、无效输入与缺失文件、系统 AirDrop 服务可用性，以及成功／取消／失败回调、异步完成与重复回调清理。测试不发送文件，不需要另一台设备。
- `Tests/Native/AirDropBridgeTests.mm` 由集成用例编译并在独立临时目录执行。

## 原生 UI 与设备验收

使用 `bash Tools/Testing/run-isolated.sh <新构建.app>/Contents/MacOS/MacExplorer` 启动独立实例；只在打印出的测试目录创建测试文件。

1. LocalSend 默认关闭时，单选测试文件，检查“发送 → 隔空投送”能打开系统发送窗口。
2. 取消后重新打开，确认会话已清理；多选两个测试文件，再次打开并核对项目数量。
3. 实际传输需要一台已启用隔空投送的接收设备，由测试人员选择接收方，核对接收文件内容与发送端原文件均完整。
4. 商店签名沙箱包另行验证授权文件、授权失效与撤销授权时的行为；普通开发包的通过结果不能代替此项。

原生测试程序另支持 `--ui`，提供单文件／多文件按钮来检查真实系统窗口，可独立于 Avalonia 的菜单输入自动化验证 AppKit。程序必须接收独立测试目录作为首个参数：

```sh
airdrop_test_dir=$(mktemp -d /private/tmp/fk-airdrop-native.XXXXXX)
clang++ -fobjc-arc Tests/Native/AirDropBridgeTests.mm -framework AppKit -framework Foundation -o "$airdrop_test_dir/AirDropTest"
bash Tools/Testing/run-isolated.sh /bin/sh -c 'exec "$1" "$MACEXPLORER_TEST_ROOT" --ui' -- "$airdrop_test_dir/AirDropTest"
```

## 2026-10-19 验证结果

- AirDrop 与 LocalSend 菜单 UI：13 通过，0 失败。
- 原生集成：1 通过，0 失败。
- 日常 fast：763 通过，2 个商店渠道用例跳过，0 失败。首次测试暴露磁盘枚举的并发原生崩溃；串行化各文件列表实例的枚举后复测通过。
- arm64、x86_64 原生桥接语法编译通过。
- 独立原生 UI 程序已实际打开单文件及多文件系统 AirDrop 窗口，取消后可再次打开。系统显示附近没有可共享的用户，未进行跨设备传输。
- 完整应用右键流程由 Headless UI 覆盖；外部原生输入自动化未能可靠触发该右键菜单。商店签名沙箱与真实接收设备验收仍需另行执行。
