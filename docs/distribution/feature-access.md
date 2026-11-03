# 商店版体验与功能权限

商店版首次进入可用主界面后提供连续 168 小时完整体验，无需确认订阅、不自动扣费；到期继续免费基础版。官网版保持原有能力，第三方插件授权独立管理。

| 免费基础功能 | 完整体验或订阅解锁 |
| --- | --- |
| 本地浏览、多页签、页签排序和批量关闭、现有视图模式 | 分栏 |
| 普通复制、移动、删除、重命名、压缩解压、外部拖放和 Finder 剪贴板 | 批量重命名 |
| 页面文件搜索 | Cmd+K 全局搜索、OCR／内容检索 |
| 预览、收藏、标签及嵌套管理、颜色标签别名 | AI 分类、Copilot |
| 系统已挂载卷访问和弹出 | 应用内网络发现、连接和配置管理、SFTP 操作，包含 WebDAV |
| 任务查看、暂停、继续、取消、已有记录的撤销 | 文件速递及其暂存区、投放和入口管理 |
| 默认外观、浅深色、字号、交互颜色、背景缩略预览 | 应用或修改背景图案、自定义图片、图案配色和强度 |
| LocalSend、隔空投送、SVG 预览及性能／稳定性修复 | — |

未列入高级清单的现有功能保持免费，不按复制次数或文件数量收费。

## 生命周期

- `SubscriptionService` 仅拥有 Apple 权益和商品。`FeatureAccessService` 合成应用体验与有效 Apple 权益；查询只读取内存位标记，无锁、无分配、无 I/O。
- `AppTrialStore` 在现有 SQLite `app_settings` 中事务初始化 `app_trial_v1`。首次时间和到期时间固定，重启、升级及购买均不重置；损坏记录不自动覆盖，失败会提示，基础功能继续可用。
- 首次 Apple 检查识别为有效订阅或 Apple 试用时，记录无额外应用体验资格；未订阅时开始的应用体验始终按原期限倒计时，期间购买、取消或退款不重置该期限。
- 体验欢迎、最后一天和体验结束提醒由应用级服务记录并去重，后台到期仅更新状态，前台窗口显示提醒。商品加载失败不阻断应用体验。
- 原全局订阅遮罩不再随权益状态自动出现；高级入口按需打开 `AppWindow.SubscriptionHost`，Escape 或“继续基础版”均可关闭。
- 背景失去权限后仅停止显示，保留图片、配色、强度与用户启用意图；重新解锁恢复，主动关闭后不恢复。文件速递保留暂存及偏好，允许清空，不接受新投放；进行中拖出安全结束。
- 不主动卸载系统卷或断开已接受任务；已有挂载卷从侧栏直接按普通目录访问，不重复执行连接。后台 SFTP 自动重连在免费状态下停止新尝试，已授权任务使用自己的连接租约。

## 执行与性能边界

UI 入口请求对应升级页，服务入口作最终检查。任务按类型和远程源／目标在入队时确定所需权限；执行上下文只保存该任务已获准的功能，不能以存在任意任务上下文作为通用绕过。等待和暂停中的已接受任务继续完成，失败重新发起需重新检查。

任务检查点和文件逐项循环不查询订阅；绘制、滚动、缩略图及拖动刷新不查询权限。非文件分析批次与连接操作使用独立、限定功能的异步授权范围，不改变原任务列表。撤销仅从当前进程中已验证的历史记录取得对应功能授权，保留文件身份和依赖检查。

## 验证入口与发行条件

```bash
bash Tools/Testing/run-tests.sh fast
bash Tools/Testing/run-tests.sh ui
bash Tools/Testing/run-tests.sh integration --area Network --area Clipboard --area FileDrop
dotnet build MacExplorer.csproj -c Debug -p:SkipMacOSReleaseDMG=true
MACEXPLORER_FEATURE_ACCESS_SMOKE=1 bash Tools/Testing/run-isolated.sh \
  'bin/Debug/net10.0/osx-arm64/Mac Explorer.app/Contents/MacOS/MacExplorer'
```

原生冒烟仅在 Debug、独立测试根目录及显式开关同时存在时生效。使用真实 Avalonia／macOS 窗口、真实隔离 SQLite 和文件；Apple 商品与交易为明确标注 `TEST` 的替身，**不能作为真实 StoreKit 支付验收**。结果写入隔离目录的 `feature-access/report.json` 和截图。

发布前另行执行 `full`、真实 StoreKit Sandbox 验收，并在 App Store Connect 移除新用户介绍试用优惠；保留已有 Apple 试用。同步后台文案和截图并重新提审。本地素材模板已改为基础免费／高级订阅，历史上传图片与旧审核回复不代表新版本已上线。
