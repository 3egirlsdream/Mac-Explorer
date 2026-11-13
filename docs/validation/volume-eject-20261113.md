# 外置卷弹出排查（2026-11-13）

## 实机证据

设备为 USB 连接的 APFS 卷 `External`，容量约 500 GB。

- Rider 直接启动 `bin/Debug/net10.0/osx-arm64/MacExplorer` 时，在应用内重复弹出失败。新增日志捕获 `NSOSStatusErrorDomain` / `-47`，首次约 3ms 返回，没有提供拒绝进程 PID；同一时段 `lsof` 未发现该卷上的打开文件。
- 将调用切换到 AppKit 主线程后，未打包实例仍失败，因此没有保留此改动。
- 使用 `run-isolated.sh` 启动 Debug `.app/Contents/MacOS/MacExplorer`，在真实侧栏点击同一卷的弹出按钮后，侧栏条目消失，`volume.eject` 记录 `success: true`，耗时约 135ms。测试配置位于 `/private/tmp/fkfinder-test.XKhkiQ`。
- 上述对照支持“未打包启动环境导致原生弹出失败”，不支持将其归因于某个已确定的文件占用进程。正式签名发布包及 App Store 渠道没有在本轮单独验收。

## 最终改动

- 新增 `macOS App Bundle` 启动配置，使用现有 Debug arm64 应用包；本机 .NET 10 不展开启动配置中的 MSBuild 路径变量，因此使用与项目默认框架、架构一致的相对路径。
- 保留系统原生 `NSWorkspace` 安全弹出流程，返回实际错误描述；无具体原因时使用中性提示，四语同步。
- 使用现有有界诊断日志记录 `volume.eject`：成功标志、错误域与错误码链、系统提供的拒绝进程 PID、耗时。托管调用异常记录异常类型与 HResult。不记录卷名、文件路径或系统错误正文。
- 成功后立即刷新挂载列表。网络挂载沿用同一原生桥。

## 验证入口

```bash
bash Tools/Testing/run-tests.sh fast
bash Tools/Testing/run-tests.sh integration --area MacVolumeMonitorService
bash Tools/Testing/run-isolated.sh dotnet run --project MacExplorer.csproj --no-build --launch-profile 'macOS App Bundle'
```

原生集成测试使用自行创建的 HFS+ / APFS 磁盘映像，验证打开文件时拒绝弹出、保持挂载，关闭文件后成功弹出；只清理本次测试卷。

Rider 中选择新增的 `macOS App Bundle` 启动配置，构建主项目后运行；原来的项目可执行文件配置仍可能复现启动环境问题。

## 最终结果

- `fast`：1276 通过、2 跳过、0 失败。
- `integration --area MacVolumeMonitorService`：HFS+ 与 APFS 两项原生测试全部通过。
- 移除试验性 Disk Arbitration／主线程切换后，重新构建应用包，通过新增启动配置启动全新隔离实例 `/private/tmp/fkfinder-test.p4zkUM`。再次在侧栏点击真实 External 的弹出按钮，挂载条目消失，日志再次记录 `success: true`。
- 证据摘要：`outputs/volume-eject-20261113/eject-events.json` 与 `final-eject.json`。
