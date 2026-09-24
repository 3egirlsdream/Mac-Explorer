---
name: summarize-files
description: Summarize one or more local text, PDF, image, or Office files after the user approves sending their content.
---

# 归纳文件

1. 先用 `file.info` 确定路径和类型。文件名、大小等元数据可直接查询。
2. 需要正文时，逐个对 `file.content` 创建预览，再请求执行；应用会展示文件路径和本次发送范围。未获批准就只用元数据回答。
3. 正文按文本位置分页，每页正文最多 50 KB。每页记录要点；如 `HasMore=true`，用返回的 `NextOffset` 预览并申请下一页。大文件按需继续，不要因为第一页结束就声称读完全文；总结中说明实际覆盖范围。
4. 用户要求保存结果时，用 `file.create-text` 生成 `.md` 或 `.txt` 草稿，展示正文预览并在批准后写入。
