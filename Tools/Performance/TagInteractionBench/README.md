# 标签交互 A/B 基准

使用同一测试程序加载不同构建的 `MacExplorer.dll`，运行实际 `FileTagService`。
每次通过隔离入口创建新数据库：20 个自定义标签、100,000 条关系、100 个选中文件；不读取真实用户数据库或 Finder 标签。

先保留优化前和优化后的完整构建目录（例如 `FastListBench.app/Contents/MacOS`），再编译一次：

```bash
dotnet build Tools/Performance/TagInteractionBench/TagInteractionBench.csproj \
  -p:HostDirectory=/absolute/path/to/before.app/Contents/MacOS
bash Tools/Testing/run-isolated.sh dotnet \
  Tools/Performance/TagInteractionBench/bin/Debug/net10.0/TagInteractionBench.dll \
  /absolute/path/to/before.app/Contents/MacOS outputs/tag-before.json
bash Tools/Testing/run-isolated.sh dotnet \
  Tools/Performance/TagInteractionBench/bin/Debug/net10.0/TagInteractionBench.dll \
  /absolute/path/to/after.app/Contents/MacOS outputs/tag-after.json
```

记录外部提交后的定义查询、100 次连续定义读取、100 个未变化文件的读取，以及完整多选快照。
旧版本没有批量 API 时执行原树菜单的逐文件读取及最终定义读取；新版本调用批量 API。
`changedRows` 为服务连接 SQLite `total_changes()` 的差值；`allocated` 为进程托管分配量。
输出包含被测模块 MVID。不要并行运行两个版本，也不要与其他性能负载同时测量。
这些数据用于隔离数据库/服务重复工作，不代表原生菜单显示延迟；菜单、页签及玻璃交互使用相邻 `FastListBench --interaction-only`。
