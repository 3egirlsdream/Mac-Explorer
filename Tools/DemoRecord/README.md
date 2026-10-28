# 本地功能演示录制

一条命令自动构建专用 Demo.app、准备示例资料、驱动真实 UI、分段录制与检查；不需要 Codex computer use、实时截图识别或剪辑工具连接。

```bash
# 在仓库根运行；首次构建稍慢，后续增量构建。
bash Tools/DemoRecord/record.sh --out outputs/demo-capture-local

# 查看每个场景展示什么，无需启动应用。
bash Tools/DemoRecord/record.sh --list

# 权限与显示器检查；不主动申请或点击系统授权。
bash Tools/DemoRecord/record.sh --doctor

# 单独补拍，必须使用新输出目录。
bash Tools/DemoRecord/record.sh --only 12-pdf-space,20-delivery-drag --out outputs/demo-native

# 输入版本与素材哈希均一致时，复用通过镜头，仅重录失败/缺失镜头。
bash Tools/DemoRecord/record.sh --out outputs/demo-capture-local --resume
```

依赖 macOS、Xcode Command Line Tools、.NET 10、Python 3.11+、ffmpeg/ffprobe。当前分镜布局要求主显示器至少 **1440×900 逻辑点**；分镜用 1200×680 窗口，放在 `(120,70)`，取景为 `(0,0,1440,810)`，四周保留桌面背景与窗口阴影，直接等比导出 1920×1080，无黑边。其他分辨率应调整 `DemoAutomation.setup`、fixture 窗口偏好与 `Session.capture` 的取景配置后重新验收，不会更改系统显示器分辨率。录制期间保持解锁，不操作其他窗口；鼠标命中其他进程或键盘焦点丢失时会拒绝发送输入。

实际执行权限取决于 `outputs/demo-tools/Demo Recorder.app` 及启动它的负责进程。`--doctor` 检查屏幕录制、CGEvent 发送和 AX 信任；录制使用前两项。缺权限时在系统设置给实际工具授权，再运行检查。没有权限继承、签名免授权或锁屏录制的承诺。

## 隔离与示例数据

`record.sh` 先调用既有 `Tools/Testing/run-isolated.sh`。每个场景/重试都建立新的 `.../fkfinder-test.*/<scene>__tNN/山野工作室`，数据库、配置、缓存、索引、插件目录均按测试路径规则隔离。默认禁用 LocalSend、Copilot、自动图像分析及演示构建的启动更新检查；需要的 PDF/OCR/分类由对应分镜显式调用真实本机服务。

`fixtures.py` 提前生成方案 Markdown、会议文本、可搜索 PDF、可预览 Word、ZIP、SVG 标志、脚本、批量重命名照片和受控收件目录；唯一复制的照片来自仓库 `docs/Assets/alpine-lake.jpg`。白板 PNG 由本机字体绘制生成。文件内容与修改时间固定，创建时间为本轮实际生成时间；文件哈希写入 fixture manifest。收藏与 Finder 标签通过应用的 IFileTagService 创建，不直接填假分析结果。照片不冒充带真实 EXIF 相机信息。

专用构建有独立 bundle ID `com.macexplorer.demo`，入口在任何数据库初始化前检查临时测试根、随机令牌与所有权标记；正常构建不包含 `Tools/DemoRecord/*.cs`。所有进程直接启动本轮 `.app/Contents/MacOS/MacExplorer`，不使用 `open -a`、不复用正常应用、不 killall。清理仅针对本轮保存的子进程句柄。应用内文件动作拒绝测试根之外的绝对路径与符号链接；测试模式本身不是系统沙箱。

## 素材交付与失败处理

- `clips/*.mp4`：合格剪辑素材，1920×1080、30 fps、H.264/yuv420p、无声、真实原速；默认场景的 16:9 画布铺满画面。
- `takes/<scene>__light__zh-CN__tNN/source.mov`：高分辨率原片；同目录保留捕获统计、`framing.json` 取景与背景哈希、fixture manifest、最终状态和 app 日志。
- `manifest.json`：功能描述、实际采用的 take、代码/fixture/二进制哈希、媒体信息、断言结果、采集 PTS 和关键动作标记。
- `markers.csv`：关键步骤相对秒数、30 fps 整数帧、非 drop-frame 时间码。
- `qa/<take>/head.jpg / middle.jpg / result.jpg`：逐镜抽帧；仍需要人工审视菜单、文字、预览和剪辑节奏。
- `events.jsonl`：使用 host monotonic clock 的动作记录，用于与采集 PTS 对齐。

等待使用应用请求响应、实际路径/条目/任务状态、预览内容就绪、录制首帧与结束、接收事件及内容哈希。`hold` 仅表示镜头节奏，不能作为判断动作成功的替代。录制器检查有效采集帧、写入返回值与最终状态，剪辑副本经过 ffprobe 和完整解码检查。Idle 静止帧不当作故障。

普通拖出已发现会激活演示主窗口；当前成功分镜使用显式 Command 拖拽。物理 Cmd+K 激活尚未通过，面板通过真实应用入口打开并通过真实查询字段驱动。这些限制写入场景与 manifest，保留失败 take，不冒充完整物理输入验证。

失败镜头不进入合格素材名单。每场景默认最多 2 take（`--attempts 1..3`），每次从全新资料重演。权限、焦点、未知窗口、功能断言或编码失败会留下错误和残片；最终任意必录镜头失败，命令以非零退出。`--resume` 必须使用完全相同输入代码、场景、fixture 和二进制，防止混用旧版本；更改代码/场景后使用新输出目录。成功片段文件被改动时会重新拍摄。

## 场景与扩展

所有默认功能集中在 **`scenes/local.json`**，每段有稳定 `id`、中文 `title`、明确 `description`、`prepare`、`record`、取景 profile 和输入方式。下列清单由该文件列出；实际通过情况看本轮 manifest，不能以存在场景配置当作功能已经录制通过。

| ID | 功能与验收 |
| --- | --- |
| 01-home | 展示由真实标签服务创建的收藏卡片与首页文件；等待界面就绪。 |
| 02-script-editor | 打开真实命令编辑窗口，展示提前配置的安全脚本、名称、图标与工作目录；本场景不启动用户终端。 |
| 03-view-modes | 依次切换三个真实文件视图，核对每次 ViewMode。 |
| 04-multi-pane | 使用真实应用命令切换三栏、四分区，核对布局。 |
| 04-native-two-pane | 真实点击标题栏布局按钮及双栏选项，等待布局状态。 |
| 05-context-menu | CGEvent 对项目方案右键，等待真实动态菜单，悬停打开子菜单；不执行对外分享。 |
| 05-sort-group | 真实文件按类型分组，再按名称倒序；展示顺序变化。 |
| 06-tags | 通过真实标签服务查找山野精选，显示原文件引用。 |
| 07-information | 选中真实照片并展开信息面板；示例照片不冒充包含相机 EXIF。 |
| 08-global-search | 应用内打开真实 Cmd+K 面板并设置查询文本，等待真实索引命中与固定预览；该镜头不验证物理 Cmd+K 或 TextBox 原生文字注入。 |
| 08-name-search | 在隔离项目范围搜索高山，检查命中高山湖泊.jpg。 |
| 09-pdf-content | 在隔离资料上执行真实 PDF 文本提取并保存分析，搜索正文 lake，验证 PDF 命中。 |
| 10-image-ocr | 对合成白板运行本机 Vision OCR，搜索 MOUNTAIN，验证图片命中。 |
| 11-image-classification | 对仓库示例湖泊照片运行真实 Vision 分类，打开最高置信度分类并验证照片。 |
| 12-image-space | 真实鼠标选中文件，发送空格，验证预览层打开。 |
| 12-office-preview | 打开自动生成的 Word 文档，录制系统 Quick Look 内嵌界面。 |
| 12-pdf-space | 打开自动生成的可搜索 PDF，录制真实 PDFKit 内容。 |
| 13-markdown-edit | 真实编辑器输入待办，按 Cmd+S，检查磁盘中保存的内容。 |
| 14-batch-rename | 真实工作台预览规则，点击执行，再点击撤销；检查文件恢复。 |
| 15-image-convert | 真实右键文件转换菜单，SVG 转 PNG，确认尺寸对话框并校验输出文件。 |
| 15-markdown-word | 真实右键调用内置转换插件，将示例方案转 Word 并检查输出。 |
| 16-archive-preview | 真实打开 fixture ZIP 并显示其内容列表。 |
| 16-compress | 打开真实压缩窗口，设置独立输出名，点击确认并校验 ZIP 结构。 |
| 16-extract | 调用实际解压入口并显示任务面板，验证生成文件及内容。 |
| 17-copy-undo | 实际文件任务复制到交付目录，校验内容，再经历史服务撤销。 |
| 20-delivery-drag | 菜单栏面板取用下载示例图，以 Command 原生拖拽保持接收窗口前台，校验副本和主窗口激活计数。普通无修饰键拖出另行验收。 |
| 22-command-hints | 真实 HID flagsChanged 按下 Command，等待提示浮层出现，再释放并确认关闭。 |
| 22-settings | 打开真实设置窗口，等待窗口存在，录制当前语言与偏好。 |

### 添加一个场景

复制相近分镜，使用小写字母/数字/连字符的唯一 ID；准备素材写入 `fixtures.py`，禁止读取用户桌面/文稿/下载。`${ROOT}` 表示本镜头测试根，`${PROJECT}` 为其 `Documents/山野计划`。准备阶段不录制，录制阶段自动包含 0.8 秒片头与 1.2 秒片尾余量。

```json
{
  "id": "30-extra-preview",
  "title": "额外预览",
  "description": "打开 fixture 照片，等待真实预览内容准备好，再退出。",
  "captureProfile": "app",
  "interactionMode": "native-input",
  "prepare": [{"action":"app","op":"navigate","path":"${PROJECT}/摄影作品"}],
  "record": [
    {"action":"click","target":{"file":"高山湖泊.jpg"}},
    {"action":"key","code":49},
    {"action":"wait","state":{"preview":true,"previewReady":true}},
    {"action":"hold","seconds":2},
    {"action":"key","code":53}
  ]
}
```

两个 profile 都由录制器创建不接收输入的桌面层背景窗口，使用仓库 `docs/Assets/alpine-lake.jpg` 的副本并略微压暗；它位于应用窗口下方，随录制结束销毁。不会读取或修改系统壁纸、真实桌面文件及用户偏好。背景作为真实窗口参与 ScreenCaptureKit 合成，菜单、弹窗和窗口阴影仍由系统实时录制。

`app` profile 包含本轮应用窗口与受控背景；`desktop` 额外包含受控收件箱和系统菜单栏。ScreenCaptureKit 不捕获未列入的正常用户应用；系统 XPC 窗口不在默认白名单里。系统分享、打开保存面板等新增场景要先验收实际窗口归属，不能只扩大到整个桌面。每个 take 的 `framing.json` 记录画布、窗口布局、背景来源与哈希；背景哈希也参与 `--resume` 输入匹配。

| action | 参数与完成语义 |
| --- | --- |
| `app` | `op` 为下表应用操作；返回业务操作结果并按需等待合成提交 |
| `wait` | `state` 精确断言；默认 25 秒 deadline，可加 `timeout`、`window` |
| `click` / `move` | `target.file` 为当前可见文件名；或 `target.name` 为控件名、无障碍名称、菜单文字/布局 Tag。支持 `window`、`right`、`count`。执行时重新计算真实 bounds，native 检查屏幕命中 |
| `key` | macOS 虚拟键码 `code`；`modifiers` 可含 cmd/shift/alt/ctrl。49 空格、53 Escape、36 Return、1 S、40 K |
| `text` | Unicode 原生输入；强制清除残留修饰键 |
| `command-down` / `command-up` | 真实 HID Command flagsChanged，失败清理时配对释放 |
| `hold` | `seconds`，仅作片头片尾/阅读停留 |
| `file` | `path` 等待真实文件存在；可加 `contains` 文本、`matches` 源哈希、`format` 容器/PNG 检查或 `exists:false` |
| `receiver` | 启动本轮 AppKit 收件箱，只接受本轮根内文件并复制到受控目录 |
| `drag` | `source.file/window`；可显式指定 `modifiers`，当前速递分镜用 `cmd`；原生鼠标拖到受控收件箱，核对接收事件、哈希、前台应用与主窗口激活计数 |

| app op | 功能与限制 |
| --- | --- |
| `navigate` / `select` | 测试根内目录导航；按 `names` 选择真实已加载条目 |
| `capability` | `id` 与 `arguments` 复用 ui.* 和 file.info 能力；仍调用真实功能所有者，文件路径受根约束 |
| `global-search-open` | 调用真实全局搜索入口；当前不把它标为物理 Cmd+K 或 TextBox 原生输入验收 |
| `preview` / `preview-close` | 真实 SuperPreview，支持原生 PDFKit/Quick Look；不是离屏截图 |
| `editor-open` / `editor-save` | 当前真实 Markdown 编辑器；文件写入仍限 fixture |
| `tag-navigate` | 通过 `name` 定位已创建标签 |
| `index-ready` | SearchIndexer.Observe / Changed，等待 Ready，Partial/Unavailable 报错；已导航文件夹提供条目信息 |
| `analyze` / `content-search` | 对 `path` 做真实 PDF/Vision 分析并保存；在文字搜索页真实查询 `query` |
| `classification-open` | 从真实分析结果选最高置信度分类；无结果报错 |
| `script-dialog` | 展示本轮安全脚本配置，不启动用户默认终端 |
| `text` | 对指定窗口 `name` 的真实 TextBox 设置字段；用于压缩名等前置参数 |
| `copy` / `undo` / `extract` | 现有文件操作/历史/压缩服务，必须以输出与任务终态验收 |
| `delivery-navigate` | 文件速递独立窗格导航 |
| `target` / `state` / `commit` | 编排器定位、状态检查与布局合成屏障；不是对外永久远控 API |

`wait.state` 支持 `loading`、`pendingImages`、`viewMode`、`layout`、`selected`、`entry`、`window` 类型、`menu` 项、`preview/previewReady`、`globalSearch/globalResult`、`editorContains`、`hints`、`mainActive`、`tasksComplete`。文件速递的状态检查加 `window: delivery`。状态读取在 UI dispatcher 内执行，mailbox 每个请求有随机令牌，未认证命令不执行。

### 当前不宣称覆盖的功能

普通无修饰键文件速递拖出、物理 Cmd+K 激活、脚本默认终端执行、系统截图与剪贴板、所有排序/过滤组合、所有压缩格式、任务暂停继续/取消、在线插件市场安装、真实网络发现/远程传输、AirDrop 实机接收、Copilot 模型回复、商店购买/授权与正常受保护目录流程不属于此本地清单。已覆盖的插件是随演示构建打包的内置文件转换，不把它称为本次在线下载安装。新增这些功能应另设 profile 和受控接收端，保留同样的证据与失败处理。

## 维护与验证

```bash
python3 -m unittest discover -s Tools/DemoRecord -p 'test_*.py' -v
bash Tools/Testing/run-tests.sh fast
```

改专用 C# adapter 后重新构建；`--no-build` 仅在已经用本轮源码构建时使用。正常产品构建没有演示控制端。只要改场景、fixture、输入或录屏引擎，就至少实录受影响镜头并检查抽帧；Headless/单测通过不等于真实录制通过。不要为节约操作时间删除根路径、PID、命中、焦点和文件结果校验。
