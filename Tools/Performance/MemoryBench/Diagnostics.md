# 内存保护与现场日志

GitHub 版运行日志位于 `~/Library/Application Support/MacExplorer/Logs/memory-*.jsonl`。商店版通过 `RuntimePaths.LogDirectory` 写入容器中的 Application Support；隔离测试写入 `MACEXPLORER_TEST_ROOT/.macexplorer/MacExplorer/Logs`。MemoryBench 将同样的诊断日志写入结果目录的 `diagnostics/`。

每次启动创建独立会话，退出前尽量排空写入队列。每 30 秒记录一次进程内存，首条在启动约 5 秒后。每文件最多 2 MiB，保留最近 8 个文件，写入队列最多 128 条。事件超过队列容量时丢弃并计数，不阻塞操作线程。日志只记录尺寸、数量、类型与内存量，不包含文件路径、搜索词、文件内容或凭据。

| 事件 | 主要用途 |
| --- | --- |
| `session.start` | 应用版本、Avalonia、运行时、系统、架构、发行渠道和 PID |
| `memory.sample` | macOS physical footprint、RSS、托管存活量、GC 堆／碎片／提交量、累计分配、GC 次数、线程及解码活动 |
| `memory.ui` | 窗口、页签、各类缓存／搜索历史／任务的实例数、条目数、估算字节与待处理数 |
| `image.decode_rejected` / `image.large_decode` | 压缩文件尺寸、源／目标像素尺寸、格式与估算解码工作集 |
| `image.raw_decode` / `image.helper_failed` / `image.helper_cancelled` | RAW 回退和原生 helper 的活动与失败 |
| `search.history_trimmed` / `search.history_reloaded` | 结果快照淘汰与返回搜索时的重新查询 |
| `task.finished` / `task.retry_state_released` | 失败类型、断点状态容量、重试状态淘汰 |
| `lifetime.native_nodes_released` | 永久关闭页面／窗口后的原生无障碍节点释放 |

`highMemory` 在 footprint 达到 2 GiB 或一次采样增长至少 256 MiB 时标记。所有字节量均以 bytes 为单位；`allocatedBytes` 是累计分配量，正常情况下也会不断增长，不代表泄漏。`heapBytes`、`committedBytes`、`fragmentedBytes` 是最近一次 GC 的信息；只有 `managedBytes` 是采样时的托管存活量估计。RSS 与 physical footprint 不等价，截图中的 macOS 应用内存更应结合后者判断。生产日志不会强制 GC。

再次发生异常时，保留上述日志文件，并记录反馈时间、截图、应用版本和大致操作。可结合 `vmmap -summary <PID>` 与 `sample <PID> 3` 的现场输出分析：托管堆持续增大时检查历史、任务和未回收的页面；footprint 增大而托管堆平稳时检查像素、原生文档／渲染资源。无需先强制 GC 或重启，否则会丢失现场。

## 当前保护策略

- 栅格图片先读 codec 头，按最长边等比缩小且不放大，最长边不超过 2048。两个共享解码槽，每槽估算工作集最多 128 MiB；估算包含源像素、输出副本和编码数据。不能在预算内解码时保留图标／占位。原生 codec 还可能分配内部缓冲区，因此这不是整个进程的硬内存上限。RAW 保留相机大图支持，但直接从 LibRaw RGB 缓冲区采样到目标 RGBA，去掉全尺寸 RGBA 中间副本。
- 每页签最多 100 条导航记录，8 份／4 MiB 搜索结果快照，每份最多 500 项。快照复制纯数据以避免持有原文件对象上的 UI 事件订阅；淘汰快照后保留查询、范围和选择锚点，返回时重新搜索。
- 最多保留 8 个失败任务的重试上下文，断点／清单数据估算合计最多 32 MiB、15 分钟后由维护周期清理。对象图估算有遍历上限，不能安全估算时也释放。任意执行闭包和异常内的对象不能精确计费，所以数量和时间限制同时生效。超限任务保留错误摘要、禁用原重试，清理任务自有临时状态；已经提交的文件保持不变。暂停、等待、正在执行的任务不按失败策略淘汰。
- 缩略图失效时同步删除淘汰索引，清空与插入使用同一把锁，防止持续浏览时索引累积或字节计数漂移。
- 失败摘要最多 100 条，另保留至多 8 条可重试失败；摘要限制文件明细与警告条数、文本长度并释放源路径集合。完成／取消摘要仍保留最近 100 条。Git 状态缓存限制为 16 个仓库／估算 8 MiB，并由空闲维护清理。

日常回归：`bash Tools/Testing/run-tests.sh fast`，其中 `MemoryBoundsTests` 覆盖细长图片、超大 PNG 头、历史回查、失败任务回收、日志轮转与采样。另运行 Thumbnail / Search / FileTasks / FileList 页面测试、Thumbnail 原生集成测试及本目录的真实 macOS MemoryBench。Headless 的位图后端不能验证真实像素内存，需结合原生运行验证。
