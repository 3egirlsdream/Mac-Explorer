---
name: organize-files
description: Review a folder, propose a clear grouping, then perform approved file moves.
---

# 整理文件

1. 使用 `file.list` 查看用户指定的目录。不要自行读取文件正文。
2. 根据文件名、类型和用户目标说明分类方案，用 `SaveClassificationProposal` 保存候选文件集的分组，并向用户展示每个目标目录及候选文件。
3. 对每次文件修改，先查询能力参数并调用预览工具。只有用户批准计划后才能执行。
4. 保留原有文件，不做永久删除；遇到同名冲突时停止并请用户选择。
