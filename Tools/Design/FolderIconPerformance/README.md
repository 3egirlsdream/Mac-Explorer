# 文件夹玻璃封面性能比较

比较现有照片封面与 Demo 的实际渲染器。直接链接两者源文件并优化编译，避免用复制的临时算法代替实现。主程序默认外观未替换。

```bash
dotnet build Tools/Design/FolderIconPerformance/FolderIconPerformance.csproj -p:SkipMacOSReleaseDMG=true
bash Tools/Testing/run-isolated.sh \
  "Tools/Design/FolderIconPerformance/bin/Debug/net10.0/osx-arm64/FolderIconPerformance.app/Contents/MacOS/FastListBench" \
  outputs/folder-icon-performance/optimized-run1
```

第二个可选参数为示例素材目录，默认 `Tools/Design/FolderIconDemo/Assets`。追加 `--reverse` 以新／旧／新反向复测。输出 `performance.json`；失败时输出 `error.txt` 并以非零退出。

追加 `--binary-only` 可读取当前已构建的 Debug Demo 程序集，测量真实二进制的 112px／256px 合成，输出 `actual-demo-binary.json`；该模式需要先构建 Demo，并从仓库根目录启动，不打开列表窗口。

- 两张相同源照片；合成测量使用已解码的源图，112px／256px 各预热 5 次、采样 40 次，包含合成、PNG 编码与位图解码。
- 48 张独立位图在同一 640×480 DIP 区域内按 2 倍缩放离屏绘制，各预热 5 次、采样 60 次。
- `HomeThumbnailCache` 同时收到 12 个相同请求时只生成一张；随后 1000 次缓存命中不重新生成。首次等待包含组件原有的 80ms 延迟。
- 原生 `FastFileList` 展示 10000 个合成文件夹，直接复用原有可见区调度、取消、180ms 停留、18ms 请求间隔和 16 MiB 缓存。新旧 provider 都在后台限流到两路、从相同内存源字节解码，再分别合成。
- 先预热，再测旧／新／旧三轮。每轮包含快速跳转、停留和小步连续滚动，按当前屏幕缩放生成离屏目标。
- 滚动数据包含布局定位和 CPU 离屏光栅绘制，不是 GPU 呈现 FPS。合成微基准不含读盘／源图解码；列表 provider 包含解码，不含目录扫描、慢盘或 Quick Look 冷生成。

功能回归：`bash Tools/Testing/run-tests.sh fast`；新增用例位于 `Tests/MacExplorer.Tests/FrostedFolderPerformanceTests.cs`。

## 本机结果（Apple M5，2026-11-17）

三轮结果保存在 `outputs/folder-icon-performance/optimized-run1`、`optimized-run2` 和 `optimized-final`；第二轮反向运行。合成中位数：

| 输出尺寸 | 现有照片封面 | 新玻璃封面 |
| --- | ---: | ---: |
| 112px | 1.57–1.88ms | 1.92–2.17ms |
| 256px | 7.92–8.05ms | 10.44–10.60ms |

112px 单图解码像素为 49KiB，两者相同；原 Demo 固定 1024px 时为 4MiB。48 张缓存位图的离屏绘制中位数约 1.2ms，两者接近。12 个并发缓存消费者只生成一张；1000 次缓存命中没有增加生成计数。生成峰值为两路。

原生列表按测试机当前 1 倍缩放运行：新方案快速跳转 P95 为 10.79–11.25ms，旧方案不同阶段为 4.31–10.74ms；连续滚动分别为 4.94–6.95ms 与 1.98–6.39ms。运行顺序和布局状态存在明显波动，当前证据不支持“所有滚动场景零回退”的结论，也没有验证 GPU 呈现 FPS。

当前实际 Debug Demo 程序集另测得 112px 约 3.89ms、256px 约 20.48ms，见 `optimized-final/actual-demo-binary.json`；这与优化编译的合成基准分开记录。快速测试 1296 项通过、2 项跳过。
