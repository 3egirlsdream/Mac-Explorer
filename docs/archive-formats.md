# 内置归档读取与解压

使用 SharpCompress 0.49.1 和 .NET 内置 GZip 解码器，无需安装命令行工具。所有列出的格式共用目录浏览、内容预览、解压到当前文件夹及同名文件夹入口；实际能力由文件内容和压缩方法决定，修改后缀不会改变格式。

| 格式 | 后缀与常用别名 | 说明 |
| --- | --- | --- |
| ZIP | zip、zipx、cbz、jar、war、ear、apk、xpi、vsix | 保留原有 ZIP64、密码与 WinZip AES 读取；不代表支持 ZIPX 的所有压缩算法 |
| RAR、7Z | rar、7z | 包含固实归档；密码读取沿用原流程 |
| TAR | tar | 展示内部目录并解出文件 |
| GZip | gz、gzip | 独立单文件或 TAR 包装；tgz、taz 为常用 TAR 别名 |
| BZip2 | bz2、bzip2 | 独立单文件或 TAR 包装；tbz、tbz2、tb2、tz2 |
| XZ | xz | 独立单文件或 TAR 包装；txz |
| Zstandard | zst、zstd | 独立单文件或 TAR 包装；tzst、tzstd |
| LZip | lz | 独立单文件或 TAR 包装；tlz |
| Unix compress / LZW | Z | 独立单文件或 tar.Z；tZ、taZ。taz 根据内容可为 GZip 或 LZW |
| ARC | arc | Stored、Packed、Squeezed、Crunched；Crunched 单条目受库的 128 KiB 限制；不支持 Squashed 等方法 |
| ARJ | arj | Stored 与方法 1–4；方法 1–3 单条目受库的 128 KiB 限制；不承诺加密或分卷支持 |
| ACE | ace | 仅 Stored；不支持 ACE 压缩方法 1/2、加密或分卷 |

TAR 复合后缀包括 `.tar.gz`、`.tar.gzip`、`.tar.bz2`、`.tar.bzip2`、`.tar.xz`、`.tar.zst`、`.tar.zstd`、`.tar.lz`、`.tar.Z`，命名同名文件夹时去掉整个后缀。后缀匹配不区分大小写。

单文件压缩缺少原始大小时显示不确定进度；没有安全有效的内嵌文件名时去掉最后一段压缩后缀。单文件内容全零与空 TAR 有歧义时，仅使用 TAR 后缀提示识别空 TAR。顺序读取只保留目录元信息，不将完整解压内容放入内存或中间 TAR 文件。

右键「解压」支持当前文件夹、同名文件夹、智能解压和自选位置。智能解压在多个顶层项目外创建同名文件夹。双击解压、包内「全部解压」和「解压此文件」共用固定的同名根目录。包内单文件保留完整路径。已有目录继续使用，同名文件通过粘贴冲突弹窗处理。

任务沿用暂停、继续、取消和重试。输出通过临时文件完成后提交。路径校验阻止写出越界路径、符号链接和硬链接。取消或失败时清理本次拥有的临时文件，已完成条目保留供重试。

「设置 → 通用 → 压缩与解压」可调整双击行为、默认压缩格式及包内缩略图。普通 ZIP 中未加密且原始大小不超过 16 MiB 的受支持文件，进入可见范围后才在后台加载缩略图。

压缩包生成在所选项目所在目录。跨目录多选时，使用第一个所选项目所在目录。

「保存压缩包密码」默认关闭。开启后，SQLite 保存已验证密码的 AES-GCM 密文和压缩包内容指纹，密钥单独保存在仅当前用户可访问的本地文件中，各发行渠道使用独立密钥。移动或改名后按内容识别。关闭设置暂停保存和自动解锁，原记录可在重新开启后使用。

本次不增加压缩输出格式、不新增分卷选择或合并界面，也不引入 CAB、ISO、CPIO、DEB、RPM 等额外引擎格式。

兼容能力依据 [SharpCompress 0.49.1 格式说明](https://github.com/adamhathcock/sharpcompress/blob/0.49.1/docs/FORMATS.md)及对应版本解码实现；测试样本与来源见 [归档测试数据](../Tests/MacExplorer.Tests/TestData/Archives/README.md)。
