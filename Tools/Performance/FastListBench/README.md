# FastFileList 原生基准

在项目根目录构建（运行中的基准窗口应先关闭）：

```sh
dotnet build Tools/Performance/FastListBench/FastListBench.csproj -c Release -p:SkipMacOSReleaseDMG=true
```

macOS 下打开输出目录中的 `FastListBench.app`。窗口依次运行明细、分组明细、网格和分组网格，约 45 秒后退出。每种模式使用十万项数据，执行滚动、随机跳转、快照替换、180 次小步连续滚动和 24 次光栅图检查；最后记录 1,000/10,000/100,000 个不同显示名称的首次网格布局和刷新耗时，并检查虚拟目录封面。

结果默认写入应用包内 `Contents/MacOS/results/`，按 `details`、`details-grouped`、`grid`、`grid-grouped` 四个子目录保存，每个目录包含 `results.json`、骨架屏 `skeleton.png` 和三个抽查位置的 PNG。根目录另有 `virtual-cover.png`、`unique-grid-layout.json` 和 `tree-comparison.json`。树形对比对同一批十万项交替测三轮普通列表与未展开树形列表，取各自 p95 中位数，并测展开一万项子目录后的行提交、收起和渲染耗时；`withinTwentyPercent` 表示未展开树形相对普通列表是否满足 20% 门槛。从终端运行可传入一个输出目录参数；只测树形对比可追加 `--tree-only`。部分自动化终端中直接运行未打包程序会得到 CoreVideo `-6661`，此时通过 Finder 启动 `.app`。

只测文件夹照片封面可追加 `--folder-cover-only`，输出 `folder-cover-comparison.json`。它对 10,000 个合成文件夹分别测关闭、开启、再次关闭时的快速跳转、连续小步滚动和停留渲染（停留 250ms，覆盖首张封面 180ms 启动等待及后续错峰请求），并记录封面请求数；另用 1,000 个本地直接子文件测扫描与合成耗时。缩略图源由可取消、最多两个并发的 8ms 测试替身提供，因此结果不包含原生 Quick Look 生成或 GPU 呈现帧率。

本基准使用 100,000 条合成数据和类型图标。它记录 UI 线程 `Render()` 的 CPU 耗时，单独检查每个完整可见行的名称区域是否有像素内容，并核对滚动是否到达目标行。它**不测量 GPU 呈现 FPS、目录扫描或 Quick Look 生成速度**。`renderP95Ms` 记录快速跳转，`continuousRenderP95Ms` 单独记录每步 3.5 像素的连续滚动，覆盖同一屏文字反复绘制的场景。光栅抽查与计时分开，避免截图干扰计时；这也不是显示器每一帧的录像验证。
