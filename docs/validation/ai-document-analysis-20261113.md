# AI 正文分析失败排查与修复（2026-11-13）

## 现有失败原因

只读查询本机数据库，正文索引状态为 Ready 123、Truncated 1、Failed 4。失败任务并非全部文档分析失败。

- 两个 Office 临时文件（`.~` 前缀，162 / 165 字节）不是 ZIP 文档，错误为 `End of Central Directory record could not be found.`。
- 两个 XLSX 被旧的 25 MB 文件大小上限拒绝。
- 截图中的 XLSX 为 40,186,306 字节，ZIP 条目 34 个，XML 共 46,013 字节；其中内嵌 OLE 附件为 39,932,416 字节。只读检查 ZIP 目录已完成，完整提取复测未完成。
- 旧流程仅缓存 Ready / Truncated，失败记录未保存有效文件指纹，目录刷新会反复生成相同失败任务。

## 修改

- 自动正文分析过滤 `~$`、`.~`、`._` 前缀的 Office 临时文件，直接索引入口也执行同一检查。
- Office 文件及总解压大小上限为 128 MiB；XML / relationships 单条目保持 12 MiB、累计 32 MiB、最多 2048 条目；正文仍限制 100 万字符和 2 万段。提取不读取二进制附件和图片内容，Open XML 解析器同时限制单个 XML 部件字符数。
- Copilot 复用同一 Office 资源检查，避免仍在外层被 25 MB 上限拒绝。
- 失败记录保存修改时间、大小与下次重试时间。相同指纹在 10 分钟内跳过，文件变化、冷却到期或显式重试可再次分析。冷却到期只在下一次索引触发时重试。
- SQLite v12 → v13 增加 `retry_after`，保留原有索引与正文；旧失败记录可在新版本重新分析。
- ZIP 格式错误显示本地化原因，任务详情保留异常；四语同步。

## 验证

- `bash Tools/Testing/run-tests.sh fast --area FeatureUpgrade`：39 / 39 通过。
- `bash Tools/Testing/run-tests.sh fast --no-build`：1260 通过，2 项按环境跳过，0 失败。
- `python3 -m unittest discover -s Tools/Localization -p 'test_*.py'`：7 / 7 通过。
- 新增回归覆盖临时文件、并发重复触发、服务重建后的失败冷却、冷却到期、强制重试、文件修复后立即索引、39 MiB 内嵌二进制附件、XML 解压与文件大小限制、v12 数据无损迁移与重复初始化。
- 真实 Excel 完整读取复测停在系统文件打开阶段，已终止本次等待进程；不将其计为提取成功。本轮未修改原文件及日常数据库，未替换或重启用户正在运行的应用。

测试结果目录：

- `/var/folders/sb/9mnn45q10077km6ydtcqkxtm0000gn/T/fkfinder-tests-40hwtpsa/`
- `/var/folders/sb/9mnn45q10077km6ydtcqkxtm0000gn/T/fkfinder-tests-wnb9_u3i/`
