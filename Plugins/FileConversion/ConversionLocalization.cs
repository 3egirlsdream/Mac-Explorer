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
        ["未知的转换命令。"] = "Unknown conversion command.",
        ["请至少选择一个本地文件。"] = "Select at least one local file.",
        ["图像转换每次只能处理一个文件。"] = "Image conversion processes one file at a time.",
        ["文件转换仅支持本地文件。"] = "Conversion supports local files only.",
        ["此文件不支持所选转换格式：{0}"] = "This file does not support the selected format: {0}",
        ["转为 {0}"] = "Convert to {0}",
        ["正在转换 {0}"] = "Converting {0}",
        ["正在转换 {0}（{1}/{2}）"] = "Converting {0} ({1}/{2})",
    };

    private static class FrenchCatalog
    {
        static FrenchCatalog() { }
        internal static readonly IReadOnlyDictionary<string, string> Strings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["无法确定图像尺寸。"] = "Impossible de déterminer les dimensions de l’image.",
            ["此文件不支持所选转换格式。"] = "Ce fichier ne prend pas en charge le format choisi.",
            ["源文件不存在。"] = "Le fichier source n’existe pas.",
            ["WebP 动画仅导出第一帧，本次转换不保留动画。"] = "Seule la première image WebP est exportée ; l’animation n’est pas conservée.",
            ["Word 文件损坏、已加密或格式无效。"] = "Le fichier Word est endommagé, chiffré ou non valide.",
            ["已按基础文档转换；复杂分页、浮动对象和页眉页脚可能与 Word 不同。"] = "Conversion en document simple ; la pagination complexe, les objets flottants, les en-têtes et pieds de page peuvent différer de Word.",
            ["转换未生成有效文件。"] = "La conversion n’a pas produit de fichier valide.",
            ["转换超时，请尝试更小或更简单的文件。"] = "Délai de conversion dépassé. Essayez un fichier plus petit ou plus simple.",
            ["Word 文档缺少根关系文件。"] = "Le document Word ne contient pas de fichier de relations racine.",
            ["文件包含二进制内容，无法作为文本转换。"] = "Le fichier contient des données binaires et ne peut pas être converti comme texte.",
            ["无法解码文本，请先保存为 UTF-8 或带 BOM 的 Unicode 文本。"] = "Impossible de décoder le texte. Enregistrez-le en UTF-8 ou en Unicode avec BOM.",
            ["缺少内置转换组件，请重新安装应用。"] = "Le composant de conversion intégré est absent. Réinstallez l’app.",
            ["无法启动转换组件。"] = "Impossible de démarrer le composant de conversion.",
            ["转换组件运行超时。"] = "Le composant de conversion a dépassé le délai imparti.",
            ["转换组件执行失败。"] = "Échec du composant de conversion.",
            ["无效的 SVG 文件。"] = "Fichier SVG non valide.",
            ["SVG 包含不支持的脚本或嵌入网页。"] = "Le SVG contient des scripts ou du contenu web intégré non pris en charge.",
            ["SVG 引用了外部资源，请先将资源嵌入文件。"] = "Le SVG référence des ressources externes. Intégrez-les d’abord au fichier.",
            ["SVG 包含外部样式资源。"] = "Le SVG contient des styles externes.",
            ["SVG 尺寸无效。"] = "Dimensions SVG non valides.",
            ["无法渲染 SVG。"] = "Impossible d’afficher le SVG.",
            ["SVG 画布为空。"] = "Le canevas SVG est vide.",
            ["无法创建图像。"] = "Impossible de créer l’image.",
            ["Word 文档缺少正文。"] = "Le document Word ne contient pas de corps.",
            ["不支持的图片引用"] = "Référence d’image non prise en charge",
            ["不支持的图片格式"] = "Format d’image non pris en charge",
            ["无法读取图片"] = "Impossible de lire l’image",
            ["[无法转换的图片]"] = "[L’image n’a pas pu être convertie]",
            ["部分 Word 图片格式无法读取，已在对应位置保留说明。"] = "Certaines images Word n’ont pas pu être lues ; des indications ont été conservées à leur emplacement.",
            ["仅支持本地图片"] = "Seules les images locales sont prises en charge",
            ["图片未加载：{0}"] = "Image non chargée : {0}",
            ["[图片未加载：{0}]"] = "[Image non chargée : {0}]",
            ["未知的转换命令。"] = "Commande de conversion inconnue.",
            ["请至少选择一个本地文件。"] = "Sélectionnez au moins un fichier local.",
            ["图像转换每次只能处理一个文件。"] = "La conversion d’images traite un fichier à la fois.",
            ["文件转换仅支持本地文件。"] = "La conversion prend uniquement en charge les fichiers locaux.",
            ["此文件不支持所选转换格式：{0}"] = "Ce fichier ne prend pas en charge le format choisi : {0}",
            ["转为 {0}"] = "Convertir en {0}",
            ["正在转换 {0}"] = "Conversion de {0}",
            ["正在转换 {0}（{1}/{2}）"] = "Conversion de {0} ({1}/{2})",
        };
    }

    private static class TraditionalCatalog
    {
        static TraditionalCatalog() { }
        internal static readonly IReadOnlyDictionary<string, string> Strings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["无法确定图像尺寸。"] = "無法確定圖像尺寸。",
            ["此文件不支持所选转换格式。"] = "此檔案不支持所選轉換格式。",
            ["源文件不存在。"] = "源檔案不存在。",
            ["WebP 动画仅导出第一帧，本次转换不保留动画。"] = "WebP 動畫僅導出第一幀，本次轉換不保留動畫。",
            ["Word 文件损坏、已加密或格式无效。"] = "Word 檔案損壞、已加密或格式無效。",
            ["已按基础文档转换；复杂分页、浮动对象和页眉页脚可能与 Word 不同。"] = "已按基礎文件轉換；複雜分頁、浮動對象和頁眉頁腳可能與 Word 不同。",
            ["转换未生成有效文件。"] = "轉換未生成有效檔案。",
            ["转换超时，请尝试更小或更简单的文件。"] = "轉換超時，請嘗試更小或更簡單的檔案。",
            ["Word 文档缺少根关系文件。"] = "Word 文件缺少根關係檔案。",
            ["文件包含二进制内容，无法作为文本转换。"] = "檔案包含二進制內容，無法作為文本轉換。",
            ["无法解码文本，请先保存为 UTF-8 或带 BOM 的 Unicode 文本。"] = "無法解碼文本，請先儲存為 UTF-8 或帶 BOM 的 Unicode 文本。",
            ["缺少内置转换组件，请重新安装应用。"] = "缺少內置轉換元件，請重新安裝應用。",
            ["无法启动转换组件。"] = "無法啓動轉換元件。",
            ["转换组件运行超时。"] = "轉換元件運行超時。",
            ["转换组件执行失败。"] = "轉換元件執行失敗。",
            ["无效的 SVG 文件。"] = "無效的 SVG 檔案。",
            ["SVG 包含不支持的脚本或嵌入网页。"] = "SVG 包含不支持的腳本或嵌入網頁。",
            ["SVG 引用了外部资源，请先将资源嵌入文件。"] = "SVG 引用了外部資源，請先將資源嵌入檔案。",
            ["SVG 包含外部样式资源。"] = "SVG 包含外部樣式資源。",
            ["SVG 尺寸无效。"] = "SVG 尺寸無效。",
            ["无法渲染 SVG。"] = "無法渲染 SVG。",
            ["SVG 画布为空。"] = "SVG 畫布為空。",
            ["无法创建图像。"] = "無法創建圖像。",
            ["Word 文档缺少正文。"] = "Word 文件缺少正文。",
            ["不支持的图片引用"] = "不支持的圖片引用",
            ["不支持的图片格式"] = "不支持的圖片格式",
            ["无法读取图片"] = "無法讀取圖片",
            ["[无法转换的图片]"] = "[無法轉換的圖片]",
            ["部分 Word 图片格式无法读取，已在对应位置保留说明。"] = "部分 Word 圖片格式無法讀取，已在對應位置保留說明。",
            ["仅支持本地图片"] = "僅支持本地圖片",
            ["图片未加载：{0}"] = "圖片未加載：{0}",
            ["[图片未加载：{0}]"] = "[圖片未加載：{0}]",
            ["未知的转换命令。"] = "未知的轉換命令。",
            ["请至少选择一个本地文件。"] = "請至少選擇一個本地檔案。",
            ["图像转换每次只能处理一个文件。"] = "圖像轉換每次只能處理一個檔案。",
            ["文件转换仅支持本地文件。"] = "檔案轉換僅支持本地檔案。",
            ["此文件不支持所选转换格式：{0}"] = "此檔案不支持所選轉換格式：{0}",
            ["转为 {0}"] = "轉為 {0}",
            ["正在转换 {0}"] = "正在轉換 {0}",
            ["正在转换 {0}（{1}/{2}）"] = "正在轉換 {0}（{1}/{2}）",
        };
    }

    public static string Get(string chinese, params object?[] arguments)
    {
        var language = CultureInfo.CurrentUICulture.Name;
        var template = language.StartsWith("fr", StringComparison.OrdinalIgnoreCase)
            ? FrenchCatalog.Strings.GetValueOrDefault(chinese, English.GetValueOrDefault(chinese, chinese))
            : IsTraditional(language) ? TraditionalCatalog.Strings.GetValueOrDefault(chinese, chinese)
            : language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? English.GetValueOrDefault(chinese, chinese) : chinese;
        return arguments.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, arguments);
    }

    private static bool IsTraditional(string? language) => language?.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) == true
        || language?.ToLowerInvariant() is "zh-tw" or "zh-hk" or "zh-mo" or "zh-cht";

    // Culture flows with this invocation's ExecutionContext across awaits and Task.Run.
    // Never mutate DefaultThreadCurrentCulture in a worker shared by concurrent calls.
    public static IDisposable Use(string? language) => new CultureScope(language);
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
        public CultureScope(string? language)
        {
            var culture = CultureInfo.GetCultureInfo(IsTraditional(language) ? "zh-Hant"
                : language?.StartsWith("fr", StringComparison.OrdinalIgnoreCase) == true ? "fr-FR"
                : language?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true ? "en-US" : "zh-CN");
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
