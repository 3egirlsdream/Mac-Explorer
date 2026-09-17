# Mac-Explorer 搜索优化实现

## 基线与交付范围

- 分支：`feat/list-row-and-hash`
- 基线提交：`cd592a5936509d64fdf4933f06a5161e624a7bd5`
- 目标：现有 `net10.0` / Avalonia `12.0.4` / `osx-arm64` 项目。
- 交付：统一查询语义、独立于浏览缓存的持久化搜索目录、后台扫描、FSEvents 增量更新、搜索快照及取消保护、回归测试。
- 未复制 TDS 代码，也未引入 NTFS、USN、Win32、System.Drawing 或新的 NuGet 包。
- 没有加入 Spotlight、多音字词典、文档正文解析、LZ4 缓存或全量内存搜索引擎。它们不是本次补丁的前置条件。

本补丁是实际源码修改，不是伪代码。但交付环境没有 .NET SDK，且不是 macOS；C# 编译、xUnit 和原生接口测试尚未执行。下文明确区分已运行验证与待运行验证。

## 1. 架构与文件职责

```text
SearchViewModel / SearchOmniboxProvider
                |
          MacSearchService
                |
       SearchQuery + SearchOptions
                |
           SearchCatalog  <-- SearchIndexer <-- MacSearchChangeSource
                |                 |
    同一个应用 SQLite 数据库     MacPinyinInitials
```

`SearchCatalog`：所有 SQL 在后台线程执行，读操作最多两个并行，索引写操作单线程提交；每次操作使用独立连接，通过 sqlite3_interrupt 中断正在执行的 SQL，不占用旧目录缓存的读锁。

`SearchIndexer`：一个工作任务处理根目录扫描和变更归并，每批最多 256 项；每个根目录最多排队一次，脏路径超过 512 个时合并为整根补扫，不丢弃变更。不会为每一个关键词启动一轮独立递归扫描。

`MacSearchChangeSource`：每个索引范围持有自己的 FSEvents stream 和串行 dispatch queue，独立于当前标签页的浏览刷新监听；回调处理标志、事件编号和路径别名映射，退出时先停止、排空回调，再释放原生资源。

`MacPinyinInitials`：通过 CoreFoundation 的 Han-Latin 转换生成按字的拼音首字母，对已转换汉字缓存结果；索引时预计算，查询不反复转码。

## 2. 为什么新增搜索表，而不把搜索目录塞回 files

旧 `files` 表同时充当文件浏览缓存，其失效流程会删除目录记录。这样无法把它的存在等同于“全局搜索范围已经完整建立”。本次继续使用原来的数据库连接工厂与同一个 SQLite 文件，但将可重建的搜索数据独立成三部分：

| 表 | 作用 |
| --- | --- |
| `search_entries` | 原始路径/名称、大小/时间/类型、规范化搜索字段、拼音首字母、扫描代次 |
| `search_names` | 外部内容 FTS5 trigram 候选索引，以及配套维护触发器 |
| `search_roots` | 最近已处理的事件编号、范围状态和更新时间 |

不修改主数据库 v9 的迁移编号，不删除或重建用户的标签、收藏、评分与设置，也不删除旧 `files` 表。新表在首次使用时初始化。唯一冲突更新保留记录 ID，避免每轮目录扫描无谓更换 FTS rowid。

FTS5 trigram 只产生候选：SQL 仍对目录边界、隐藏项、所有关键词和扩展名做精确判断，然后才排序、LIMIT。查询不足三个 Unicode 字符时走相同的字面匹配，不把短中文当作无结果；系统 SQLite 不支持 trigram 时也可退回精确 SQL。数据库损坏、权限错误和锁错误不会被伪装成空结果或触发删库。

旧 `IFileIndex` 接口仍服务浏览缓存与兼容代码。本次生产搜索入口由 App 强制注入新的共享 `MacSearchService`，不会走旧的全库取固定候选再过滤范围的逻辑。`SearchOmniboxProvider` 只传入旧 IFileIndex 的兼容构造方式被保留，以免破坏既有测试/调用；它不是新搜索的生产入口。

## 3. 查询语法与结果语义

| 输入 | 含义 |
| --- | --- |
| `合同 2026` | 多关键词 AND；两个词都需要匹配 |
| `"final version"` | 带空格的完整字面短语 |
| `ext:pdf 合同` | 扩展名为 PDF，同时匹配合同 |
| `ext:pdf,png` | 扩展名属于列举集合 |
| `path:"My Projects" config` | 父路径包含 My Projects，名称匹配 config |
| `ht 2026 ext:pdf` | 启用首字母匹配时，可匹配“合同2026.pdf” |
| `holiday sunset` | 名称查询优先；AI 补充查询允许名称与已有 AI 标签共同满足各关键词 |

普通查询内容按字面处理，`%`、`_` 不是 SQL 通配符。双引号用于短语，`\"` 表示字面双引号，`\\` 表示字面反斜杠；编辑中的未闭合引号可继续搜索。未知操作符视为普通文字。仅输入空的 `ext:` / `path:` 不会导致全目录查询。

名称匹配使用 NFC + invariant 大写搜索键；路径身份、路径范围和去重仍使用原始大小写敏感字符串。不会把 `Report.pdf` 和 `report.pdf` 合并为同一条。这里区分了“名称不区分大小写匹配”和“文件身份”。

AI 搜索只使用已存在的 `ai_tags` 记录，并关联已建立的搜索条目。每个词可由名称、拼音首字母或标签满足；目录及扩展名约束仍在 LIMIT 前生效。名称匹配优先展示，AI 结果随后补足且去重。不会因一次查询主动读取/分析文件内容。

两个新配置键复用现有 ISettingsService（没有新建设置页）：

| 配置键 | 默认值 | 作用 |
| --- | --- | --- |
| `SearchPinyinEnabled` | `true` | 控制查询是否匹配预计算首字母 |
| `SearchIncludeAiTags` | `true` | 控制是否补充已有 AI 标签匹配 |

继续沿用 `HideSystemFiles`、`HideDotFiles`、`HideDotFolders`。拼音是按字首字母，不保证词语级多音字识别，不是完整全拼搜索；中文覆盖由当前 macOS 的转换能力决定。

## 4. 索引范围、启动与更新

首个窗口打开后延迟启动 `/Applications`、`/System/Applications`、用户主目录的后台预热；其他本地范围在首次搜索时登记。已有父范围且索引策略确实覆盖子范围时复用监听。首次未索引的范围可能先返回部分/零条结果，随后后台补全。

默认遵循现有 `IndexConfiguration.ExcludedPaths`，修正到路径边界判断。扫描不递归 `.git`、`.svn`、`.hg`、`node_modules`、`.cache`、`.gradle`、`DerivedData` 及系统索引/回收站目录。默认不递归用户 `~/Library`，也不钻入代码列明的 `.app`、照片库、framework、插件、虚拟机等包。直接将被排除位置选为搜索根目录可以显式进入，但该根下面其他排除规则仍适用。

不跟随树内的符号链接；显式选择的根目录可为别名。监听使用 realpath 定位实际目录，再把 FSEvents 路径映射回请求根（例如 `/private/tmp` → `/tmp`），不修改文件名大小写。显式符号链接根在应用运行期间改指向另一个目标时，应重启或调用 Refresh 重新建立监听；本次没有额外监视所有符号链接祖先。数据库自身及其 WAL/SHM/journal 文件不进入索引或变更任务，避免自触发循环。

扫描只读取目录项和必要元数据，不计算哈希、缩略图、文件正文。某目录全部成功枚举后，才清理该目录已经消失的记录和已删除的子树；目录被普通文件/符号链接替换时也清除旧子树。权限不足或扫描失败会保留旧记录并标记 Partial/Unavailable，而不是以空目录误删。

FSEvents 在扫描前启动；变更合并为父目录刷新与必要的子树补扫。MustScanSubDirs、UserDropped、KernelDropped、EventIdsWrapped、根移动和卷挂载变化均进入恢复路径。事件编号只有在对应工作成功写入并完成目录核对后才持久化；卷变化或编号回绕会重建监听并作废旧游标。

**重启策略保守：每个本次会话启用的根目录先进行完整核对，已有数据库可立即用于查询。不是只依赖历史游标实现的“免扫描秒级启动”。** 这覆盖进程关闭期间及历史记录不可用的变化；全量核对时不重复把历史回放当成第二次全量扫描。运行中的正常变化采用局部刷新。

Unavailable 根目录在后续查询时最多按 30 秒间隔重试。Partial（例如存在受保护系统目录）不会因为用户持续输入而反复全盘扫描；可由 `SearchIndexer.Refresh(root)`、原生恢复事件或重启触发完整核对。当前没有新增索引管理/刷新设置页；授权变化后可重启应用重新核对。

多个根目录可能重叠，扫描由同一工作任务串行执行，数据库按原始路径去重；大范围排除的内容可能已由另一显式范围收录，因此“全局”查询可返回这些已收录条目。

## 5. 界面、取消与远程搜索

普通窗口搜索支持索引快照：在建立期间分批更新，在状态栏显示仍在补全/部分未更新/位置不可访问。列表上限为 500，取额外一项判断是否还有结果。稳定完成后结束本次查询会话；本补丁不把已结束的搜索页改造成持续订阅全部文件变化的 live query。

SearchViewModel 保留 120ms 输入防抖，CTS 由创建它的查询释放；每次请求带代次检查，即使旧 provider 不响应取消，也不能覆盖新结果/状态。新查询等待期间可短暂保留旧列表，直到新快照替换，减少闪烁。

Omnibox 保持 40 项的快速单次查询和现有范围选择。不会等待整个磁盘建索引；后台新增结果在下次建议查询时可见，**本补丁没有给 Omnibox 增加持续推送或独立的索引状态行**。需要观察首次索引的实时补全时，使用普通窗口搜索。

非本地路径仍使用 IFileService 分批枚举，复用同一查询模型，不混进本地 SQLite。远程查询不跟随符号链接，隐藏规则生效，并跳过 `.git` / `node_modules` 子目录。远程连接和大目录的响应仍取决于既有 provider，不声称具有本地索引延迟；其状态文案明确为扫描而非持久化索引。

## 6. 已运行验证与待运行验证

交付环境为 Linux，没有 .NET SDK/compiler；网络限制使完整 Git 克隆和 SDK 下载不可用。本次通过 GitHub 连接按固定提交取得所有被改写的旧文件，并核对其 Git blob SHA。完整新代码、测试和实际统一 diff 已生成，但未在该环境编译应用。

### 已运行

```sh
python3 Tools/Performance/verify-search-sql.py
```

在 SQLite 3.46.1 上，18 项测试通过，包括 720 组随机 Unicode/标点子串的 trigram 候选与字面匹配一致性对比。该脚本直接提取 C# 源码中的建表、触发器和 UPSERT SQL 常量；查询构造部分由 Python 镜像实现。它验证 SQLite 行为，**不是 C# 单元测试，也不验证 .NET 与原生互操作**。

覆盖：范围及隐藏过滤先于 LIMIT、大小写不同路径、短中文、字面通配符、首字母及扩展名/路径组合、名称与 AI 组合、FTS 同步/完整性、稳定 rowid、删除/替换子树、未完成扫描保留数据、已有数据上补建 FTS、无符号 checkpoint 和用户表保留。

### 已提供但未运行

`Tests/MacExplorer.Tests/SearchOptimizationTests.cs` 含查询、数据库、取消、深层目录变更、事件恢复、路径别名、原生首字母及旧请求不能覆盖新结果等 xUnit 测试。

请在项目原有的 macOS/.NET 10/Xcode 构建环境执行：

```sh
dotnet build MacExplorer.csproj -c Release
dotnet test Tests/MacExplorer.Tests/MacExplorer.Tests.csproj
```

测试项目仍使用仓库原有的 macOS RID 和 xUnit v3 配置，没有替换测试框架。不要将 Python 验证通过视为完整应用构建已通过。

### macOS 人工验收

1. 首次启动，在全局/当前文件夹/自定义范围中连续输入短词、多词、中文、首字母；验证状态提示与取消响应。
2. 在 3 层以上目录新建、删除、重命名、整目录移动；确认搜索最终一致且旧子树消失。
3. 用 `/tmp`、`/private/tmp`、显式符号链接根和大小写敏感 APFS 卷测试监听及路径身份。
4. 关闭应用后变更文件再重开；测试没有读取权限的目录、恢复授权、外置卷移除与重连。
5. 扫描期间关闭全部窗口/退出应用，检查无崩溃、死锁和访问已释放 delegate。
6. 对 10 万/100 万真实目录项测冷启动、首批/P95 查询延迟、内存和磁盘占用、UI 帧率。当前没有这类实机性能结果，不承诺固定耗时或倍数。

## 7. 应用补丁与回退

先保存当前未提交修改。以下命令在仓库根目录执行，diff 路径按实际保存位置修改：

```sh
git switch feat/list-row-and-hash
git switch -c feat/search-index-v2
git apply --check /path/to/Mac-Explorer-search-optimization.diff
git apply /path/to/Mac-Explorer-search-optimization.diff
```

本补丁针对上述 cd592a5 基线。分支继续变化后，先检查而不要强制覆盖。补丁包含完整 Git blob index 信息，但三路应用仍需要本地仓库中存在相关对象。`git apply` 不会自动提交，也没有远程推送。

尚未继续修改这些文件时，可以检查反向补丁后回退：

```sh
git apply --reverse --check /path/to/Mac-Explorer-search-optimization.diff
git apply --reverse /path/to/Mac-Explorer-search-optimization.diff
```

回退源码不会删除新增的派生搜索表；旧代码忽略这些表即可。不要为了清理搜索索引删除整个 index.db，因为里面还有用户数据。

## 8. 主要参考

- Microsoft.Data.Sqlite 异步限制：https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async
- Microsoft.Data.Sqlite 原生句柄互操作：https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/interop
- SQLite FTS5 / trigram / external-content：https://www.sqlite.org/fts5.html
- Apple FSEvents 使用和丢事件恢复：https://developer.apple.com/library/archive/documentation/Darwin/Conceptual/FSEvents_ProgGuide/UsingtheFSEventsFramework/UsingtheFSEventsFramework.html
- Apple FSEventStreamSetDispatchQueue：https://developer.apple.com/documentation/coreservices/fseventstreamsetdispatchqueue(_:_:)

这些文档支撑 API 与恢复策略选择，不替代实际编译和 macOS 回归。
