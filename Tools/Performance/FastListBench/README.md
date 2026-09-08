# FastFileList 原生基准

在项目根目录构建（运行中的基准窗口应先关闭）：

```sh
dotnet build Tools/Performance/FastListBench/FastListBench.csproj -c Release -p:SkipMacOSReleaseDMG=true
```

macOS 下打开输出目录中的 `FastListBench.app`。窗口依次运行明细、分组明细、网格和分组网格，约 30 秒后退出。每种模式使用十万项数据，执行滚动、随机跳转、快照替换和 24 次光栅图检查；最后记录 1,000/10,000/100,000 个不同显示名称的首次网格布局和刷新耗时，并检查虚拟目录封面。

结果默认写入应用包内 `Contents/MacOS/results/`，按 `details`、`details-grouped`、`grid`、`grid-grouped` 四个子目录保存，每个目录包含 `results.json`、骨架屏 `skeleton.png` 和三个抽查位置的 PNG。根目录另有 `virtual-cover.png` 和 `unique-grid-layout.json`。从终端运行可传入一个输出目录参数。部分自动化终端中直接运行未打包程序会得到 CoreVideo `-6661`，此时通过 Finder 启动 `.app`。

本基准使用 100,000 条合成数据和类型图标。它记录 UI 线程 `Render()` 的 CPU 耗时，单独检查每个完整可见行的名称区域是否有像素内容，并核对滚动是否到达目标行。它**不测量 GPU 呈现 FPS、目录扫描或 Quick Look 生成速度**。光栅抽查与计时分开，避免截图干扰计时；这也不是显示器每一帧的录像验证。


设置环境变量 `FKFINDER_COMPARE_LIST_STYLES=1` 可以改为运行外观对照。工具在同一原生窗口中加载旧 FileListView 模板和新 FastFileList，生成浅色/深色、明细/网格、分组/不分组的新旧 PNG 及实际布局 JSON。通过 Finder 启动时可在独立测试包的 `Contents/Info.plist` 的 `LSEnvironment` 字典中设置此变量，修改后重新签名；不要改动运行中的包。此模式使用固定少量样本，独立于性能计时。
