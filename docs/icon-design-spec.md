# FKFinder 图标设计规范

基于 Microsoft Fluent Design 的文件类型图标设计体系，适用于 `FileIconRenderer.cs` 中所有 SVG 图标的生成。

---

## 1. 画布与尺寸

| 属性 | 值 |
|------|-----|
| ViewBox | `0 0 32 32` |
| 视觉填充区域 | 28×28（从 `(2,2)` 到 `(30,30)`） |
| 输出尺寸 | 通过 `width`/`height` 参数动态指定（网格36px、列表20px、预览80px） |

---

## 2. 设计原则

- **扁平化**：不使用渐变(linearGradient/radialGradient)，仅通过多层纯色+不同透明度叠加营造微妙深度
- **圆角**：所有矩形使用圆角 `rx=4~6`，小元素 `rx=0.7~2`
- **阴影**：统一使用偏移 1px 的同形状黑色填充 `opacity="0.04"` 作为阴影层
- **高光**：顶部区域叠加低透明度浅色层（`opacity="0.15~0.35"`），仅用于制造微弱层次感
- **填充优先**：用填充圆角矩形 `<rect rx>` 代替描边线条 `<line>`，保持 Fluent 的柔和感
- **最少描边**：仅在语义必要时使用描边（如代码括号、对勾），`stroke-width="1.2~1.5"`，始终加 `stroke-linecap="round" stroke-linejoin="round"`

---

## 3. 字体规范

```
font-family="'Segoe UI','SF Pro Display',-apple-system,sans-serif"
font-weight="700"
letter-spacing="0.2"
```

字号自适应规则（按文本长度动态计算）：

### Badge 字号（右下角浮标）— `TFs()`

| 字符数 | 字号 |
|--------|------|
| ≤2 | 6.2 |
| 3 | 5.5 |
| 4 | 4.8 |
| ≥5 | 4.0 |

### CenterExt 字号（居中大字）— `LFs()`

| 字符数 | 字号 |
|--------|------|
| 1 | 11 |
| 2 | 9 |
| 3 | 7.5 |
| 4 | 6.5 |
| ≥5 | 5.2 |

---

## 4. 基底模板

图标分为三种基底模板，新图标必须从中选择一种。

### 4.1 Doc（文档基底）

适用于：文本类、文档类、可执行文件、字幕等"文件"语义的图标。

```
构成：
1. 阴影层：<rect x="3" y="3" width="28" height="28" rx="5" fill="#000" opacity="0.04"/>
2. 主体页：<rect x="2" y="2" width="28" height="28" rx="5" fill="#FAFBFC"/>
3. 折角色块：<path d="M22 2h3c2.76 0 5 2.24 5 5v0h-5c-1.66 0-3-1.34-3-3V2z" fill="{tint}"/>
```

参数 `tint`：折角区域颜色，用于区分不同文件类型，默认 `#E8ECF0`。

### 4.2 Card（卡片基底）

适用于：媒体类、内容类图标（图片、视频、音频、数据库、设计文件等）。

```
构成：
1. 阴影层：<rect x="3" y="3" width="28" height="28" rx="6" fill="#000" opacity="0.04"/>
2. 主体卡片：<rect x="2" y="2" width="28" height="28" rx="6" fill="{fill}"/>
3. 顶部高光：<rect x="2" y="2" width="28" height="14" rx="6" fill="{tint}" opacity="0.35"/>
```

参数：
- `fill`：卡片底色（浅色，如 `#FFF7ED`）
- `tint`：高光层颜色（略深一级，如 `#FFEDD5`）

### 4.3 Folder（文件夹基底）

适用于：文件夹图标，使用 Fluent UI 官方 folder 造型路径。

```
构成：
1. 阴影层：同造型偏移 1px 的 path，fill="#000" opacity="0.04"
2. Tab 后片：左上角文件夹标签，fill="#D4A017"（深琥珀）
3. 前片主体：文件夹正面，fill="#F5C731"（亮琥珀）
4. 顶部细高光：<rect y="13" height="1" fill="#FFF" opacity="0.15"/>
```

---

## 5. 内容图形规范

### 5.1 TextLines（文本行）

用圆角填充矩形模拟文本内容行：

```
每行：<rect x y width height="1.6" rx="0.8" fill="{color}" opacity="{op}"/>
行间距：3.2
递减宽度：每行比上一行短 2.5
```

### 5.2 Office 品牌面板

Word / Excel / PowerPoint 使用统一的左侧品牌面板 + 右侧文档预览布局：

```
右侧文档区：<rect x="10" y="2" width="20" height="28" rx="4" fill="{浅色}"/>
            上半高光：同区域 height="14" opacity="0.7" fill="{更浅色}"
            内容区：TextLines 或网格 cells

左侧品牌面板：<rect x="2" y="4" width="16" height="24" rx="4" fill="{品牌主色}"/>
              上半高光：同区域 height="12" opacity="0.6" fill="{品牌亮色}"
              品牌字母：font-size="13" font-weight="700" fill="#fff"
```

品牌色对照：

| 应用 | 字母 | 主色 | 亮色 | 文档底色 | 内容色 |
|------|------|------|------|----------|--------|
| Word | W | #185ABD | #2B7CD3 | #D6E4F5 | #2B579A |
| Excel | X | #107C41 | #21A366 | #D0E8D8 | #217346 |
| PowerPoint | P | #C43E1C | #E04E2C | #F2D9D0 | #C43E1C |

---

## 6. 后缀显示方式

每个图标只能使用以下**一种**后缀显示方式，禁止同时使用以避免重复。

### 6.1 Badge（浮动标签）

右下角圆角药丸形标签，适用于 Card 基底图标和需要显示简短后缀的 Doc 图标。

```
位置：右下角，y=22.5，高度 7.5，rx=3
宽度：max(11, 3.5 + 字符数 × 3.6)
文字：白色加粗，使用 TFs() 字号
背景：与图标主题色一致的深色
```

### 6.2 CenterExt（居中大字）

图标正中偏下位置的大字后缀，适用于文本类 Doc 基底图标。

```
位置：x=16（水平居中），y 默认 19（可调）
文字：主题深色，加粗，使用 LFs() 字号
```

---

## 7. 配色体系

每种图标类型有独立的色系，由浅到深通常包含 3-4 个色阶：

| 图标类型 | 浅色/底色 | 中间色 | 深色/强调色 | 后缀背景 |
|----------|----------|--------|-----------|----------|
| 通用文件 | #FAFBFC | #94A3B8 | #E8ECF0 | — |
| 文本文件 | #CBD5E1 | #94A3B8 | #64748B | — (CenterExt) |
| Markdown | #C7D2FE | #818CF8 | #4338CA | — (CenterExt) |
| PDF | #FECACA | #EF4444 | #DC2626 | — (横幅) |
| 压缩文件 | #DDD6FE | #8B5CF6 | #6D28D9 | — (CenterExt) |
| 证书 | #FEF3C7 | #FBBF24 | #F59E0B | — |
| 安装包 | #BFDBFE | #3B82F6 | #2563EB | #2563EB |
| 图片 | #FFEDD5 | #FB923C | #EA580C | #C2410C |
| 网页 | #DCFCE7 | #4ADE80 | #16A34A | #16A34A |
| 代码 | #1E1B4B | #818CF8 | #6366F1 | #6366F1 |
| 配置 | #FDE68A | #F59E0B | #D97706 | #B45309 |
| 视频 | #EDE9FE | #8B5CF6 | #7C3AED | — |
| 音频 | #FCE7F3 | #EC4899 | #DB2777 | — |
| 字体 | #D1D5DB | #6B7280 | #374151 | #6B7280 |
| 数据库 | #D1FAE5 | #34D399 | #059669 | #059669 |
| 电子书 | #FEF3C7 | #D97706 | #92400E | #92400E |
| 设计 | #FCE7F3 | #EC4899 | #DB2777 | #DB2777 |
| 3D | #EDE9FE | #8B5CF6 | #7C3AED | #7C3AED |
| 字幕 | #CBD5E1 | #94A3B8 | #64748B | #64748B |
| 可执行 | #CBD5E1 | #475569 | #475569 | #475569 |
| 虚拟机 | #DBEAFE | #3B82F6 | #2563EB | #2563EB |
| 文件夹 | — | #D4A017 | #F5C731 | — |

---

## 8. 新增图标检查清单

1. **选择基底**：文档语义用 `Doc(tint)`，媒体/内容语义用 `Card(fill, tint)`
2. **绘制特征图形**：用填充形状而非描边，保持圆角，透明度 0.06~0.5
3. **选择后缀方式**：只能选 Badge 或 CenterExt 之一，不可同时使用
4. **配色**：从 Tailwind 色板中选取相近的 3-4 级色阶
5. **阴影一致性**：确保阴影层 opacity 为 0.04，偏移 1px
6. **视觉尺寸**：所有内容限制在 28×28 视觉区域内
7. **注册入口**：在 `Render()` 的 switch 中添加 iconKey 映射
8. **扩展名映射**：在 `SqliteFileIndex.ResolveIconKey()` 中添加对应扩展名

---

## 9. SVG 输出包装

```csharp
// 文件图标
$@"<svg width=""{size}"" height=""{size}"" viewBox=""0 0 32 32"" xmlns=""http://www.w3.org/2000/svg"">{inner}</svg>"

// 文件夹图标
$@"<svg width=""{size}"" height=""{size}"" viewBox=""0 0 32 32"" xmlns=""http://www.w3.org/2000/svg"">{FolderIcon()}</svg>"
```

文本内容必须通过 `WebUtility.HtmlEncode()` 转义后输出。
