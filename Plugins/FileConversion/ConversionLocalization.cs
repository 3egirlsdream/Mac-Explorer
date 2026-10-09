using System.Globalization;

namespace MacExplorer.Services.Impl;

internal static class ConversionLocalization
{
    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["无法确定图像尺寸。"] = "Unable to determine image dimensions.",
        ["此文件不支持所选转换格式。"] = "This file does not support the selected format.",
        ["源文件不存在。"] = "The source file does not exist.",
        ["WebP 动画仅导出第一帧，本次转换不保留动画。"] = "Only the first WebP frame is exported; animation is not preserved.",
        ["Word 文件损坏、已加密或格式无效。"] = "The Word file is damaged, encrypted, or invalid.",
        ["已按基础文档转换；复杂分页、浮动对象和页眉页脚可能与 Word 不同。"] = "Converted as a basic document; complex pagination, floating objects, headers, and footers may differ from Word.",
        ["转换未生成有效文件。"] = "Conversion did not produce a valid file.",
        ["转换超时，请尝试更小或更简单的文件。"] = "Conversion timed out. Try a smaller or simpler file.",
        ["Word 文档缺少根关系文件。"] = "The Word document has no root relationships file.",
        ["文件包含二进制内容，无法作为文本转换。"] = "The file contains binary content and cannot be converted as text.",
        ["无法解码文本，请先保存为 UTF-8 或带 BOM 的 Unicode 文本。"] = "Unable to decode text. Save as UTF-8 or Unicode with a BOM first.",
        ["缺少内置转换组件，请重新安装应用。"] = "The built-in conversion component is missing. Reinstall the app.",
        ["无法启动转换组件。"] = "Unable to start the conversion component.",
        ["转换组件运行超时。"] = "The conversion component timed out.",
        ["转换组件执行失败。"] = "The conversion component failed.",
        ["无效的 SVG 文件。"] = "Invalid SVG file.",
        ["SVG 包含不支持的脚本或嵌入网页。"] = "The SVG contains unsupported scripts or embedded web content.",
        ["SVG 引用了外部资源，请先将资源嵌入文件。"] = "The SVG references external resources. Embed them in the file first.",
        ["SVG 包含外部样式资源。"] = "The SVG contains external style resources.",
        ["SVG 尺寸无效。"] = "Invalid SVG dimensions.",
        ["无法渲染 SVG。"] = "Unable to render SVG.",
        ["SVG 画布为空。"] = "The SVG canvas is empty.",
        ["无法创建图像。"] = "Unable to create the image.",
        ["Word 文档缺少正文。"] = "The Word document has no body.",
        ["不支持的图片引用"] = "Unsupported image reference",
        ["不支持的图片格式"] = "Unsupported image format",
        ["无法读取图片"] = "Unable to read image",
        ["[无法转换的图片]"] = "[Image could not be converted]",
        ["部分 Word 图片格式无法读取，已在对应位置保留说明。"] = "Some Word images could not be read; placeholders have been kept in their positions.",
        ["仅支持本地图片"] = "Only local images are supported",
        ["图片未加载：{0}"] = "Image not loaded: {0}",
        ["[图片未加载：{0}]"] = "[Image not loaded: {0}]",
    };

    public static string Get(string chinese, params object?[] arguments)
    {
        var template = CultureInfo.CurrentUICulture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? English.GetValueOrDefault(chinese, chinese) : chinese;
        return arguments.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, arguments);
    }

    // Culture flows with this invocation's ExecutionContext across awaits and Task.Run.
    // Never mutate DefaultThreadCurrentCulture in a worker shared by concurrent calls.
    public static IDisposable Use(string? language) => new CultureScope(language);
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
        public CultureScope(string? language)
        {
            var culture = CultureInfo.GetCultureInfo(language?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true ? "en-US" : "zh-CN");
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
        public void Dispose()
        {
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _uiCulture;
        }
    }
}
