# 文件夹内置素材与渲染验收

此工具直接调用应用的渲染器，生成磨砂／拟物普通图标、设置示例和固定种子的纸纹。运行时普通新样式图标读取 `Assets/FolderArtwork`，缺失／损坏时退回默认系统图标；照片封面仍根据真实照片动态合成。

## 生成与检查

从仓库根目录执行，所有入口要求隔离配置：

```bash
dotnet build Tools/Design/FolderRenderAssets/FolderRenderAssets.csproj -c Release \
  -p:SkipMacOSBundle=true -p:SkipMacOSReleaseDMG=true

bash Tools/Testing/run-isolated.sh \
  Tools/Design/FolderRenderAssets/bin/Release/net10.0/osx-arm64/FastListBench \
  check Assets/FolderArtwork
```

构建目标会把原生助手复制到工具旁；工具启动时验证 physical footprint 可用，避免因原生助手未加载而误测默认图标回退路径。macOS 经系统 shell 启动时可能移除 `DYLD_LIBRARY_PATH`，不要依赖它加载助手。

将 `check` 换为 `export` 会重建素材。只在设计确实变更时执行，并检查 PNG 差异。`check` 比较解码像素，不要求 PNG 压缩字节完全一致。导出普通拟物图标时关闭材质缓存并从固定种子生成纸纹，避免拿已内置素材验证自身。

尺寸为 16、32、48、64、96、128、160、192、224、256、512px。16–512px 内的其他尺寸选择相邻较大的资源并缩小；超过 512px 的现有请求保留输出尺寸，从最大 512px 资源放大，不回到程序生成。默认系统样式保持原路径。两张新增照片示例对应当前三张示例源图，另外两张普通图标示例直接复用 256px 图标。素材总共 25 个 PNG，约 1.1 MiB。

## 比较模式

同一可执行文件还支持：

- `capture <目录>`：输出 222 张原始图标与封面，覆盖三种样式、不同尺寸、照片数量、横／竖／透明源图。
- `compare <修改后目录> <修改前目录>`：生成逐像素差异统计 `comparison.json`。
- `bench <目录>`：冷图标、1000 次缓存命中、112／256px 动态合成、P50／P95、托管分配、GC、RSS 和 macOS physical footprint。
- `scroll <目录>`：真实窗口中 10000 项列表的快速跳转与连续滚动；包含布局和 CPU 离屏绘制，不是显示器／GPU 呈现 FPS。源图读取在计时前完成，provider 包含解码，保持两路生成。

比较时保留修改前完整输出程序集，用同一版本工具分别加载旧／新 `MacExplorer.dll`，不要在测量中覆盖程序集。三轮按旧／新、新／旧、旧／新交替运行；不要把首次 JIT、系统图标冷初始化或其他进程负载差异算作渲染器收益。

## 真应用长时间循环

```bash
dotnet build MacExplorer.csproj -c Debug -p:SkipMacOSReleaseDMG=true
MACEXPLORER_FOLDER_RENDER_SOAK=1 bash Tools/Testing/run-isolated.sh \
  'bin/Debug/net10.0/osx-arm64/Mac Explorer.app/Contents/MacOS/MacExplorer'
```

仅 Debug 且隔离模式可用。在本次测试目录创建 120 个相册，持续至少 10 分钟反复新建页签、切换三种样式、缩放、滚动、关闭页签。每轮记录托管内存、总分配、GC、RSS、physical footprint、材质缓存和已关闭对象弱引用；随后空闲 65 秒，另记强制回收前后结果。工作负载期间不强制 GC，回收后的下降不能替代运行中趋势。结果位于打印出的测试根目录 `folder-render-soak`。

`FolderMaterialCache` 的 4 MiB 是缓存保留像素预算；被淘汰但仍有租约的图像继续计入诊断 Bytes，最后一个使用者释放后销毁。诊断 Pending 表示活动租约数，Owners 保持已有诊断系统的实例计数语义。入口不操作真实用户目录。
