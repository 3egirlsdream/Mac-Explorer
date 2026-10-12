# 测试入口

日常验证用 `bash Tools/Testing/run-tests.sh fast`。入口会先增量构建，再运行全部 Unit 和共享组件 UI 冒烟检查；目标是本机已还原依赖后的增量流程不超过 15 秒。结果目录打印在终端，保留每个程序集的 XML、日志和包含构建/执行/总时长的 `summary.json`。

```bash
# 构建 + 日常检查；可把 15 秒作为可验证的整个流程预算
bash Tools/Testing/run-tests.sh fast --budget 15

# 当前二进制已由本轮构建更新时，直接运行
bash Tools/Testing/run-tests.sh fast --no-build

# 局部单元测试或页面交互（Area 可重复，取并集）
bash Tools/Testing/run-tests.sh fast --area Search
bash Tools/Testing/run-tests.sh ui --area FileList --area Sidebar

# 所有自动用例：Unit、UI、Integration、Stress；保留真实原生 helper 和 bundle
bash Tools/Testing/run-tests.sh full
bash Tools/Testing/run-tests.sh full --configuration Release

# 集成/压力场景；设备间手动互通需单独选择 manual
bash Tools/Testing/run-tests.sh integration --area LocalSend
bash Tools/Testing/run-tests.sh stress
```

`--no-build` 只用于当前代码已构建的情况。首次还原、干净构建和完整原生验证的时间另外取决于 SDK、编译与机器负载，不通过隐藏构建或跳过失败来满足预算。

## 分类与隔离

- `Kind=Unit`：纯逻辑，或使用显式临时目录/测试替身的服务行为测试。
- `Kind=UI`：Avalonia Headless 控件与页面交互。共享组件冒烟另标 `Smoke=UI`。
- `Kind=Integration`：真实网络、原生 helper、插件进程、系统元数据或后台索引调度。
- `Kind=Stress`：大规模真实文件、长期/大量重复操作。
- `Kind=Manual`：需要人工操作其他应用或设备的验证；`full` 不包含这些用例。

完整页面 UI、Integration、Stress 和 Manual 用例为显式测试。直接 `dotnet test` 默认只运行所属项目的非显式用例；推荐使用统一入口，确保两个测试程序集都被运行。发布 CI 使用 `full`。

`MacExplorer.UnitTests` 通过显式编译列表收纳不使用 dispatcher、全局语言服务或共享应用状态的测试，最多 4 个集合并行。其余测试保持串行和 Avalonia PerTest 隔离。不要直接对 UI 程序集打开集合并行或改成 PerAssembly。

旧 `FileListViewModelCreateTests` 的测试已按文件拆成各功能类，创建方法与替身复用 `FileListViewModelTestBase`。使用 `Area` 筛选需要考虑调用链；共享主题或输入样式变化应运行 `ui`，LocalSend/插件/native 改动应运行对应 `integration`，发布或公共基础设施变化运行 `full`。

每个测试进程都经过 `run-isolated.sh`，使用独立 `MACEXPLORER_TEST_ROOT`。平台/发行渠道不匹配的用例报告为 Skip；可选 SFTP、真实相机 RAW、签名 Store sandbox 验证仍需要原有环境。

## 构建与时间控制

日常入口设置 `SkipMacOSBundle=true`、`SkipMacOSNativeHelpers=true`、`SkipMacOSReleaseDMG=true`，不为逻辑测试重新打包签名；文件路径与访问检查所需的轻量 macOS 平台桥仍按输入/输出增量构建并复制，图片/文档等独立 helper 仅在完整检查中构建。完整检查保留 bundle 构建。原生目标按源文件、共享 helper、脚本及构建配置的输入/输出做增量判断。快速构建跳过常规分析器，仍运行必须的源码生成器；发布 CI 的干净完整构建开启全部分析器。

LocalSend 的发现时钟与网络重连延迟使用注入的 `TimeProvider`。重试场景推进虚拟时间，实际 HTTP/TLS 仍在集成组验证。首页动画也使用可推进时钟；侧栏悬停测试注入可取消调度并核对默认 600ms，取消后无需固定等待 700/750ms。RAW 排队使用明确的排队信号；插件崩溃由进程退出通知结束 RPC，不能让超时冒充崩溃处理成功。

文件覆盖、取消后完整内容、路径越界、证书/指纹与资源回收的检查继续保留。有限 watchdog 在正常完成时立即返回；无需为了“减防御代码”删除清理或文件安全测试。

## 本机验收（2026-10-12）

- 自动全量：1362 个用例，1355 通过、7 个环境/渠道 Skip、0 失败；执行约 119 秒（原来约 185 秒）。
- 日常入口：734 个用例，732 通过、2 个渠道 Skip、0 失败；执行约 6–9 秒。一次测试源码增量构建 4.44 秒，加执行 6.88 秒，总流程 11.31 秒，通过 15 秒预算。
- 触发产品程序集重新编译：分析器优化后构建约 10.17 秒，整个流程约 18.72 秒，仍超过 15 秒；此前约 21.93 秒。预算检查会如实失败，不隐藏这部分成本。
- UI 按 FileList + Sidebar：109 个用例全部通过，执行约 6.39 秒。Search 日常选测：57 个用例全部通过，约 1.71 秒。
- 移除测试目录中的三个独立 helper，并指定不存在的应用/插件 fixture 路径时，日常入口仍全部通过（保留轻量平台桥）；证明它不依赖旧 bundle 或这些 helper。
- 主要慢场景：LocalSend 重试 35.89 → 0.67 秒，8 台设备上限 10.35 → 0.44 秒；RAW 排队 3.19 → 0.14 秒；动画循环 3.80 → 0.01 秒；插件带子进程崩溃 5.22 → 0.39 秒。

以上为单机样本，运行时间会受编译缓存和机器负载影响。15 秒适用于日常执行及缓存增量流程；首次构建、产品重编译和真实原生全量检查不能保证在 15 秒内。
