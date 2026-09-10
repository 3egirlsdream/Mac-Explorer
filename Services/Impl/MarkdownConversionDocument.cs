using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;
using SkiaSharp;
using W = DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace MacExplorer.Services.Impl;

internal sealed class MarkdownConversionDocument
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().DisableHtml().Build();
    private readonly MarkdownDocument _document;
    private readonly string _work;
    private readonly CancellationToken _token;
    private MainDocumentPart _part = null!;
    private W.Numbering _numbering = null!;
    private int _numberingId;
    private uint _imageId;

    public MarkdownConversionDocument(string markdown, string source, string work, List<string> warnings, CancellationToken token)
    {
        _document = Markdown.Parse(markdown, Pipeline);
        _work = work;
        _token = token;
        foreach (var link in _document.Descendants<LinkInline>().ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (!link.IsImage)
            {
                if (!IsSafeLink(link.Url)) link.Url = "";
                continue;
            }
            var original = link.Url ?? "";
            try
            {
                string path;
                if (Uri.TryCreate(original, UriKind.Absolute, out var uri))
                {
                    if (!uri.IsFile || !string.IsNullOrEmpty(uri.Host)) throw new IOException("仅支持本地图片");
                    path = uri.LocalPath;
                }
                else path = Path.GetFullPath(Uri.UnescapeDataString(original), Path.GetDirectoryName(source)!);
                if (!File.Exists(path)) throw new FileNotFoundException();
                var fileName = "image-" + Guid.NewGuid().ToString("N") + ".png";
                var output = Path.Combine(work, fileName);
                if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
                    FileConversionService.RenderSvg(path, output, FileConversionService.ReadSvg(path).Size);
                else
                {
                    using var stream = File.OpenRead(path);
                    using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException();
                    new ConversionImageSize(codec.Info.Width, codec.Info.Height).Validate();
                    using var bitmap = SKBitmap.Decode(codec) ?? throw new InvalidDataException();
                    using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                    using var file = File.Create(output);
                    png.SaveTo(file);
                }
                link.Url = fileName;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Xml.XmlException)
            {
                warnings.Add($"图片未加载：{original}");
                link.ReplaceBy(new LiteralInline($"[图片未加载：{original}]"));
            }
        }
    }

    private static bool IsSafeLink(string? url)
        => string.IsNullOrEmpty(url) || url.StartsWith('#')
           || (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto");

    private static readonly Lazy<string> HighlightScript = new(() => ReadResource("highlight.min.js") + """

        // Highlight declared languages only: plain text and unknown languages remain literal.
        for (const code of document.querySelectorAll('pre > code')) {
            const language = Array.from(code.classList).find(c => c.startsWith('language-'))?.slice(9);
            if (language && hljs.getLanguage(language) && code.textContent.length <= 100000)
                hljs.highlightElement(code);
        }
        """);
    private static readonly Lazy<string> HighlightStyles = new(() => ReadResource("github.min.css"));

    private static string ReadResource(string name)
    {
        using var stream = typeof(MarkdownConversionDocument).Assembly.GetManifestResourceStream("MacExplorer.Resources.Conversion." + name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public string ToHtml()
    {
        var script = HighlightScript.Value;
        // Permit only the bundled highlighter; Markdown HTML and user scripts stay disabled.
        var hash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(script)));
        return """
        <!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
        """ + $"<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; script-src 'sha256-{hash}'; font-src 'none';\">" + """
        <style>
        """ + HighlightStyles.Value + """
        @page { size: A4; margin: 20mm; }
        * { -webkit-print-color-adjust: exact; print-color-adjust: exact; box-sizing: border-box; }
        body { margin: 0; font: 11pt -apple-system, 'PingFang SC', sans-serif; line-height: 1.65; color: #24292f; overflow-wrap: anywhere; }
        h1,h2,h3,h4,h5,h6 { margin: 18pt 0 9pt; line-height: 1.35; page-break-after: avoid; break-after: avoid-page; }
        h1 { font-size: 24pt; padding-bottom: 7pt; border-bottom: 1px solid #d0d7de; }
        h2 { font-size: 19pt; padding-bottom: 5pt; border-bottom: 1px solid #d8dee4; }
        h3 { font-size: 15pt; } h4 { font-size: 12pt; } h5,h6 { font-size: 11pt; }
        body > :first-child { margin-top: 0; }
        p { margin: 0 0 10pt; }
        p,pre,blockquote,tr { page-break-inside: avoid; break-inside: avoid-page; }
        p,li,pre { orphans: 3; widows: 3; }
        a { color: #0969da; text-decoration: underline; }
        ul,ol { margin: 0 0 10pt; padding-left: 22pt; } li { margin: 3pt 0; }
        li > p { margin-bottom: 5pt; } li > ul,li > ol { margin-bottom: 0; }
        pre,code { font-family: Menlo, 'PingFang SC', monospace; font-size: 9pt; tab-size: 4; }
        code { padding: 1pt 3pt; border-radius: 3pt; background: #eff1f3; }
        pre { margin: 0 0 12pt; white-space: pre-wrap; overflow-wrap: anywhere; line-height: 1.55; padding: 10pt; border: 1px solid #d8dee4; border-radius: 4pt; background: #f6f8fa; }
        pre code,pre code.hljs { display: inline; padding: 0; background: transparent; overflow: visible; white-space: inherit; }
        table { width: 100%; border-collapse: collapse; table-layout: fixed; margin: 0 0 12pt; font-size: 10pt; }
        th,td { border: 1px solid #d0d7de; padding: 6pt 8pt; vertical-align: top; }
        th { background: #eff1f3; font-weight: 600; } tbody tr:nth-child(even) { background: #f6f8fa; }
        thead { display: table-header-group; }
        img { max-width: 100%; max-height: 680pt; height: auto; }
        blockquote { margin: 0 0 12pt; border-left: 3pt solid #afb8c1; padding: 4pt 12pt; color: #57606a; background: #f6f8fa; }
        blockquote > :last-child { margin-bottom: 0; }
        hr { border: 0; border-top: 1px solid #d0d7de; margin: 16pt 0; }
        </style></head><body>
        """ + Markdown.ToHtml(_document, Pipeline) + "<script>" + script + "</script></body></html>";
    }

    public void WriteDocx(string path)
    {
        WritePackage(path);
        FileConversionService.NormalizeDocxRelationships(path);
    }

    private void WritePackage(string path)
    {
        using var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        _part = package.AddMainDocumentPart();
        var body = new W.Body();
        _part.Document = new W.Document(body);
        var styles = _part.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new W.Styles(new W.DocDefaults(
            new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle(
                new W.RunFonts { Ascii = "Helvetica", HighAnsi = "Helvetica", EastAsia = "PingFang SC" },
                new W.FontSize { Val = "22" })),
            new W.ParagraphPropertiesDefault(new W.ParagraphPropertiesBaseStyle(new W.SpacingBetweenLines { After = "120", Line = "300", LineRule = W.LineSpacingRuleValues.Auto }))));
        for (var level = 1; level <= 6; level++)
            styles.Styles.Append(new W.Style(new W.StyleName { Val = "heading " + level },
                new W.StyleParagraphProperties(new W.KeepNext(), new W.OutlineLevel { Val = level - 1 }),
                new W.StyleRunProperties(new W.Bold(), new W.FontSize { Val = new[] { "40", "32", "28", "26", "24", "22" }[level - 1] }))
                { Type = W.StyleValues.Paragraph, StyleId = "Heading" + level });
        _numbering = new W.Numbering();
        _part.AddNewPart<NumberingDefinitionsPart>().Numbering = _numbering;
        WriteBlocks(_document, body);
        if (!body.Elements<W.Paragraph>().Any() && !body.Elements<W.Table>().Any()) body.Append(new W.Paragraph());
        body.Append(new W.SectionProperties(new W.PageSize { Width = 11906, Height = 16838 },
            new W.PageMargin { Top = 1134, Bottom = 1134, Left = 1134, Right = 1134, Header = 0, Footer = 0, Gutter = 0 }));
        _part.Document.Save();
    }

    private void WriteBlocks(ContainerBlock blocks, OpenXmlCompositeElement target, int depth = 0, int? numberingId = null, bool quote = false)
    {
        var first = true;
        foreach (var block in blocks)
        {
            _token.ThrowIfCancellationRequested();
            switch (block)
            {
                case Table table:
                    WriteTable(table, target);
                    break;
                case ListBlock list:
                    var id = AddNumbering(list, depth);
                    foreach (var item in list.OfType<ListItemBlock>()) WriteBlocks(item, target, depth + 1, id, quote);
                    break;
                case QuoteBlock quoted:
                    WriteBlocks(quoted, target, depth, null, true);
                    break;
                case ThematicBreakBlock:
                    target.Append(new W.Paragraph(new W.Run(new W.Text("────────────"))));
                    break;
                case LeafBlock leaf:
                    var properties = new W.ParagraphProperties();
                    if (leaf is HeadingBlock heading) properties.Append(new W.ParagraphStyleId { Val = "Heading" + heading.Level });
                    if (numberingId.HasValue && first)
                        properties.Append(new W.NumberingProperties(new W.NumberingLevelReference { Val = 0 }, new W.NumberingId { Val = numberingId.Value }));
                    else if (depth > 0 || quote)
                        properties.Append(new W.Indentation { Left = ((depth + (quote ? 1 : 0)) * 360).ToString() });
                    var paragraph = new W.Paragraph(properties);
                    if (leaf is CodeBlock code)
                    {
                        paragraph.ParagraphProperties!.Append(new W.Shading { Fill = "F4F4F4", Val = W.ShadingPatternValues.Clear });
                        var run = new W.Run(new W.RunProperties(new W.RunFonts { Ascii = "Menlo", HighAnsi = "Menlo", EastAsia = "PingFang SC" }, new W.FontSize { Val = "20" }));
                        AddText(run, code.Lines.ToString());
                        paragraph.Append(run);
                    }
                    else if (leaf.Inline != null) WriteInlines(leaf.Inline, paragraph, new W.RunProperties());
                    else paragraph.Append(new W.Run(new W.Text(leaf.Lines.ToString()) { Space = SpaceProcessingModeValues.Preserve }));
                    target.Append(paragraph);
                    first = false;
                    break;
                case ContainerBlock container:
                    WriteBlocks(container, target, depth, numberingId, quote);
                    break;
            }
        }
    }

    private int AddNumbering(ListBlock list, int depth)
    {
        var id = ++_numberingId;
        var level = new W.Level(
            new W.StartNumberingValue { Val = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1 },
            new W.NumberingFormat { Val = list.IsOrdered ? W.NumberFormatValues.Decimal : W.NumberFormatValues.Bullet },
            new W.LevelText { Val = list.IsOrdered ? "%1." : "•" },
            new W.LevelJustification { Val = W.LevelJustificationValues.Left },
            new W.PreviousParagraphProperties(new W.Indentation { Left = ((depth + 1) * 360).ToString(), Hanging = "240" })) { LevelIndex = 0 };
        // Abstract definitions precede instances in the numbering schema.
        var definition = new W.AbstractNum(level) { AbstractNumberId = id };
        var instance = _numbering.Elements<W.NumberingInstance>().FirstOrDefault();
        if (instance == null) _numbering.Append(definition); else _numbering.InsertBefore(definition, instance);
        _numbering.Append(new W.NumberingInstance(new W.AbstractNumId { Val = id }) { NumberID = id });
        return id;
    }

    private void WriteTable(Table table, OpenXmlCompositeElement target)
    {
        var rows = table.OfType<TableRow>().ToArray();
        var columns = Math.Max(1, rows.Select(row => row.Count).DefaultIfEmpty(1).Max());
        var result = new W.Table(new W.TableProperties(
            new W.TableWidth { Width = "5000", Type = W.TableWidthUnitValues.Pct },
            new W.TableBorders(new W.TopBorder { Val = W.BorderValues.Single, Size = 4 }, new W.LeftBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.BottomBorder { Val = W.BorderValues.Single, Size = 4 }, new W.RightBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4 }, new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4 }),
            new W.TableLayout { Type = W.TableLayoutValues.Fixed }));
        result.Append(new W.TableGrid(Enumerable.Range(0, columns).Select(_ => new W.GridColumn { Width = (9638 / columns).ToString() })));
        foreach (var row in rows)
        {
            var outputRow = new W.TableRow();
            if (row.IsHeader) outputRow.Append(new W.TableRowProperties(new W.TableHeader()));
            foreach (var cell in row.OfType<TableCell>())
            {
                var outputCell = new W.TableCell(new W.TableCellProperties(new W.TableCellWidth { Width = (9638 / columns).ToString(), Type = W.TableWidthUnitValues.Dxa }));
                WriteBlocks(cell, outputCell);
                if (outputCell.LastChild is not W.Paragraph) outputCell.Append(new W.Paragraph());
                if (row.IsHeader)
                    foreach (var run in outputCell.Descendants<W.Run>())
                    {
                        run.RunProperties ??= new W.RunProperties();
                        run.RunProperties.Bold = new W.Bold();
                    }
                outputRow.Append(outputCell);
            }
            result.Append(outputRow);
        }
        target.Append(result);
    }

    private void WriteInlines(ContainerInline container, OpenXmlCompositeElement target, W.RunProperties style)
    {
        foreach (var inline in container)
        {
            _token.ThrowIfCancellationRequested();
            switch (inline)
            {
                case LiteralInline literal: AppendText(target, literal.Content.ToString(), style); break;
                case CodeInline code:
                    var mono = (W.RunProperties)style.CloneNode(true);
                    mono.RunFonts = new W.RunFonts { Ascii = "Menlo", HighAnsi = "Menlo", EastAsia = "PingFang SC" };
                    AppendText(target, code.Content, mono);
                    break;
                case LineBreakInline line:
                    if (line.IsHard) target.Append(new W.Run(new W.Break())); else AppendText(target, " ", style);
                    break;
                case EmphasisInline emphasis:
                    var emphasized = (W.RunProperties)style.CloneNode(true);
                    if (emphasis.DelimiterCount >= 2) emphasized.Bold = new W.Bold();
                    if (emphasis.DelimiterCount % 2 == 1) emphasized.Italic = new W.Italic();
                    WriteInlines(emphasis, target, emphasized);
                    break;
                case LinkInline link when link.IsImage:
                    WriteImage(link.Url!, target);
                    break;
                case LinkInline link:
                    if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && IsSafeLink(link.Url))
                    {
                        var relation = _part.AddHyperlinkRelationship(uri, true);
                        var hyperlink = new W.Hyperlink { Id = relation.Id };
                        var linked = (W.RunProperties)style.CloneNode(true);
                        linked.Color = new W.Color { Val = "175AC1" };
                        linked.Underline = new W.Underline { Val = W.UnderlineValues.Single };
                        WriteInlines(link, hyperlink, linked);
                        target.Append(hyperlink);
                    }
                    else WriteInlines(link, target, style);
                    break;
                case AutolinkInline auto:
                    var url = auto.IsEmail ? "mailto:" + auto.Url : auto.Url;
                    if (Uri.TryCreate(url, UriKind.Absolute, out var autoUri) && IsSafeLink(url))
                    {
                        var hyperlink = new W.Hyperlink { Id = _part.AddHyperlinkRelationship(autoUri, true).Id };
                        AppendText(hyperlink, auto.Url, style);
                        target.Append(hyperlink);
                    }
                    else AppendText(target, auto.Url, style);
                    break;
                case ContainerInline nested: WriteInlines(nested, target, style); break;
                case HtmlInline html: AppendText(target, html.Tag, style); break;
            }
        }
    }

    private static void AppendText(OpenXmlCompositeElement target, string text, W.RunProperties style)
    {
        var run = new W.Run((W.RunProperties)style.CloneNode(true));
        AddText(run, text);
        target.Append(run);
    }

    private static void AddText(W.Run run, string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var line = 0; line < lines.Length; line++)
        {
            if (line > 0) run.Append(new W.Break());
            var pieces = lines[line].Split('\t');
            for (var piece = 0; piece < pieces.Length; piece++)
            {
                if (piece > 0) run.Append(new W.TabChar());
                run.Append(new W.Text(pieces[piece]) { Space = SpaceProcessingModeValues.Preserve });
            }
        }
    }

    private void WriteImage(string fileName, OpenXmlCompositeElement target)
    {
        var path = Path.Combine(_work, fileName);
        using var bitmap = SKBitmap.Decode(path);
        var image = _part.AddImagePart(ImagePartType.Png);
        using (var stream = File.OpenRead(path)) image.FeedData(stream);
        var scale = Math.Min(1d, Math.Min(642d / bitmap.Width, 900d / bitmap.Height));
        var width = (long)(bitmap.Width * scale * 9525);
        var height = (long)(bitmap.Height * scale * 9525);
        var id = ++_imageId;
        var picture = new PIC.Picture(
            new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = id, Name = fileName }, new PIC.NonVisualPictureDrawingProperties()),
            new PIC.BlipFill(new A.Blip { Embed = _part.GetIdOfPart(image) }, new A.Stretch(new A.FillRectangle())),
            new PIC.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = width, Cy = height }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));
        var drawing = new W.Drawing(new DW.Inline(new DW.Extent { Cx = width, Cy = height },
            new DW.DocProperties { Id = id, Name = fileName },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(new A.GraphicData(picture) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            { DistanceFromTop = 0, DistanceFromBottom = 0, DistanceFromLeft = 0, DistanceFromRight = 0 });
        target.Append(new W.Run(drawing));
    }
}
