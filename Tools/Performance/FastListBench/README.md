# FastFileList 原生基准

页签和右键交互基准使用 `--interaction-only`，通过隔离启动器运行：

```sh
dotnet build Tools/Performance/FastListBench/FastListBench.csproj -c Debug -p:SkipMacOSReleaseDMG=true
bash Tools/Testing/run-isolated.sh Tools/Performance/FastListBench/bin/Debug/net10.0/osx-arm64/FastListBench.app/Contents/MacOS/FastListBench /private/tmp/fkfinder-interaction-results --interaction-only
```

在隔离目录创建 100 个文件，使用原生渲染窗口，测浅／深色的页签切换、文件选择与三类右键菜单，以及新建页签、连续拖动和松手排序。`interaction-results.json` 分别记录操作耗时、两次动画帧回调的延迟、布局次数、页签位图重建次数、玻璃采样和进程分配量；保留首次与后续样本，不用单次结果推导性能提升。菜单直接调用应用入口，拖动通过 Avalonia 路由事件注入，不覆盖系统鼠标投递延迟，帧回调也不代表显示器呈现时间。测量时不要并行构建或运行其他基准；附着调试器的问题需要另对实际 Debug 进程采样。

`file-context` 观察打开入口及接下来两帧；`file-context-complete` 等待初始、完整两次菜单动作发布，再等待两帧，确保异步或低优先级更新计入总成本。对比旧、新构建时必须使用同一版本的基准程序集；完整菜单指标与早期仅测打开入口的结果分开统计。

任务面板开合基准使用 `--tasks-only`，同样通过隔离启动器运行：

```sh
bash Tools/Testing/run-isolated.sh Tools/Performance/FastListBench/bin/Debug/net10.0/osx-arm64/FastListBench.app/Contents/MacOS/FastListBench /private/tmp/fkfinder-task-panel-results --tasks-only
```

默认只填入 2 个已完成任务，进度负载每 50ms 更新一次，每组预热后开合 3 次。只有显式追加 `--tasks-stress` 才测 2／100 项、每 8ms 更新进度和每组开合 5 次；此前这组压力测试曾伴随整机鼠标短暂无响应，根因尚未定位，不应反复运行。

单次开合或预热返回后若总耗时超过 500ms，停止后续轮次并以失败退出；这个检测不能阻止首次停顿，只用于避免继续施压。测量阶段停止时保留已采集的部分数据。`task-panel-results.json` 记录整段耗时、进程 CPU、面板布局次数、UI 线程位移修改次数、玻璃采样次数及 `RequestAnimationFrame` 回调间隔。回调间隔不代表 GPU 呈现 FPS；测量时不要同时运行构建或其他基准。`FASTLIST_BENCH_KEEP_OPEN=1` 仅在成功完成后保留测试窗口。

真实目录导航基准使用完整 `App`、主窗口、目录枚举、SQLite 搜索索引和原生缩略图服务：

```sh
dotnet build Tools/Performance/FastListBench/FastListBench.csproj -c Release -p:SkipMacOSReleaseDMG=true
bash Tools/Testing/run-isolated.sh Tools/Performance/FastListBench/bin/Release/net10.0/osx-arm64/FastListBench.app/Contents/MacOS/FastListBench /private/tmp/fkfinder-navigation-results --navigation-only
```

每次在隔离目录创建 1,000／10,000／100,000 个名称不同的真实文件，分别测列表、图标和未展开树形模式；搜索索引 Ready 和主动 Refresh 两组各重复三次，共 54 次。`busy` 表示请求过刷新，实际是否仍在扫描以 `phaseAtStart`／`phaseAtEnd` 为准。基准检查完整数量与名称排序，记录导航开始至完整列表第一次 `Render()`、UI 提交时间、16ms UI 心跳最长间隔及滚动绘制 p95。结果写入 `navigation-results.json`，错误写入 `navigation-error.txt`。这些计时不含鼠标输入投递延迟，也不代表显示器呈现 FPS。基准只允许通过隔离启动器运行。

快速滚动追加 `--scroll-only`，在隔离目录创建 10,000 项（文件夹、PNG 和文本各约三分之一），名称包含中文、重音拉丁字符和 emoji。列表／图标／树形模式各测关闭、开启真实缩略图，每 8ms 请求推进 3.5／120／600px，每组 120 步、重复两遍，共 36 组。文字缓存保留正常的 1,024 项上限，因此第二遍也可能重新排版。首次 OCR 在本次隔离配置中关闭，搜索索引等待 Ready；这用于单独测滚动，未覆盖 OCR 并发负载。

`scroll-results.json` 记录绘制 CPU 时间、8ms UI 心跳和 `RequestAnimationFrame` 回调间隔，以及超过 25／50ms 的回调数；回调间隔并非 GPU 呈现 FPS。可设置 `FASTLIST_BENCH_FONT_FAMILY` 覆盖本次字体资源，使用同一二进制做字体配置 A/B 对比；默认运行同时检查主字体直接解析。`FASTLIST_BENCH_KEEP_OPEN=1` 在测量完成后保留隔离主窗口用于原生交互验证。

在项目根目录构建（运行中的基准窗口应先关闭）：

```sh
dotnet build Tools/Performance/FastListBench/FastListBench.csproj -c Release -p:SkipMacOSReleaseDMG=true
```

macOS 下打开输出目录中的 `FastListBench.app`。窗口依次运行明细、分组明细、网格和分组网格，约 45 秒后退出。每种模式使用十万项数据，执行滚动、随机跳转、快照替换、180 次小步连续滚动和 24 次光栅图检查；最后记录 1,000/10,000/100,000 个不同显示名称的首次网格布局和刷新耗时，并检查虚拟目录封面。

结果默认写入应用包内 `Contents/MacOS/results/`，按 `details`、`details-grouped`、`grid`、`grid-grouped` 四个子目录保存，每个目录包含 `results.json`、骨架屏 `skeleton.png` 和三个抽查位置的 PNG。根目录另有 `virtual-cover.png`、`unique-grid-layout.json` 和 `tree-comparison.json`。树形对比对同一批十万项交替测三轮普通列表与未展开树形列表，取各自 p95 中位数，并测展开一万项子目录后的行提交、收起和渲染耗时；`withinTwentyPercent` 表示未展开树形相对普通列表是否满足 20% 门槛。从终端运行可传入一个输出目录参数；只测树形对比可追加 `--tree-only`。部分自动化终端中直接运行未打包程序会得到 CoreVideo `-6661`，此时通过 Finder 启动 `.app`。

只测文件夹照片封面可追加 `--folder-cover-only`，输出 `folder-cover-comparison.json`。它对 10,000 个合成文件夹分别测关闭、开启、再次关闭时的快速跳转、连续小步滚动和停留渲染（停留 250ms，覆盖首张封面 180ms 启动等待及后续错峰请求），并记录封面请求数；另用 1,000 个本地直接子文件测扫描与合成耗时。缩略图源由可取消、最多两个并发的 8ms 测试替身提供，因此结果不包含原生 Quick Look 生成或 GPU 呈现帧率。

本基准使用 100,000 条合成数据和类型图标。它记录 UI 线程 `Render()` 的 CPU 耗时，单独检查每个完整可见行的名称区域是否有像素内容，并核对滚动是否到达目标行。它**不测量 GPU 呈现 FPS、目录扫描或 Quick Look 生成速度**。`renderP95Ms` 记录快速跳转，`continuousRenderP95Ms` 单独记录每步 3.5 像素的连续滚动，覆盖同一屏文字反复绘制的场景。光栅抽查与计时分开，避免截图干扰计时；这也不是显示器每一帧的录像验证。
