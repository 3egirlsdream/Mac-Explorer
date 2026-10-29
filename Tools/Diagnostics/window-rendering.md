# 最大化／还原后的界面缩放诊断

运行包含本次埋点的构建后自动启用，无需修改设置。已经启动的旧进程需要退出并启动新构建。

## 问题再次出现时

1. 记下发生的大致时间，以及是否刚唤醒电脑、连接显示器或切换显示缩放。
2. 保持异常窗口约 3 秒。方便时截一张图，帮助区分整个界面放大与局部控件变化。
3. 复制日志目录中的全部 `memory-*.jsonl` 并压缩，连同发生时间交给开发者分析。不要只截取包含“error”的行，异常前后的正常尺寸也需要保留。

网站版默认目录：`~/Library/Application Support/MacExplorer/Logs/`。
商店版使用应用容器内对应的 `RuntimePaths.LogDirectory`；隔离测试使用 `$MACEXPLORER_TEST_ROOT/.macexplorer/MacExplorer/Logs/`。

日志仅保存在本地，不会自动上传。沿用现有 128 条异步队列、单条 8 KiB、单文件 2 MiB、最多 8 个轮转文件。问题出现后请及时复制，继续长期运行或启动其他实例可能使旧记录轮转淘汰。

## 记录内容与边界

- `window.render`：匿名窗口编号、类型、进程／窗口运行时间、双击发出的目标状态、实际状态、客户端与布局尺寸、渲染缩放、布局是否有效、根控件变换。
- `native`：原生窗口和视图尺寸、屏幕及窗口的 backing scale、图层尺寸／变换、呈现层几何、动画数量，以及 Metal 的 `drawableSize`。`layerKind=Metal` 能确认实际使用 Metal；`CALayer` 不能单独区分 OpenGL 与软件渲染。`nativeStatus=unavailable` 表示未取得原生快照，不代表状态正常。
- 状态变化即时采样；连续尺寸变化合并后，在约 250 ms、1 s、3 s 采样。每 30 秒检查可见窗口，正常且未发生事件时每 5 分钟保留一次完整快照。
- `window.render_mismatch`：连续采样仍存在不一致，且距离最后一次尺寸变化至少 1 秒。可能标记 `managed-layout-size`、`native-view-size`、`view-coordinate-scale`、`backing-scale`、`layer-size`、`metal-drawable-size`。这是调查线索，不是根因判定。
- `memory.ui.dispatcherDelayMs`：主线程执行周期采样的排队时间，可与内存、GC、进程运行时间一起判断卡顿背景。

只读取公开原生属性，不获取 drawable、不强制布局／重绘、不刷新 Core Animation 事务、不截屏、不读 GPU 像素，也不读取 Avalonia 私有字段。不会记录窗口标题、文件路径、搜索词或文件内容。图层报告尺寸正确但内部 GPU 帧异常时，日志不能单独证明渲染内容是否正确；因此并非所有视觉问题都能自动标记。

## 原生埋点验证

```bash
dotnet build MacExplorer.csproj -c Debug -p:SkipMacOSReleaseDMG=true
MACEXPLORER_WINDOW_RENDER_SMOKE=1 bash Tools/Testing/run-isolated.sh \
  'bin/Debug/net10.0/osx-arm64/Mac Explorer.app/Contents/MacOS/MacExplorer'
```

独立配置下连续最大化／还原三轮，每步等待 3.5 秒，随后退出；日志位置打印在终端。检查每轮 `state-request`、`state-changed` 和 `settled-3`，确认 `nativeStatus=ok`，并比较 `native.drawableSize` 与 `native.backingSize`。这验证埋点能采集真实图层，不代表复现了用户的低概率故障。
