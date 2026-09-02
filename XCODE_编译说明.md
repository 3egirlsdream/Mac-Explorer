# 使用 Xcode 编译

1. 保持整个 `Mac-Explorer` 文件夹完整，不要只复制 `.xcodeproj`。
2. 双击 `MacExplorer.xcodeproj`。
3. 在窗口顶部选择 `MacExplorer > My Mac`。
4. 按 **Command-B**，或选择 **Product > Build**。不要选择 Product > Clean。
5. 第一次构建如果电脑没有 .NET 10.0.201，工程会自动下载到本源码目录的 `.dotnet` 文件夹，因此需要联网并等待下载完成。
6. 看到 **Build Succeeded** 后，选择 **Product > Run**；也可以在 Finder 中打开：

```text
bin/Debug/net10.0/osx-arm64/Mac Explorer.app
```

Xcode 工程现在使用标准的 macOS Application Target。Run Scheme 会从
Xcode 的 Build Products 中启动 App 包内的实际可执行文件：

```text
bin/Debug/net10.0/osx-arm64/Mac Explorer.app/Contents/MacOS/MacExplorer
```

如果构建失败，请在 Xcode 左侧点带感叹号的“问题导航器”，复制第一条红色错误信息；“Clean Succeeded”只表示清理成功，不代表已经编译。

## 搜索位置

打开应用内“设置 → 位置”，可看到 `/Applications`、`~/Applications`、`/System/Applications`，并在“额外文件夹”区域使用 **＋** 添加多个搜索目录、使用 **−** 移除选中目录。这里的列表会自动保存，并作为全局搜索“自定义位置”的搜索范围。

## 标签页快捷键

- **Command-T**：在当前文件夹新建标签页
- **Command-W**：关闭当前标签页；只剩一个标签时关闭窗口
- **Control-Tab**：切换到下一个标签页
- **Control-Shift-Tab**：切换到上一个标签页

## 多窗格布局

点击标签栏右侧、`+` 按钮旁边的“窗格布局”按钮，可选择 12 种布局，同时显示 1–4 个文件位置。选择多窗格布局时，如果标签页数量不足，应用会自动在当前文件夹创建所需标签页。点击任意窗格后，侧栏、顶部导航、工具栏、搜索和信息面板都会操作该活动窗格。
