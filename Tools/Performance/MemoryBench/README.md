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

## 自然回收实机长测

先构建上面的 Debug 原生应用，再执行：

```bash
python3 Tools/Performance/MemoryBench/run-soak.py --minutes 120 --output /tmp/macexplorer-memory-soak-run
```

输出目录必须尚不存在。运行器复制本次 `.app` 并记录程序集哈希，避免测试途中构建覆盖二进制；通过隔离入口启动，测试期间仅阻止系统因空闲休眠，不阻止用户主动睡眠。长测六个时段分别执行导航、页签／窗口／设置、图片／PDF／画廊预览，再重复一次。每段前半重复操作、后半回到相同单页签目录空闲观察。目录样例为四组各 2000 个文件及可展开子目录，预览为 96 张 3000×2000 图片和 12 页 PDF，均在测试根目录生成，不读写用户文件。

用 `python3 Tools/Performance/MemoryBench/analyze-soak.py /tmp/macexplorer-memory-soak-run --wait` 等待该次运行结束并写入 `analysis.json`；已结束或需要查看中途统计时省略 `--wait`。汇总按场景比较两轮空闲结束时的基线、空闲期增长率、Gen2 次数及最终强制回收的差异。`--wait` 默认最多等待 180 分钟，较长测试可通过 `--timeout-minutes` 调整。

正常阶段不主动 GC，结束后才记录一次独立强制回收对照。`soak-samples.jsonl` 与 `progress.json` 保存阶段、操作次数、托管／进程内存、GC 及关闭对象弱引用数量；`native-samples.jsonl` 在阶段切换时提取 `vmmap -summary` 的数值分区、malloc 分配／碎片及系统页统计，不保存原始输出、文件路径、地址或内容。`verification.json` 表示流程完成；`failure.json`／`runner-result.json` 记录失败类型与超时状态，不包含异常消息。生产同款日志在 `diagnostics/`。可先用 `--minutes 3` 检查场景接线，短测不代替长时间结果。主线程超过 180 秒没有进度或进程 footprint 超过 3 GiB 时结束该测试实例并保留已有统计。

原生生命周期桥接针对当前固定的 Avalonia 12.0.4：清理窗口关闭回调、页签标题、页面控件及装饰层节点，并移除失效包装器的回调与缓存映射，容器复用时会创建新的桥接。`AutomationLifetime.mm` 补齐子节点列表、已有节点、父节点、根节点与根窗口查询遗漏的 COM Release，保留原有无障碍树和父子关系。`accessibilityParent` 取得根窗口后不释放，会让 C++ `WindowImpl` 持续保留视图、输入法与渲染目标；即使托管窗口弱引用归零，完整 GC 也无法释放这些原生对象。托管侧按精确程序集版本启用；原生侧检查方法及字段后替换实现。升级 Avalonia 时必须核对接口 ABI 并重跑原生用例，不能只依赖 Headless 测试。

对应上游实现：[automation.mm](https://github.com/AvaloniaUI/Avalonia/blob/12.0.4/native/Avalonia.Native/src/OSX/automation.mm) 和 [AvnView.mm](https://github.com/AvaloniaUI/Avalonia/blob/12.0.4/native/Avalonia.Native/src/OSX/AvnView.mm)。

可用 `--lifetime-kind window`、`settings`、`tab` 将六段固定为同一种生命周期场景，用 `--software-rendering` 做渲染器对照。窗口场景保留一个测试自有 COM 引用至关闭完成，然后释放并记录剩余引用数；验收要求它为零，且最终 GC 后关闭对象的弱引用归零。AX 遍历同时检查父节点、所属窗口及顶层窗口，防止以破坏无障碍树换取回收。运行器只提取 `heap --noContent` 中指定原生类的数量和字节数，不保存堆内容或地址。

`MacFontLifetime` 补齐 Avalonia 12.0.4 系统字体缓存缺失的“请求家族名＋样式”键。Helvetica／Helvetica Neue 在本机返回空 `TypographicFamilyName` 和 `FamilyNames`，上游合成字形只按这两类备用名缓存，导致粗体查询不断走 `OpenStream`／`FromStream`，CoreText 注册表随之累积。桥接复用原系统字体集合和原查询结果，不另建字体缓存、不改字体家族或字重。原生基准对这两个家族的 SemiBold／Bold 各重复查询 50 次，要求返回同一字形实例；结合 native heap 中字体对象数量验证，不能用 Headless 的替代字体推断此问题已修复。

## 真实文件只读回归

设置 `MACEXPLORER_MEMORY_REAL_ROOT="$HOME/Downloads"` 后运行 `run-soak.py --minutes 6 --output <新目录>`，使用真实目录及文件代替生成样例。测试在所选目录内限时、限深扫描最多 200 个目录，选根目录和三个文件较多的子目录，以及图片／PDF 较多的目录，重复导航、滚动、树展开、关闭页签／窗口及预览。不跟随符号链接，不递归应用包，不执行文件移动、重命名或删除。配置、缓存和索引默认路径仍按测试规则隔离，显式目录访问不受该隔离限制。只记录数据来源、匿名目录条目数及预览数量，不把真实路径或文件名写入诊断日志。

运行器必须同时取得 `verification.json` 的完成标记；应用提前退出即使退出码为 0，也标记为 `app-exited-before-verification` 并以非零状态结束，不能把部分长测当作通过。

设置 `MACEXPLORER_MEMORY_PREVIEW_KIND=pdf` 可单独选择真实 PDF 目录与预览文件。真实目录的导航按视口连续滚到末尾，每个目录最多 100 步，记录实际步数和完整滚动次数；配置变量不写入日志。

## 回收后余量归因实验

`MACEXPLORER_MEMORY_RETENTION_PROBE=1` 在基线和结束时执行完整 GC，结束后额外观察 120 秒自然空闲，再依次执行普通 GC、LOH 压缩、Aggressive GC、malloc pressure relief、Skia CPU 缓存清理及切换到空目录。每个状态保持 10 秒供数值采样；这是受控归因实验，不能按自然回收长测解释。`retention.state` 同时记录 GC index 对齐的存活量、各代碎片、Skia CPU 缓存、JIT 字节／代码页计数及非字符串 GC 配置。

字体表 POH 修补现已接入生产 `App.Initialize`，无需实验环境变量。`ThirdParty/Avalonia.TextShaping` 保存固定 12.0.4 的排版回补，只改变字体表分配位置和句柄失败清理，平台接口的实现程序集引用仅限该独立适配项目。基准要求普通启动已安装该生产实现，并对 4 个本机字体家族、3 种字重、10 组文字及 OpenType 特性开关执行 240 次逐字形／位置一致性比较；测试创建的原排版字体表在检查结束后释放。两小时自然回收验收仍应关闭归因探针，不以周期性强制 GC 代替修复。升级时按该项目 `NOTICE.md` 复核。

`MACEXPLORER_MEMORY_HEAP_GRAPH_HOLD=1` 在基线及最终分离后各保持 30 秒，供外部 GC graph 工具在内存中核对类型／固定句柄。此类采集会触发 GC，必须与未采集的干净对照区分，不保存原始堆或对象内容。
