# 磨砂口袋文件夹 Demo

独立 Avalonia 视觉比较页面，使用主项目的 `AppWindow`、主题、字体、按钮与本地化。主程序通过设置 → 显示 → 文件夹样式，成套切换普通文件夹和照片封面，默认保留系统样式。

普通文件夹设计使用 `--plain`：整片浅蓝磨砂、顶部开口、圆弧折光和轻阴影，同屏对比照片封面、现有系统图标及 16–128px 小图；与主程序共享 `Services/Impl/PlainFolderRenderer.cs`。支持 `--dark`、`--width 780`、`--language fr`；`--capture` 同时导出页面与 1024px 透明背景图标（`.icon.png`）。

普通图标与照片封面通过 `Services/Impl/FrostedFolderEdge.cs` 共用细边材质：取消宽幅卷边和内侧暗槽，只保留细窄的定向反光；两侧与下沿的层次由前片透色和轻阴影表现。普通图标另缩窄顶部露出的背板，照片封面保留原有主图比例和底部磨砂。

前后层次共用 `Services/Impl/FrostedFolderDepth.cs`：后片压暗并使用偏灰冷蓝，减弱后片高光；开口接触阴影仅落在前片后方，向两端渐隐。前片与细边宽度保持原有参数。

```bash
dotnet build Tools/Design/FolderIconDemo/FolderIconDemo.csproj -p:SkipMacOSReleaseDMG=true
bash Tools/Testing/run-isolated.sh \
  "Tools/Design/FolderIconDemo/bin/Debug/net10.0/osx-arm64/Folder Icon Demo.app/Contents/MacOS/FolderIconDemo"
```

- 主图铺满整个前片，底部默认 11% 为磨砂口袋，Demo 可在 10–30% 间调整；主程序、设置预览与 Demo 共用默认值。磨砂减弱灰蓝染色、去色和反光，保留更多照片透色。
- 双图模式仅在顶部露出第二张图片，旋转约 −2.4°；后图使用约 0.75 个设计单位的浅蓝纸边；单图模式保留文件夹背板与页耳。
- 清晰图和模糊图先以同一比例裁切，再由统一玻璃材质采样，底部按浅凸曲面折射照片。
- 前片的覆盖率、折射与光照在同一次绘制中计算，不叠加独立的白色周边描线。圆角距离场提供法线，与普通文件夹共用细边高光和轻微折射；顶部背板保留冷蓝色系渐变与柔和透光。
- 底部磨砂采用随深度变化的散射、弧面环境反射和下缘收暗，保留原图模糊透色；接口亮部也由材质生成，不画单独的水平白线。
- 下半部边缘比顶部更窄，降低蓝灰暗边、接触阴影与底部反光，用轻柔透色过渡到外缘。
- 点击右侧名称切换大图；可更换主图或后图，图片仅在当前 Demo 内使用。
- 同屏展示 48 / 64 / 96 / 128px；窗口变窄时展示区上下排列。
- 渲染按控件实际显示尺寸与屏幕缩放生成，以 32px 分档；小图不再借用固定 1024px 位图。大预览按自己的尺寸生成，最高 1024px。
- 直接复用 `HomeThumbnailCache` 的延迟加载、同请求合并、8 MiB／64 项缓存及租约生命周期；`FolderCoverRenderService` 在后台最多同时生成两张。尺寸、照片版本、单／双图、磨砂参数和阴影主题共同决定缓存键，过期请求取消并拒绝旧结果。
- 切换选中项和已缓存主题复用已生成的位图；关闭窗口释放租约，并取消尚未完成的生成。生成和解码均不在绘制过程中执行。
- 参数：`--dark`、`--single`、`--width 760`、`--language en`（另支持 `zh-CN`、`zh-Hant`、`fr`）；`--original-cover` 使用上一版湖泊照片，以同一输入检查材质变化。
- 导出实际 Avalonia 页面：`--capture /绝对路径/preview.png --exit`。

借鉴用户提供压缩包中的“整图预模糊后裁切输出”思路，重新绘制文件夹轮廓、叠图和立体边缘。渲染器现位于 `Services/Impl/FrostedFolderRenderer.cs`，与主程序共享；设置 → 显示 → 文件夹照片封面提供“默认／磨砂玻璃”预览选择，默认继续使用原样式。主程序复用文件列表的可见范围加载、后台限流和位图缓存，Demo 保留独立的材质比较页面。可复现性能比较见相邻目录 `FolderIconPerformance/README.md`。

默认 `reference-cover.png` 是从用户提供设计稿中提取的可见照片区域，不包含文件夹或玻璃材质；仅把底部照片镜像延伸到被磨砂遮住的区域，供同封面对比使用，并非原始摄影文件。所有文件夹轮廓、玻璃、折射、模糊和阴影仍由渲染器绘制。旧版湖泊图片复用 `docs/Assets/alpine-lake.jpg`。其他示例素材来自 Unsplash，仅用于独立视觉预览：

- [海岸图片](https://images.unsplash.com/photo-1518837695005-2083093ee35b)
- [森林图片](https://images.unsplash.com/photo-1441974231531-c6227db76b6e)
- [建筑图片](https://images.unsplash.com/photo-1487958449943-2429e8be8625)


## 拟物纸质：相册叠片

`--skeuomorphic` 打开独立纸质 Demo，与生产环境共用 `SkeuomorphicFolderRenderer`。左侧展示三张照片、暖白相纸边与实体米灰口袋，右侧展示配套普通文件夹，底部对照 16–128pt 与系统图标。纹理为固定种子生成并复用的细纸纹，不使用参考图拼贴，不复用玻璃材质。

```bash
dotnet build Tools/Design/FolderIconDemo/FolderIconDemo.csproj -p:SkipMacOSReleaseDMG=true
bash Tools/Testing/run-isolated.sh \
  "Tools/Design/FolderIconDemo/bin/Debug/net10.0/osx-arm64/Folder Icon Demo.app/Contents/MacOS/FolderIconDemo" \
  --skeuomorphic --capture /tmp/folder-paper.png
```

可追加 `--dark --width 780 --language fr`；`--album coast|forest|architecture` 选择其他内置照片，`--photo-count 1|2|3` 检查层叠数量，`--exit` 截图后退出。截图旁同时输出 `.icon.png` 普通文件夹透明图。

设置 → 显示 → 文件夹样式提供默认／磨砂玻璃／拟物纸质三项，成套切换普通文件夹与照片封面；默认值和照片封面开关保持独立。设置预览、文件列表、首页普通文件夹与信息面板复用同一按样式及尺寸缓存的图标；照片继续使用原可见范围加载、两并发限流与过期结果拒绝。

### 与原设计同尺寸核对

纸质材质与层叠参考原设计；轮廓按用户后续要求改为默认 macOS 文件夹的约 1.27:1 横向比例，照片单独等比裁切，保留完整背板，只移除左侧带弧度的灰色折边。取消前片的双重描边；细纸纹、短纤维与缓慢起伏静态生成，口袋边缘以表面法线计算薄亮边和下缘厚度。

给 `--skeuomorphic` 追加 `--reference <reference.png>`，Demo 只提取其中的照片内部作为输入，图标轮廓、内衬、纸边、口袋、纹理和阴影仍由共享渲染器生成。该选项用于视觉核对，生产预览继续使用内置照片。

`--render-paper-review --reference <reference.png> --output <目录>` 可导出 640×640 的同尺寸对照：`comparison.png` 左为原设计，右为实际渲染，主照片相同；后侧两张从参考图其他照片内部提取。另存普通文件夹和照片封面的透明 PNG。原设计只用于左侧对照，不作为任何文件夹图标的底图。

对照命令可追加 `--default-folder <默认文件夹透明PNG>`，输出 `default-proportions.png`，从左到右比较默认、拟物普通、拟物照片三者的整体比例。照片的源裁切单独计算，调整轮廓不缩放照片横纵比例。

磨砂普通文件夹和照片封面共用 `FrostedFolderDepth.Front` 与背板轮廓，按默认文件夹统一为约 1.27:1；照片纹理按实际前片尺寸等比裁切，保留 11% 底部磨砂及原有细边光学参数。`--render-glass-review` 可使用与纸质对照相同的参数导出默认／磨砂普通／磨砂照片三项预览。
