# 索引写入对照

此工具在独立进程中运行实际 `SearchIndexer` / SQLite 索引，不启动用户界面、不读取真实用户目录。文件负载由 Python 子进程生成，索引进程使用确定的事件脚本，以便主分支和优化分支执行相同输入。真实 FSEvents 的分类、保存、重命名、删除及自身产物过滤由 `integration --area Search` 验证。

## 构建与运行

```bash
dotnet build Tools/Performance/SearchWriteBench/SearchWriteBench.csproj \
  -p:SkipMacOSReleaseDMG=true -p:SkipMacOSBundle=true -p:SkipMacOSNativeHelpers=true

python3 Tools/Performance/SearchWriteBench/prepare.py /private/tmp/fkfinder-index-fixture/files
bash Tools/Testing/run-isolated.sh \
  Tools/Performance/SearchWriteBench/bin/Debug/net10.0/osx-arm64/SearchWriteBench \
  /private/tmp/fkfinder-index-fixture/files outputs/search-write-run 1800
```

每次运行使用新的 fixture 路径和空输出目录。生成器创建十万个普通文件，其中一万个集中在单目录，九万个分布在千个目录；另有长中文路径、长文件名、符号链接、隐藏目录及应用包目录。大小写不同路径的索引语义由 SQL 回归测试覆盖，不能要求默认大小写不敏感的 APFS 创建同目录下仅大小写不同的两个文件。

第三个参数为空闲观察秒数，`0` 跳过，正式验收使用 `1800`。开始空闲计时前额外等待 11 秒排空默认诊断尾写。

## 测量场景

按顺序测量首次建立索引、一万文件目录无变化核对 50 次、单文件修改、文件增删／重命名、原子保存、目录重命名及子树删除、千目录事件积压、扫描期间变化和模拟日志／缓存／临时文件反复改写。校验删除后的子树不再可搜索，扫描期间的变化最终可见。

`report.json` 保存各阶段耗时、索引进程 CPU 时间、500ms 采样的进程驻留内存峰值，以及 macOS `proc_pid_rusage(RUSAGE_INFO_V2)` 的 `ri_diskio_byteswritten` 增量。该字段是操作系统报告的进程磁盘 I/O，不能称为 SSD NAND 写入量。数据库与 WAL 的文件大小不作为累计写入指标。

基准固定使用空拼音替身，以隔离扫描和数据库写入成本；真实拼音功能由 `SearchOptimizationTests.MacPinyinProducesCommonInitials` 等测试覆盖。这里的 CPU 时间不代表真实拼音转换、缩略图、正文分析和完整界面的总成本。数据生成子进程及系统其他进程的 I/O 不计入索引进程指标。

优化版 `before` / `after` 包含聚合计数，主分支不具备这些接口时为 `null`。峰值是本轮开始以来的进程高水位，不是单阶段额外占用，也不等于存活托管对象大小。先完成构建，再开始正式测量，活动阶段不要并行执行其他基准。

## 行变更观测与排除对照

在第三个参数后追加 `audit`，会为本次独立数据库添加条目插入、更新、删除的计数触发器，`rowsBefore` / `rowsAfter` 记录实际 SQL 行变更。此模式用于验证行数；它会增加写入和 CPU，**不得将其磁盘数据与无观测触发器的正式性能结果混用**。

追加 `exclude-churn`，仅在该进程的配置里排除 fixture 的 `churn` 子树。对照运行应使用相同代码及独立、初始内容相同的数据集；对比 `churn` 阶段，单列过滤事件、检查项、变更行及进程写入量。它模拟排除高频目录的额外收益，不改变产品默认排除路径，不能外推真实系统目录的 MB/天。

```bash
# 两种模式都必须使用新的 fixture 和输出目录
bash Tools/Testing/run-isolated.sh <SearchWriteBench> <fixture> <output> 0 audit
bash Tools/Testing/run-isolated.sh <SearchWriteBench> <fixture> <output> 0 exclude-churn
```

报告要记录基线提交、优化提交、实际工具源码／二进制校验值、运行时、场景原始数据、测试结果和限制。原始报告存放在项目 `outputs/` 中；自动全量仍由后续获授权的 GitHub Actions 执行。

所有运行完成后，用比较脚本生成 JSON／CSV 并检查无变化写入下降、积压范围及 30 分钟稳定性：

```bash
python3 Tools/Performance/SearchWriteBench/compare.py \
  <baseline-output> <improved-output> <audit-baseline-output> <audit-improved-output> \
  <included-output> <excluded-output> <comparison-output>
```
