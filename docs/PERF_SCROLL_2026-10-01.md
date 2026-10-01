# 快速滚动性能修复（2026-10-01）

进入目录的修复之后，快速滚动仍能复现两个开销：新文件名反复查找字体，以及玻璃侧栏在背景像素未变化时替换快照、重新运行滤镜。本次保留字号、字重、文件图标、缩略图和玻璃材质参数，在各自资源与快照管理处修复。

## 定位与修改

- `FontFamilyUi` 的首项 `System Font` 不是已安装字体名称，Avalonia 正式的默认字体标识是 `$Default`。原配置最终解析为 PingFang SC；不匹配的请求名称导致反复进入平台字体创建路径。15 秒 EventPipe 采样定位到 `FastFileList.Texts -> TextLayout -> FontManager`，关闭缩略图仍能复现。单独对同一进程的两种配置测 100 次主字体查询，约 78.4ms → 0.08ms。
- 直接配置现有的 PingFang SC 和 Apple Color Emoji，保留 Segoe UI／Helvetica Neue／Arial 后备项。实际中文、拉丁字符主字体仍为 PingFang SC，emoji 仍为 Apple Color Emoji。没有改动字体大小或字重。默认字体标识可核对 [Avalonia 12.0.4 FontFamily](https://raw.githubusercontent.com/AvaloniaUI/Avalonia/12.0.4/src/Avalonia.Base/Media/FontFamily.cs)。
- Avalonia 的 `SceneInvalidated` 通知使用整个窗口的矩形，原玻璃代码据此强制发布快照，滚动被排除的文件区域仍会触发侧栏滤镜重算。该行为可核对 [Avalonia 12.0.4 CompositingRenderer](https://raw.githubusercontent.com/AvaloniaUI/Avalonia/12.0.4/src/Avalonia.Base/Rendering/Composition/CompositingRenderer.cs)。现在在已有完整像素拷贝上与上一张快照逐字节比较；相同则保留原图及滤镜缓存，任一像素变化仍发布新快照，不依赖 8×8 采样判断细小变化。
- 同时让祖先已隐藏的玻璃控件退出采样范围，并忘记它们旧的布局记录，重新出现时及时采样。隐藏祖先回归在旧库上复现失败，在修复后通过。

## 对照方式

完整 App 主窗口，arm64 Release，1100×760 DIP。隔离目录含 10,000 个真实项目：文件夹、240×160 PNG、文本各约三分之一，名称包含中文、重音拉丁字符和 emoji。等待搜索索引 Ready，在本次隔离配置中关闭首次图片 OCR，使用实际缩略图服务。每 8ms 请求推进 3.5／120／600px，各 120 步、两遍，列表／图标／树形各测缩略图开关，共 36 组。第二遍可能超出正常的 1,024 项文字缓存，仍需重新排版。

字体 A/B 使用同一二进制、只覆盖字体资源；另以字体已修复的版本对照玻璃修复。正式对照无并发构建、测试或采样。早期诊断发现基准进程未处理后台更新工作参数，误启动另一轮合成测试；已补齐入口，受影响计时弃用。正式数据保存在 [scroll-2026-10-01.csv](../Tools/Performance/FastListBench/results/scroll-2026-10-01.csv)，四轮各 36 条，`run` 区分 font-before／font-after／glass-before／final；前两轮未记录玻璃计数，相关单元格为空。

下表为启用缩略图、每步 600px 时，两遍 p95 的中位数，单位 ms：

| 视图 | 修复前绘制 CPU p95 | 最终绘制 CPU p95 | 修复前 UI 心跳 p95 | 最终 UI 心跳 p95 | 修复前动画回调 p95 | 最终动画回调 p95 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 列表 | 13.72 | 1.71 | 20.28 | 9.80 | 26.02 | 17.41 |
| 图标 | 23.78 | 5.34 | 28.67 | 10.97 | 34.32 | 17.44 |
| 树形 | 10.32 | 2.44 | 15.12 | 9.28 | 23.33 | 17.79 |

只修复字体后，部分动画回调仍超过 25ms，因此继续处理玻璃快照。玻璃单独对照中，36 组不变背景的快照发布次数 687 → 0，滤镜缓存未命中次数 674 → 0，超过 25ms 的动画回调 347 → 43；与最初字体配置相比为 611 → 43。最终仍有 5 次超过 50ms 的回调；本轮没有逐次归因这些峰值。`RequestAnimationFrame` 计时属于框架回调间隔，不是 GPU 呈现 FPS，也没有测量完整鼠标投递延迟。

本次证明减少了重复字体查询和不变背景的滤镜重算，仍有少量较长回调，未保证所有场景无卡顿。结果限于本机、本地测试目录、上述名称与文件类型；OCR 并发、网络盘和 Intel 未在本轮覆盖。

## 验证

- 当前工作区完整隔离测试：`Total: 1234, Errors: 0, Failed: 0, Skipped: 4, Not Run: 1`。既有 Store 沙箱／SFTP 环境及实机 RAW 样本未配置的项目略过。
- 精确像素回归验证：相同背景可复用，8×8 采样格之外的单像素变化必须更新；隐藏页的玻璃不参与采样，重新显示正常恢复。
- 原生 App：浅色／深色、1000×680 DIP 窄窗口、中文与 emoji、图标／树形滚动、停留缩略图、选择、首页进入与返回均实际检查。测试配置与数据均位于独立临时目录。
- 独立提交副本（HEAD 加本次源码，排除其他工作区改动）：App／基准／测试构建通过，App 严格签名校验通过；字体、玻璃、首页玻璃与文件列表／树形相关测试共 368 项，0 失败。完整原生滚动 36 组完成，并检查上述窄窗口主题；这一轮与相关测试并行，计时没有加入正式对照数据。

主修复位于 `Assets/TypographyTokens.axaml` 和 `ThirdParty/LiquidGlassAvaloniaUI/LiquidGlassBackdropProvider.cs`。其余为基准、回归与证据；保留工作区原有其他改动。
