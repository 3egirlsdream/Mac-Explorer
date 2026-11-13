# 侧栏拖拽卡死与闪退修复（2026-11-13）

## 原因与修复

16:32 的 MacExplorer 崩溃报告显示 `os_unfair_lock is corrupt`；对卡死进程 17665 采样时，主线程在原生拖拽图像初始化期间反复陷入 `AvnView.setRenderTarget → _NSSetObjectValueAndNotify` 的异常处理。

两次故障前的最后一条窗口日志均为 `FileDeliveryDropWindow` 首次打开。窗口先由 `MacWindowChrome` 为 `layer` 注册 KVO，随后 `ConfigureDeliveryDrop` 用 `object_setClass` 将视图换成新子类，使 KVO 动态类失效。独立原生复现显示，“监听后换类”在更新 Metal 层时退出，“先配置拖拽、后监听”正常。文件速递主面板激活保护具有同类风险。

修复在窗口首次原生外观准备时，先配置文件速递主面板／投放窗，再注册图层监听。两个配置入口均按视图保存安装状态，重复定位、显示及主题变化保留 KVO 包装类，不再替换视图类。其他窗口保持原流程。

## 回归结果

- `bash Tools/Testing/run-tests.sh fast`：1271 通过、2 个渠道 Skip、0 失败。
- `bash Tools/Testing/run-tests.sh integration --area FileDelivery --area Window --no-build`：5/5 通过。原生测试组合拖拽配置、KVO、重复定位与 Metal 层替换，保留激活保护与点击行为检查。
- `bash Tools/Testing/run-tests.sh ui --area Sidebar --area FileDelivery --area Window`：81/81 通过。侧栏测试等待分组标题所在区域完成布局并可命中后再发出事件。
- 新构建真实 macOS 应用的原生鼠标验证：14 项检查全部通过。连续三次拖入自定义分组均返回 Copy、生成独立快捷入口；第四次 Escape 取消返回 None，未添加入口。四次均使用 AppKit 原生拖拽会话，复用同一投放窗，原目录及内容不变，文件速递暂存保持为空。覆盖浅、深色。
- `git diff --check` 与全局按钮检查通过。

```bash
dotnet build MacExplorer.csproj -c Debug -p:SkipMacOSReleaseDMG=true
MACEXPLORER_DELIVERY_SMOKE=native-drag bash Tools/Testing/run-isolated.sh \
  'bin/Debug/net10.0/osx-arm64/Mac Explorer.app/Contents/MacOS/MacExplorer'
```

此入口仅在 Debug 隔离实例中执行，使用本次测试目录；会实际发送鼠标和 Escape 输入。原生报告、完成截图、构建日志及测试摘要保存在 `outputs/sidebar-drag-crash-20261113/fixed/`，原故障采样和最小复现保存在其上级目录。当前已加载旧原生库的进程需重启，不能通过托管热更新应用此修复。
