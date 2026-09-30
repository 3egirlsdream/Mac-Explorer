---
name: summarize-files
description: Summarize one or more local text, PDF, image, or Office files after the user approves sending their content.
---

# 归纳文件

1. 查找包含特定正文的 PDF、图片或符合语义与元数据条件的文件时，先用 `file.search-index` 查询已有数据库，必要时用 `file.search-fields` 查看字段；按用户范围和真实扩展名过滤。无命中不能排除未分析或索引未覆盖的文件。找到一份即可满足的请求，不继续读取其他候选。
2. 用 `file.info` 核对候选路径和类型。需要正文时，对 `file.content` 使用 `PreviewOperation` 创建预览，再用 `ExecuteApprovedPlan` 请求执行，不能使用 `CallReadOnly`。有效数据库正文会自动复用；应用仍展示本次发送范围，未获批准就只用元数据回答。不要读取无关文件测试服务。
3. 正文按文本位置分页，每页正文最多 50 KB。每页记录要点；如 `HasMore=true`，用返回的 `NextOffset` 预览并申请下一页。大文件按需继续，不要因为第一页结束就声称读完全文；总结中说明实际覆盖范围。
4. 用户要求保存结果时，用 `file.create-text` 生成 `.md` 或 `.txt` 草稿，展示正文预览并在批准后写入。
