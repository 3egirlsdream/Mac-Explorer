# 离线归档样本

`sample.txt` 为 UTF-8 文本 `archive sample: 中文 / café\n`。`sample.tar` 包含同样内容的 `docs/中文说明.txt` 和空文件 `empty.txt`。其 GZip/BZip2/XZ 版本由 Python 标准库生成，LZip/Zstandard 版本由项目锁定的 SharpCompress 0.49.1 编码器生成，`.Z` 版本由 macOS `/usr/bin/compress -c` 生成。自动测试不调用这些工具、不访问网络。

ARC、ARJ、ACE、RAR、7Z 固定样本来自 SharpCompress 源码提交 `1d9fd83a73db0144df3d2844133cf1b1f3699c7c`（NuGet 0.49.1 的 repository commit）：

- [上游归档目录](https://github.com/adamhathcock/sharpcompress/tree/1d9fd83a73db0144df3d2844133cf1b1f3699c7c/tests/TestArchives/Archives)
- [原始文件目录](https://github.com/adamhathcock/sharpcompress/tree/1d9fd83a73db0144df3d2844133cf1b1f3699c7c/tests/TestArchives/Original)
- [MIT 许可](../../../../ThirdParty/Notices/Dependencies/SharpCompress-0.49.1.txt)

`upstream-files.json` 中的 SHA-256 独立计算自上游原始文件，未使用待测解压器生成期望内容。上游归档的文本使用 Windows 换行，源码原始文件使用 LF；文本比较仅规范化 CRLF 为 LF，EXE/JPG 逐字节校验。部分旧格式文件名编码不同，因此这些样本通过后缀关联各自的三个原始文件。

`Arc.squashed.arc` 和 `Ace.method1.ace` 用于验证不支持算法的错误及临时文件清理，不作为成功解压样本。
