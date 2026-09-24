---
name: batch-rename
description: Rename several files in one folder with the existing batch rename rules and an itemized approval preview.
---

# 批量重命名

1. 用 `file.list` 找到目标文件，确认它们位于当前活动窗格的同一目录。
2. 查询 `file.batch-rename` 参数，选择一个现有规则：`FindReplace`、`AddPrefix`、`AddSuffix`、`Sequence`、`Date` 或 `CaseConversion`。
3. 预览会列出旧名与新名；冲突或无变化时调整规则并重试，不要绕过预览。
4. 用户批准后执行，逐项报告成功、跳过和失败，并保留应用现有的撤销历史。
