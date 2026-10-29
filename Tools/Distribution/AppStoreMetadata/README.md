# App Store 元数据

当前窗口恢复审核修复为 `1.0.62 (2026102901)`，详见 [整改与重新提交记录](1.0.62-review-fix.md)。原送审构建记录见 [1.0.62 发布记录](1.0.62-release.md)。中文介绍、推广文本和按数字顺序排列的更新日志分别保存在本目录；英文介绍为同步维护的本地文案，后台沿用现有简体中文商店本地化。

## 2026-10-16 App Store 审核整改

对应 Submission ID `1bb9a55c-48b1-40c8-a052-0dfc48e1c613`，原审核构建 `1.0 (2026100803)`。

## App Store Connect 字段

| 字段 | 简体中文 zh-Hans | 英文 en-US |
| --- | --- | --- |
| 名称 | 文件资源管理器 | 文件资源管理器 |
| 副标题 | 多标签、分栏与文件预览 | Tabs, Split Views and Previews |
| 宣传文本 | 全部文件管理功能需年度订阅（App 内购买）。多标签、分栏、搜索与预览帮助整理文件。价格和试用资格以 App Store 显示为准。 | All file management features require an annual in-app subscription. Organize files with tabs, split views, search, and previews. Price and trial eligibility vary. |
| 描述 | [中文完整描述](zh-Hans-description.txt) | [英文完整描述](en-description.txt) |

描述、宣传文本、副标题、截图、预览视频和订阅商品展示名称需要一起核对。已有其他商店本地化时，逐一同步名称和付费说明；不能留下旧 Mac Explorer 名称或暗示永久免费。当前应用界面提供简体中文、繁体中文、英文和法语；商店包的系统名称本地化仍声明简体中文与英文，商店产品页面沿用简体中文主语言。

订阅商品 ID 保持 `001`，商店 Bundle Identifier 保持 `com.thankful.top.macexplorer.store`。保留现有订阅组和应用记录，不创建替代应用。订阅商品展示名称建议为「文件资源管理器年度订阅」/「文件资源管理器 Annual Subscription」，说明全部功能需购买年度自动续订订阅。不要把有资格限制的试用写成所有用户必有的优惠。

## 截图与预览

每张展示文件管理功能的商店截图均标注「全部功能需年度订阅 · App 内购买」/「Annual subscription required · In-app purchase」。按用户最新要求，说明统一置于右下角，采用半透明小字；详情图为中文 16px、英文 12px、64% 不透明度（1440×900 画布），保留安全边距，不覆盖功能操作区域。推广文本与描述开头保留完整购买说明。

至少展示一张真实订阅页截图，能看到年度周期、StoreKit 实际价格、适用试用说明、恢复购买及隐私／条款入口。不得用本地 QA 商品获取失败画面或替身测试中的 ¥9.90 冒充实际在售商品。视频若展示付费功能，也同步标注需订阅。已通过当前登录的 App Store Connect 保存中文描述、推广文本、版权及副标题，并将原有 8 张截图替换为新版 9 张带双语购买说明的截图（功能总览首图 + 8 张详情）。原图仍在 Apple 素材库。英文描述作为本地草稿保留；后台英文名称校验冲突后撤销本轮新增英文记录，商店沿用原有简体中文主语言回退。

## 新构建

本机可先生成不含分发凭据的验证包：

```sh
dotnet build Tests/MacExplorer.Tests/MacExplorer.Tests.csproj -c Release -r osx-arm64 \
  --artifacts-path /private/tmp/fkfinder-appstore-review-20261016 \
  -p:DistributionChannel=AppStore -p:MacOSBuildNumber=2026101601 \
  -p:MacOSSigningIdentity=- -p:SkipMacOSReleaseDMG=true
```

正式提交沿用原 Bundle ID、正式签名及 provisioning profile。本次正式包营销版本为 `1.0`（仓库官网版本为 `1.0.57`），构建号为 `2026101601`；如该构建号已上传，后续打包必须递增。签名前生成 `InfoPlist.strings`，不能事后直接改已签名包内名称。正式构建已执行 `StoreSubmission=true` 预检，流程详见 [双渠道构建](../../../docs/distribution/mac-app-store.md) 和 [订阅配置](../SUBSCRIPTIONS.md)。

新包核对：Finder 安装名称、Dock／启动名称、菜单栏应用名称、关于、退出、设置关于页，在中文与英文系统中分别与对应商店名称一致。中英文系统均显示「文件资源管理器」，分别由 `zh-Hans.lproj/InfoPlist.strings` 和 `en.lproj/InfoPlist.strings` 提供。原生菜单保留系统服务、隐藏和退出行为；关于打开应用自己的设置关于页。订阅锁定时该页面仍受原有权益限制。

## 审核回复草稿

以下文字仅在新构建上传、元数据和全部截图实际更新后发送；本次工作不会代发审核消息：

> Hello App Review Team,
>
> We have addressed the issues in submission 1bb9a55c-48b1-40c8-a052-0dfc48e1c613 and uploaded a new build.
>
> For guideline 5.2.5, the App Store version no longer uses “Mac” in its product name. The product name is “文件资源管理器” in all supported system languages and on the App Store.
>
> For guideline 2.3.8, we aligned the App Store names with the installed application, launch display name, application menu, About page, and Quit menu. We replaced the framework's default About Avalonia entry with our application's About page. The bundle identifier is unchanged: com.thankful.top.macexplorer.store.
>
> For guideline 2.3.2, the description, promotional text, and every feature screenshot now clearly state that all file management features require an annual auto-renewable in-app subscription. Downloading the app does not include permanent free access. Trial availability and eligibility, when offered, are determined by the App Store. The in-app purchase screen displays the localized price, subscription period, renewal terms, Restore Purchases, Privacy Policy, and Terms of Use.
>
> Thank you for reviewing the updated submission.

## 依据

- [Apple 审核指南 2.3.2 / 2.3.8 / 5.2.5](https://developer.apple.com/app-store/review/guidelines/)
- [Apple 商标使用指南](https://www.apple.com/legal/intellectual-property/guidelinesfor3rdparties.html)
- [编辑 App Store Connect 应用信息](https://developer.apple.com/help/app-store-connect/create-an-app-record/view-and-edit-app-information)

最终命名：用户确认所有语言统一使用「文件资源管理器」。后台新增英文记录时，`File Explorer` 和「文件资源管理器」均遇到名称占用校验；已撤销本轮新增英文记录，保留原有中文主语言及名称。英文界面与包内英文 InfoPlist.strings 同样使用「文件资源管理器」。副标题使用功能介绍，不放“需订阅”；购买要求保留在推广文本、描述开头及全部截图中。

## 本次交付与验证

- 最新截图位于 `outputs/appstore-review-20261016/screenshots-v2/`：首张为内置 imagegen 生成的功能总览，结合真实分栏、文档预览和批量重命名界面，文案为「文件井然有序，工作更从容」；其余 8 张为真实功能界面，统一使用右下角半透明订阅小字。共 9 张 1440×900 JPEG。原始生成图、完整生成提示词、导出 HTML 与 SHA-256 清单均保留在该目录。
- 详情截图排版源文件为本目录 `screenshots.html`，首图导出源为 `hero.html`。详情继续沿用 `artifacts/store-2026-10-08/screenshots/2880x1800` 的 7 张真实界面原图，另保留本次隔离商店实例的真实 StoreKit 订阅页；旧版大字标注截图保留供回溯。
- 本地输出为 `outputs/appstore-review-20261016/`，包含截图、后台保存证据、原生订阅页、商店包审计与定向测试日志。真实 StoreKit 查询显示中国区价格 ¥9.90/年，当前测试账户具有 7 天试用资格；未执行购买验收。
- 全量检查：1384 通过、0 失败、7 个环境／渠道跳过；补充 UI 本地化／订阅检查 11 通过；商店版菜单、订阅与本地化定向检查 32 通过；Python 分发检查 5 通过。原生中英文环境均显示统一应用名称，菜单为“关于／退出 文件资源管理器”和“About／Quit 文件资源管理器”；两次退出正常。
- 正式提交包：`outputs/appstore-review-20261016/文件资源管理器-1.0-2026101601-AppStore.pkg`，arm64，使用原商店 Bundle ID 和商品 ID。应用签名为 `Apple Distribution: XinJi Jiang (AXBYDPHN88)`，安装包签名为 `3rd Party Mac Developer Installer: XinJi Jiang (AXBYDPHN88)`，签名、嵌入描述文件、沙盒与名称检查均通过。文件大小与 SHA-256 见同目录 `submission-manifest.json`，打包审计见 `submission-package.log`。
- 用户提供的 `FKFinder_Mac_Sandbox_Development (1).provisionprofile` 是设备受限的开发描述文件，不用于提交。本次使用下载目录已有的 `MacExplorer_App_Store_Distribution.provisionprofile`，已核对 Bundle ID、团队、签名证书、有效期及分发类型。
- 后续复核确认 Transporter 已交付 `1.0 (2026101601)`，已将后台旧构建替换为该新构建，更新审核备注的旧名称、购买要求与沙盒测试步骤。用户已自行发送整改回复；本助手未另发消息。首图图标已进一步修正为实际应用的紫红色图标，后台保留 9 张带右下角订阅说明的截图。
- 用户确认本次暂不在法国销售。已取消应用与年度订阅的法国供应，保留其他 174 个国家或地区；按标准加密、不在法国分发保存出口合规信息，解除提交阻碍。
- 2026-10-16 15:06（后台显示，Asia/Shanghai），已将 `1.0 (2026101601)`、年度会员 `001` 和订阅群组一起重新提交，三个项目均显示「等待审核」。原 Submission ID 保持不变。检查记录见 `outputs/appstore-review-20261016/resubmission-check.md`，成功凭证为同目录 `resubmission-success.jpg`。此前 ad-hoc ZIP 仅用于本地验证，不能作为商店上传包。
