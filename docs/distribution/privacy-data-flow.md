# 文件与网络数据流

当前渠道与发布说明见 [双渠道构建与发布](mac-app-store.md)。正式隐私政策位于 [官网页面](https://3egirlsdream.github.io/Mac-Explorer/privacy/)，本文件记录代码中的数据流。

应用配置、索引、收藏、标签、页签、对话历史和缓存保存在本机。网站版使用 Application Support / Caches；商店版由系统映射至自己的沙盒容器。不会将用户主目录误作应用数据目录。商店版只浏览系统选择器授予的位置，保存 security-scoped bookmark，支持移动路径恢复、离线重试和移除授权。移除授权不会删除文件；本地历史与索引可仍含之前记录的名称。

| 功能 | 数据与接收方 | 触发及控制 |
| --- | --- | --- |
| 本机索引、缩略图、LibRaw、OCR、PDF、内置转换 | 所选文件，本机进程及随包签名的帮助程序 | 本地处理；无云端 OCR 或遥测 |
| 照片地点名称 | 照片已有的 GPS 经纬度，Apple CoreLocation 地理编码服务 | 默认关闭；设置中说明接收方与用途，主动同意后才启用。Swift 在 CLGeocoder 入口检查可撤销凭证，缺失、失效或撤回均不联网；本地 GPS、坐标回退、OCR、分类、人脸、日期和相机功能保留 |
| Copilot | 用户文字、当前路径、选中名称、附件路径、工具搜索结果；接收方为用户配置的 Endpoint / Model | 首次发送明确许可；Endpoint、Model 或 API key 变化后重新许可；可在设置中撤回。每次 HTTP 发送及工具结果续传再次检查许可；读取/发送正文继续使用原有逐次审批 |
| Copilot 历史 | 用户文字、模型回复、工具过程，本地数据库 | 用户可在历史界面删除会话；API key 存入本机 SQLite 数据库（未额外加密），旧钥匙串密钥需重新填写 |
| SFTP | 主机、用户名、认证及用户主动传输的文件，用户配置的服务器 | 首次连接展示 SHA256 主机指纹，明确确认并保存成功后才进入认证；按真实主机/端口校验，密钥变化拒绝、不覆盖。可明确撤销指定主机信任。旧 JSON 密码迁移至本机 SQLite 数据库；旧钥匙串凭据不读取，需重新填写，失败保留旧配置与重试入口；私钥由系统选择器授权 |
| LocalSend | 别名、设备类型、证书指纹、网络地址及传输文件，局域网用户选择的对端 | 可关闭服务；接收确认可更改授权目录；HTTPS 指纹校验；IPv4 发现保留，直连/接收支持 IPv6 |
| 更新及插件市场 | 网站版的版本请求/插件市场请求 | 商店版在 UI 与服务执行边界禁止；网站版保持原功能 |

未加入广告跟踪、分析 SDK、账号收费或商店收据逻辑。LocalSend 的随机证书指纹用于设备身份及 TLS 校验，不从设备信号生成广告指纹。首次 AI 许可说明服务方按其政策处理数据；第三方 AI 接收方不是应用开发者的服务器。

Apple 当前 [required reason API 文档](https://developer.apple.com/documentation/bundleresources/describing-use-of-required-reason-api)明确列出的平台为 iOS、iPadOS、tvOS、visionOS、watchOS；本次是 macOS 桌面渠道，不套用移动平台的 reason code。文件时间、空间、UserDefaults、运行库以及第三方 SDK 的实际用途仍需在正式提交前按当时平台规则和扫描报告复核。尚未生成虚构的 PrivacyInfo.xcprivacy 声明，也未提交 App Store 隐私标签。

照片授权切换不会升级分析版本或使已有照片自动重分析。已有地点名称/坐标及其他结果仍保留在本地数据库；以后需要分析的记录按当时授权处理。撤回使旧辅助进程的凭证失效，重新授权不会恢复旧凭证。已经发出的 Apple/AI 请求无法撤回。

设置“关于”及 Copilot 首次许可提供政策入口。默认地址来自 `Directory.Build.props` 的 `PrivacyPolicyUrl`，指向官网正式政策；未配置有效 HTTPS 地址时打开随包的 [正式政策副本](../../Assets/PrivacyPolicy.txt)。发布方可通过同名 MSBuild 属性覆盖该地址。

本文件是代码数据流记录，不能替代正式隐私政策或 App Store Connect 隐私资料。商店提交时仍需核对各项处理说明和对应隐私标签，尤其是可选 AI 内容与照片 GPS 的用途。
