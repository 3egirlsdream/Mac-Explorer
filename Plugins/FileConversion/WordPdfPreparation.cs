using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using SkiaSharp;
using W = DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace MacExplorer.Services.Impl;

internal static class WordPdfPreparation
{
    // AppKit's OOXML reader omits DrawingML images and named styles. A temporary
    // copy expands basic styles and carries images through explicit attachment markers.
    public static string Prepare(string input, string work, List<string> warnings, CancellationToken token)
    {
        var attachments = new List<Attachment>();
        using (var package = WordprocessingDocument.Open(input, true))
        {
            var part = package.MainDocumentPart ?? throw new InvalidDataException("Word 文档缺少正文。");
            var body = part.Document.Body ?? throw new InvalidDataException("Word 文档缺少正文。");
            var styles = part.StyleDefinitionsPart?.Styles;
            var defaults = styles?.GetFirstChild<W.DocDefaults>()?.RunPropertiesDefault?.RunPropertiesBaseStyle;
            var counts = new Dictionary<(int Id, int Level), int>();
            foreach (var paragraph in body.Descendants<W.Paragraph>())
            {
                token.ThrowIfCancellationRequested();
                var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                var style = styles?.Elements<W.Style>().FirstOrDefault(style => style.StyleId == styleId);
                foreach (var run in paragraph.Elements<W.Run>())
                {
                    run.RunProperties ??= new W.RunProperties();
                    ApplyMissing(run.RunProperties, style?.StyleRunProperties);
                    ApplyMissing(run.RunProperties, defaults);
                }
                var numbering = paragraph.ParagraphProperties?.NumberingProperties;
                if (numbering?.NumberingId?.Val?.Value is int numberId)
                {
                    var depth = numbering.NumberingLevelReference?.Val?.Value ?? 0;
                    var definitions = part.NumberingDefinitionsPart?.Numbering;
                    var instance = definitions?.Elements<W.NumberingInstance>().FirstOrDefault(item => item.NumberID == numberId);
                    var definition = definitions?.Elements<W.AbstractNum>().FirstOrDefault(item => item.AbstractNumberId == instance?.AbstractNumId?.Val);
                    var level = definition?.Elements<W.Level>().FirstOrDefault(item => item.LevelIndex == depth);
                    var format = level?.NumberingFormat?.Val?.Value;
                    if (format == W.NumberFormatValues.Decimal || format == W.NumberFormatValues.Bullet)
                    {
                        var key = (numberId, depth);
                        counts[key] = counts.TryGetValue(key, out var previous) ? previous + 1 : level?.StartNumberingValue?.Val?.Value ?? 1;
                        foreach (var lower in counts.Keys.Where(key => key.Id == numberId && key.Level > depth).ToArray()) counts.Remove(lower);
                        var label = format == W.NumberFormatValues.Bullet ? "•" : level?.LevelText?.Val?.Value ?? "%1.";
                        for (var index = 0; index <= depth; index++) label = label.Replace("%" + (index + 1), counts.GetValueOrDefault((numberId, index), 1).ToString());
                        var prefix = new W.Run(new W.Text(label + " ") { Space = SpaceProcessingModeValues.Preserve });
                        paragraph.InsertAfter(prefix, paragraph.ParagraphProperties!);
                        var indent = level?.GetFirstChild<W.PreviousParagraphProperties>()?.Indentation;
                        if (indent != null) paragraph.ParagraphProperties!.Indentation = (W.Indentation)indent.CloneNode(true);
                        numbering.Remove();
                    }
                }
            }
            foreach (var drawing in body.Descendants<W.Drawing>().ToArray())
            {
                token.ThrowIfCancellationRequested();
                var marker = "MACEXPLORERIMAGE" + Guid.NewGuid().ToString("N");
                try
                {
                    var relationship = drawing.Descendants<A.Blip>().FirstOrDefault()?.Embed?.Value;
                    if (relationship == null || part.GetPartById(relationship) is not ImagePart imagePart)
                        throw new InvalidDataException("不支持的图片引用");
                    var file = Path.Combine(work, marker + ".png");
                    using var stream = imagePart.GetStream();
                    using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("不支持的图片格式");
                    new ConversionImageSize(codec.Info.Width, codec.Info.Height).Validate();
                    using var bitmap = SKBitmap.Decode(codec) ?? throw new InvalidDataException("无法读取图片");
                    using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                    using (var output = File.Create(file)) data.SaveTo(output);
                    var extent = drawing.Descendants<DW.Extent>().FirstOrDefault();
                    var width = (extent?.Cx?.Value ?? bitmap.Width * 9525L) / 12700d;
                    var height = (extent?.Cy?.Value ?? bitmap.Height * 9525L) / 12700d;
                    if (width <= 0 || height <= 0) { width = bitmap.Width * .75; height = bitmap.Height * .75; }
                    var scale = Math.Min(1, Math.Min(481d / width, 700d / height));
                    attachments.Add(new(marker, file, width * scale, height * scale));
                    drawing.InsertAfterSelf(new W.Text(marker));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
                {
                    drawing.InsertAfterSelf(new W.Text("[无法转换的图片]"));
                    warnings.Add("部分 Word 图片格式无法读取，已在对应位置保留说明。");
                }
                drawing.Remove();
            }
            part.Document.Save();
        }
        FileConversionService.NormalizeDocxRelationships(input);
        var manifest = Path.Combine(work, "attachments.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(attachments));
        return manifest;
    }

    private static void ApplyMissing(W.RunProperties target, OpenXmlCompositeElement? source)
    {
        if (source == null) return;
        foreach (var property in source.ChildElements)
            if (!target.ChildElements.Any(existing => existing.LocalName == property.LocalName && existing.NamespaceUri == property.NamespaceUri))
                target.AddChild(property.CloneNode(true), true);
    }

    private record Attachment(string Marker, string Path, double Width, double Height);
}
