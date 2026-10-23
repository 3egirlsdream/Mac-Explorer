# macOS 内存生命周期回归

在真实 Avalonia/macOS 窗口中验证原生无障碍引用、页签、设置和任务上下文的回收。需要 Apple Silicon Mac；所有测试文件、配置和索引均在临时测试目录内。

```bash
dotnet build Tools/Performance/MemoryBench/MemoryBench.csproj -p:SkipMacOSReleaseDMG=true
bash Tools/Testing/run-isolated.sh \
  Tools/Performance/MemoryBench/bin/Debug/net10.0/osx-arm64/MemoryBench.app/Contents/MacOS/MemoryBench \
  outputs/memory-verification
```

执行约两分钟：在四个 500 文件目录间导航 36 次，分三轮创建并关闭 36 个页签，完成 30 个各捕获 1 MiB 数据的任务，再打开并关闭三次设置。遍历真实 NSAccessibility 树以确保创建了原生包装器，不依赖是否连接了屏幕阅读器。每轮完整 GC 后检查已关闭页签、窗口和完成任务负载的弱引用；任务摘要必须继续存在。任一检查失败以非零状态退出并保存 `error.txt`。

输出 `samples.json` 和 `verification.json`，并将生产同款内存日志写入 `diagnostics/`（见 [诊断日志与保护策略](Diagnostics.md)）。`physicalFootprintMiB` 来自 macOS `task_info`，无法读取时为 null。`rssMiB` 为 RSS，`managedMiB` 来自 `GC.GetTotalMemory`，`heapMiB` / `committedMiB` 来自最后一次 GC 的堆统计；这些量不等价，也不与 macOS Activity Monitor 的 physical footprint 混用。`*-gc` 仅在诊断程序内强制回收，生产程序不定时调用 GC。最后保留一分钟空闲阶段，期间可在日志出现 `READY FOR HEAP SNAPSHOT` 后用 `dotnet-gcdump` 采集堆。该工具也会触发 GC，比较数据时需记录其影响。

修复应以关闭对象归零、重复操作后堆不累积为标准，不把某个绝对 RSS 数值作为跨机器阈值。完整测试还要求原生引用修复已安装，且关闭第 36 个页签后相对第 12 个的托管存活量增长小于 8 MiB，以捕捉只释放控件、仍泄漏 COM 包装器的回归。Debug/Release、渲染器、辅助技术、窗口尺寸与本机内存压力会影响占用。

定位失败可用 `MACEXPLORER_MEMORY_QUICK=1` 缩短预热和空闲阶段，报告标记 `quick`；最终验收不设置此变量。`MACEXPLORER_MEMORY_HOLD_ON_FAILURE=1` 在失败后保留进程 30 秒，供采集堆。

原生生命周期桥接针对当前固定的 Avalonia 12.0.4：清理窗口关闭回调、页签标题、页面控件及装饰层节点，并移除失效包装器的回调与缓存映射，容器复用时会创建新的桥接。`AutomationLifetime.mm` 修正 `AvnAccessibilityElement` 和 `AvnView` 构建子节点列表时遗漏的 COM Release，保留原有无障碍树。托管侧按精确程序集版本启用；原生侧检查方法及字段后替换实现。升级 Avalonia 时必须核对接口 ABI 并重跑原生用例，不能只依赖 Headless 测试。

对应上游实现：[automation.mm](https://github.com/AvaloniaUI/Avalonia/blob/12.0.4/native/Avalonia.Native/src/OSX/automation.mm) 和 [AvnView.mm](https://github.com/AvaloniaUI/Avalonia/blob/12.0.4/native/Avalonia.Native/src/OSX/AvnView.mm)。
