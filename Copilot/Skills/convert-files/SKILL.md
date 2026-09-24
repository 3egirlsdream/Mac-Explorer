---
name: convert-files
description: Find an installed plugin command that matches selected local files and run the existing conversion workflow after approval.
---

# 转换文件

1. 用 `plugin.list` 查看已安装插件和命令；从能力目录查询对应的 `plugin.command:` ID 与参数。
2. 只对本地、实际存在、与插件匹配的文件创建操作预览。确认卡片必须显示输入文件、插件命令和生成位置。
3. 执行使用应用原有插件命令执行器。查看回执中的输出与警告；失败时报告插件错误，不猜测输出文件已经生成。
4. 插件未安装时可先查询 `plugin.market-list`；安装需另建 `plugin.market-install` 计划，向用户展示来源与校验信息并单独批准。
