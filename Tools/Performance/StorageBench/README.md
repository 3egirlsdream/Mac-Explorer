# 磁盘迁移验证

只读打开指定的旧版数据库，用 SQLite Backup 创建一致副本。在副本上执行实际的搜索迁移、查询和空间回收；逐条计算搜索记录指纹，比较所有非搜索表（含配置、凭据、标签、收藏、AI 资料），并运行完整性检查。不会启动应用或扫描用户文件。退出时删除本次创建的数据库副本，只留下不包含文件名和资料内容的 `metrics.json`。

```bash
dotnet run --project Tools/Performance/StorageBench -p:SkipMacOSReleaseDMG=true \
  -p:SkipMacOSBundle=true -p:SkipMacOSNativeHelpers=true -- \
  "$HOME/Library/Application Support/MacExplorer/index.db" \
  outputs/storage-benchmark-20261023
```

对旧资料库只验证可重建索引退役、保留全部用户资料时，在上述命令最后追加 `--retire-legacy`，并指定旧库路径和另一处新的输出目录。此模式同样只修改本次创建的副本。

输出目录必须尚不存在。验证需要数据库副本和 SQLite 整理的临时空间，不能用磁盘几乎已满的设备运行；副本只在本机暂存。索引已升级的数据库不适用于旧版迁移基准。生产回收不会删除用户资料：旧数据库只移除列举的可重建索引，并保留完整的用户资料表；当前数据库在线仅回收 WAL。首次结构升级在窗口和数据库服务启动前完成迁移与压缩，后续主库压缩在正常退出后按空闲空间阈值执行。
