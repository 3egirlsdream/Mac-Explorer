# App Store 年度订阅

只在 `DistributionChannel=AppStore` 启用。客户端调用 Apple StoreKit 2，验证签名交易，不需要自建服务器或在应用内存放 App Store Server API 密钥。

当前商店 Bundle ID 为 `com.thankful.top.macexplorer.store`，年度订阅商品 ID 为字符串 `001`（保留前导零）。商店构建默认使用这两个值；测试可通过 MSBuild 参数覆盖，正式提审会核对 `release-config.json`。Apple 后台显示的数字 Apple ID 不参与 StoreKit 商品查询。

## Apple 后台配置

1. 在正式应用中建立一个订阅组，添加唯一的自动续订商品，周期一年。商品 ID 不可随版本改变。
2. 中国大陆目标价格为人民币 ¥9.9/年。先在 App Store Connect 确认可选价格点；如果没有这一价格点，重新确认价格，不擅自选取相近价格。其他地区配置本地价格。
3. 添加免费试用介绍优惠，周期为 1 周；关闭账单宽限期。试用资格由 Apple 判断，用户确认订阅后开始试用，之后自动续订。
4. 完成付费应用协议、税务、银行账户、销售地区、订阅本地化、审核截图及说明。使用 Apple 标准 EULA，在商店描述中提供条款及隐私政策链接。
5. 将实际 Bundle ID 和商品 ID 填入同目录 `release-config.json` 的 `appStoreBundleIdentifier`、`appStoreSubscriptionProductId`。构建同时传入相同的 `AppStoreBundleId`、`AppStoreSubscriptionProductId`，正式提交加 `StoreSubmission=true`。缺少或不匹配的配置会拒绝提审构建。
6. 首个订阅商品与应用版本一起提交审核。这里的构建和检查不创建商品、不上传、不提交审核。

## 运行规则

- 主窗口打开后以 ApplicationIdle 启动订阅服务；构造和启动首帧路径不查询 StoreKit。首次检查前暂时可用，首次检查之后不能通过重试重新获得临时权限。
- 权益检查与商品加载分别限时 15 秒。当前有效且验证通过的权益在网络失败时保留至原到期时间；无有效权益时失败即锁定，错误显示为暂时无法验证。
- 每 15 分钟、回前台、交易更新、到期时复查；同一检查合并。收到成功但为空的权益结果立即清除旧权限，不把退款误认为断网。
- 到期立即限制新增操作，随后后台确认续费；现有复制、移动、接收会话不因订阅锁定取消。无额外本地宽限期。
- `AppStore.sync()` 只供用户主动恢复购买使用。购买取消和待批准不会解锁，批准后的交易更新会触发检查。
- 无自建服务器意味着应用未运行或设备离线时不能即时获知退款，重新运行或恢复联网后更新。
- 本地测试模式不绕过订阅。无商品配置的商店 QA 包会显示验证失败锁定页；官网版不加载 StoreKit 动态库。QA 商品不能替代正式 App Store 商品。

## 验收清单

- 在 Apple Sandbox 测试新账户 7 天试用、无介绍优惠资格、购买取消、Ask to Buy 待批准、自动续费、关闭续订、到期、退款、恢复购买、换设备和离线。
- Sandbox 时间经过加速，按 Sandbox 实际续费周期验证到期事件，不等待真实一年。
- 使用 `bash Tools/Testing/run-isolated.sh <新构建应用的 Contents/MacOS/MacExplorer>`，不复用正常实例及真实目录。构建传 `-p:SkipMacOSReleaseDMG=true`。
- 检查浅色、深色、窄窗口、多个窗口、文件速递、已打开的菜单与快捷键；验证付费入口可键盘操作，锁定后无法发起新文件任务，已开始任务安全完成。
- 商品展示必须与 Apple 付款确认页的本地价格和优惠一致。缺少商品或验证失败时应能重试、恢复购买、管理订阅、打开隐私／条款和退出。
- 测量原版本和接入版本的首帧时间；本地构建／替身测试通过不等于 Apple Sandbox 支付通过或正式审核通过。
