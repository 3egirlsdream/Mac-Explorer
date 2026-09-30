---
name: batch-rename
description: Generate editable rename rule chains for selected local files and folders, including selections across directories, and open the professional preview workbench.
---

# 批量重命名

1. 默认使用工作区上下文中的 `selectionId`，它在本地保存整批选中项目。上下文仅提供前 20 项样本；不要将样本当成全部目标，也不要为了生成规则逐项读取正文或枚举全部选中路径。没有选中项时请用户先选择；含糊的命名要求在当前对话中澄清。
2. 将需求转换成按执行顺序排列的 `rules` 与 `options`，调用只打开预览的 `ui.batch-rename-dialog`。用户将在工作台编辑并点击重命名。不要在生成方案后继续调用执行能力。
3. 所有规则默认只作用于名称并保留扩展名。`target` 可为 `Name|Extension|FullName`；`appliesTo` 为 `All|Files|Folders`；`fileExtensions` 可填逗号分隔的扩展名，如 `jpg,png`。规则可用 `isEnabled:false` 暂停。
4. 规则参数：
   - `FindReplace`：`findText`、`replaceText`、`useRegex`、`caseSensitive`、`matchOccurrence`（0 全部，1 第一处，-1 最后一处；正则替换捕获组使用 `$1`）。
   - `AddPrefix` / `AddSuffix`：`prefixText` / `suffixText`。
   - `InsertText`：`text`、`placement`（`Before|After|AtPosition`）、`position`（从 0 开始，按完整字符）。
   - `RemoveText`：`position`、`removeLength`。
   - `Sequence`：`sequenceStart`（默认 1）、`sequenceStep`（默认 1）、`sequencePadding`（默认 3）、`separator`（默认 `_`）、`placement`、`position`。
   - `Date`：`dateSource`（`Current|Created|Modified|PhotoTaken`）、`dateFormat`（如 `yyyyMMdd_HHmmss`）、`placement`、`separator`。缺少拍摄日期会显示问题；仅用户明确要求时设置 `fallbackToModified:true`。
   - `CaseConversion`：`caseMode`（`Uppercase|Lowercase|TitleCase`）。
   - `Template`：`templateText`，支持 `{name}` 当前步骤名称、`{parent}` 所在目录、`{n:000}` 独立编号、`{created:yyyyMMdd}`、`{modified:yyyyMMdd}`、`{taken:yyyyMMdd}`、`{date:yyyyMMdd}`。编号参数和日期缺失策略同上。固定名称例：`项目_{n:000}`。
   - `Cleanup`：`trimWhitespace`、`collapseWhitespace`、`normalizeSeparators`、`separator`。
   - `Extension`：`extensionText`，可省略句点。仅明确要求时更改扩展名。
5. `options.sort` 为 `Input|Name|Created|Modified|PhotoTaken|Manual`，`descending` 默认 false；`restartPerDirectory` 默认 false。拍摄日期排序缺失时，默认显示问题；仅用户明确允许时使用 `photoSortFallbackToModified:true`。选择范围仅包含显式项目，文件夹只改自身名称。递归、表格导入、文件配对、脚本或按正文含义重命名不在首版规则范围内。
6. 示例：按修改时间排列并生成项目编号：
   `{"selectionId":"上下文中的 ID","rules":[{"type":"Template","templateText":"项目_{n:000}"}],"options":{"sort":"Modified","descending":false}}`。
7. 保留旧调用：`file.batch-rename` 接收显式 `paths` 与 `rules`，或旧单条 `rule`，两者不要同时提供。此执行能力仍需要审批预览；源项目变化、冲突和错误必须重新预览。逐项报告成功、跳过、失败及信息同步提示；成功项目以整批形式记录撤销。
