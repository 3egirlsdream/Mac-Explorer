# 2026-11-09 回收后余量归因

更新：字体表分配回补现已接入生产启动，迁移至 `ThirdParty/Avalonia.TextShaping`，不再需要实验开关；下述 A/B 数值保留正式接入前的实验背景。生产接线与回归结果见 [正式修复记录](FontTableFix-20261109.md)。

## 已确认的直接原因

Avalonia 12.0.4 的 `HarfBuzzTypeface.GetTable` 将 Skia 返回的普通 `byte[]` 用 `GCHandleType.Pinned` 固定，交给 HarfBuzz 的字体表 Blob；句柄直到字体表 Blob 释放才解除。字体随字体缓存长期存活，这些小数组也长期不能移动。后期字体表落在普通小对象堆（SOH）较高位置，前面的临时 UI／预览对象虽已释放，压缩也不能移动这些固定数组来消除空洞、降低该段的占用高水位。macOS .NET 10.0.5 使用 segment GC，大空洞在 Gen0 中保留；这是本次回收后仍占用大量页面的主要原因。

该问题与此前修复的 Helvetica 合成字体缓存失效是两条路径：缓存失效会反复创建字体，而这里即使字体只创建一次，正确缓存的字体也会长期固定 SOH 数组。也不同于原生窗口 COM 引用泄漏。

### 因果验证

独立 .NET 10.0.5 小程序：保留最早的固定数组，分配约 94 MiB 临时小数组，再固定一个 16 KB 普通数组；释放临时数组并执行两次完整压缩 GC 后，Gen0 大小 98,773,872 bytes，其中空洞 98,757,824 bytes。只将后一个数组改为 `GC.AllocateUninitializedArray<byte>(16000, pinned: true)`，相同操作后 Gen0 大小为 24 bytes、碎片为 0。解除普通数组的固定句柄也会使空洞消失，但已提交页的归还仍受运行时策略影响；不能把解除固定等同于即时 RSS 下降。

真实应用 A/B 使用同一份下载目录、相同 PDF 专项操作：8 次目录访问、166 次滚动／8 次完整目录滚动、16 个页签、4 个主窗口、4 个设置窗口关闭及 56 次预览选择。只在测试程序中替换字体排版实现，复制上游同版本代码，将字体表复制到 POH 后再固定；字形选择和排版算法不变。生产应用未启用该替代排版器。

| 完整 GC 快照（MiB） | 原排版器基线 | 原排版器操作后 | POH 对照基线 | POH 对照操作后 |
| --- | ---: | ---: | ---: | ---: |
| 实际 GC 后存活量 | 40.49 | 54.27 | 40.4 | 63.0 |
| 堆碎片 | 23.43 | 109.14 | 1.05 | 1.12 |
| 已提交托管堆 | 71.11 | 174.16 | 56.2 | 76.3 |

仅改变字体表的固定位置，操作后碎片减少约 108 MiB、已提交堆减少约 98 MiB。这是对直接原因的受控验证，不是依据弱引用归零推断。两轮存活量并非完全相同，因此不将 footprint 的全部差异归为字体表自身大小。

原生 physical footprint 的对应阶段：普通完整 GC 后 649.5 → 529.4 MiB；空闲 120 秒并再次完整 GC 后 584.8 → 477.9 MiB。两轮原生基线分别为 412.0 / 402.8 MiB，按基线校正后，空闲回收余量从约 172.8 降到约 75.1 MiB，减少约 97.7 MiB。最终分离后的 GC graph 存活量分别为 54,450,752 / 54,458,528 bytes（均约 51.93 MiB），几乎一致，进一步排除以少创建业务对象换取内存下降。

原排版器最终 GC graph 中有 47 个固定对象，合计仅 2,411,780 bytes；其中小于 85 KB 的对象合计 104,510 bytes。小对象本身约 102 KiB，却能使近 100 MiB 普通堆空间无法压紧。图采集在内存中进行，仅导出类型、数量、大小及虚拟内存分组，未保存原始堆、对象字段值、用户文件路径或内容；采集会触发 GC，故另保留未采集 graph 的图片干净对照。

工具提示 .NET 10 无法启用 .NET 11 的无损阻塞事件缓冲，因此 GC graph 仅作辅助证据，不据此保证每个类型都完整列出；主结论由独立复现、GC 公共计数、原生 footprint 及只改固定位置的 A/B 共同支持。两轮最终固定对象数仍均为 47，改变的是分配位置，不是提前释放字体。

## 为什么最初还会多出约 200 MiB

这个差额包含不同部分，不能把它都解释为存活业务对象。在本次原排版器 PDF 对照里，稳定的原生采样基线为 412.0 MiB，普通完整 GC 后为 649.5 MiB；延长自然空闲 120 秒，再完整 GC 后为 584.8 MiB。约 65 MiB 在空闲期回落，说明刚 GC 后的余量包含延迟清理与原生分配器页面回收。

空闲后相对基线仍多 172.8 MiB：`vmmap` 的 Untagged 脏页／交换页增加约 125.4 MiB，malloc 区域增加约 47.8 MiB。malloc 增量中，存活分配增加约 19.7 MiB、碎片／空闲页增加约 28.1 MiB；不是额外 47.8 MiB 业务对象。Untagged 中托管堆提交量增加约 102 MiB，其中约 90 MiB 是新增碎片、约 9.4 MiB 是新增存活对象；可执行 Untagged 页增加约 12 MiB，JIT 事件另确认已涉及代码页从约 10.7 MiB 增至约 24.5 MiB。不同统计口径不完全可加，剩余小额归于运行时映射与页级开销，不声称每页都已定位到分配栈。

完整压缩、单独 LOH CompactOnce、Aggressive GC、`malloc_zone_pressure_relief(NULL, 0)`、Skia CPU 缓存清理及切换空目录均未消除原排版器的大块 Gen0 空洞。malloc relief 返回 0；Skia CPU 图片资源为 0、字体缓存约 0.55 MiB。因此反复强制 GC 或清空图片缓存不能修掉这条字体表固定路径。

## 诊断数值修正

本机 `GC.GetTotalMemory(false)` 另有重复估计：独立小程序仅保留约 16 MiB 数组时返回 33,130,736 bytes，而同一次完整 GC 的 `HeapSizeBytes - FragmentedBytes` 为 16,565,360 bytes。运行时非 region 分支计算 Gen0 跨度时包含更老代，再累加老代存活量。不能直接把历史 `managedBytes` 当作实际存活量，也不能统一除以二。

生产日志新增与 `gcIndex` 对齐的 `liveAfterGcBytes = heapBytes - fragmentedBytes`，保留原字段兼容已有分析。该值属于所标识的最近一次 GC，不是采样时当前存活量；采样本身不强制 GC。

## 验证范围与后续修复位置

具体修复位置是上游字体表 Blob 的创建／释放边界：长期表数据应使用 POH 或由 Blob 释放的非托管缓冲区，避免固定普通 SOH 数组。首次对照使用的 `RetentionTextShaper.cs` 与实验开关现已移除；对应代码迁移到 `ThirdParty/Avalonia.TextShaping`，经固定版本保护接入生产。正式回补核对上游平台接口、字体排版一致性及升级策略，不通过无条件定时 GC 替代。

本次两轮受控测试及干净图片测试不等同于两小时自然回收验收。最早一轮 PDF 探针在普通 GC 后两秒仍有一个托管窗口弱引用未消失而结束，没有计作通过；正式对照记录完整等待后的弱引用和原生窗口引用检查。

正式原排版器和 POH 对照均取得 `completed: true`、退出码 0，分别运行约 6.7 分钟；每轮 16 个已关闭页签、4 个主窗口及 4 个设置窗口的最终弱引用均为 0，8 次原生窗口引用检查均为 0。POH 对照最终普通堆 Gen0 碎片约 0.07 MiB、全堆碎片约 0.1 MiB；原排版器对应全堆碎片约 112.4 MiB。生产代码相关 fast 检查 1150 通过、2 跳过、0 失败；最终基准构建和 Python 语法检查通过。

归档 6527 条 JSONL 记录，下载目录路径匹配为 0、丢弃事件为 0；用 1940 个文件／目录名词条检查后，仅有系统 VM 字段、原生区域类型、固定事件名及窗口类名的静态词汇碰巧相同，没有用户文件派生字段。GC graph 归档进一步只保留数值和固定类型词汇；原排版器基线旧采集器未正确筛选固定句柄，已省略其无效的基线固定对象列表，最终 47 个句柄的统计来自修正后的采集器。

数值证据保存在 `outputs/memory-retention-20261109/`；运行配置保留程序集 SHA256。早期配置中的 `forcedGc: final-only` 是旧标签，`retentionProbe: true` 以及事件顺序明确记录了基线／阶段 GC；运行器现已改为 `controlled-retention-probe`，不把归因实验误标为自然回收长测。

源代码依据：[HarfBuzzTypeface.GetTable](https://github.com/AvaloniaUI/Avalonia/blob/12.0.4/src/HarfBuzz/Avalonia.HarfBuzz/HarfBuzzTypeface.cs)、[SkiaTypeface.TryGetTable](https://github.com/AvaloniaUI/Avalonia/blob/12.0.4/src/Skia/Avalonia.Skia/SkiaTypeface.cs)、[.NET 10.0.5 GC 实现](https://github.com/dotnet/runtime/blob/v10.0.5/src/coreclr/gc/gc.cpp)、[macOS 的 segment 配置](https://github.com/dotnet/runtime/blob/v10.0.5/src/coreclr/gc/gcpriv.h)。
