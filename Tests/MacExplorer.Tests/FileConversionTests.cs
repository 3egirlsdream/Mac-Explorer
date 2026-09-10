using System.Diagnostics;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using SkiaSharp;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MacExplorer.Tests;

public sealed class FileConversionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fkfinder-conversion-test-" + Guid.NewGuid().ToString("N"));
    public FileConversionTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private string Write(string name, string text) { var path = Path.Combine(_root, name); File.WriteAllText(path, text); return path; }
    private static FileConversionService Service()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "MacExplorer.csproj"))) root = root.Parent;
        var config = AppContext.BaseDirectory.Contains("/Release/") ? "Release" : "Debug";
        var helper = Path.Combine(root!.FullName, "bin", config, "net10.0", "osx-arm64", "MacExplorer.FileConversion");
        Assert.True(File.Exists(helper), "Build the application before native conversion tests.");
        return new FileConversionService(helper, TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData("a.DOC", "Pdf")]
    [InlineData("a.docx", "Pdf")]
    [InlineData("a.MD", "Docx,Pdf")]
    [InlineData("a.markdown", "Docx,Pdf")]
    [InlineData("a.json", "Docx,Pdf")]
    [InlineData("Dockerfile", "Docx,Pdf")]
    [InlineData("a.SVG", "Png,Jpg")]
    [InlineData("a.ico", "Png,Jpg")]
    [InlineData("a.icns", "Png,Jpg")]
    [InlineData("a.png", "")]
    [InlineData("a.pdf", "")]
    public void FormatsFollowSourceType(string name, string formats)
        => Assert.Equal(formats, string.Join(',', new FileConversionService().GetAvailableFormats(Path.Combine(_root, name))));

    [Fact]
    public void DirectoriesAndRemotePathsAreNotConvertible()
    {
        var directory = Path.Combine(_root, "folder.md"); Directory.CreateDirectory(directory);
        Assert.Empty(new FileConversionService().GetAvailableFormats(directory));
        Assert.Empty(new FileConversionService().GetAvailableFormats("sftp://server/test.md"));
    }

    [Fact]
    public void StrictUnicodeDecodingNeverSilentlyReplacesInvalidBytes()
    {
        const string text = "中文 # ` < >\n最后一行";
        foreach (var encoding in new Encoding[] { new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true), new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true) })
        {
            var path = Path.Combine(_root, "encoded.txt");
            File.WriteAllBytes(path, encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray());
            Assert.Equal(text, FileConversionService.ReadText(path));
        }
        var invalid = Path.Combine(_root, "invalid.txt"); File.WriteAllBytes(invalid, [0xff, 0xff, 0xc0]);
        Assert.Throws<InvalidDataException>(() => FileConversionService.ReadText(invalid));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  # 原文\n```\n<tag> & 文本\n\t最后一行")]
    [InlineData("第一行\n\n最后一行\n")]
    public async Task TextDocxPreservesLiteralContent(string text)
    {
        var source = Write("literal.txt", text);
        var result = await new FileConversionService().ConvertAsync(new(source, FileConversionFormat.Docx));
        using var doc = WordprocessingDocument.Open(result.OutputPath, false);
        var paragraph = Assert.Single(doc.MainDocumentPart!.Document.Body!.Elements<W.Paragraph>());
        var restored = string.Concat(paragraph.Descendants().Select(node => node switch
        {
            W.Text value => value.Text,
            W.Break => "\n",
            W.TabChar => "\t",
            _ => ""
        }));
        Assert.Equal(text, restored);
        Assert.Empty(new OpenXmlValidator().Validate(doc));
        Assert.Equal(text, File.ReadAllText(source));
        Assert.False(File.Exists(Path.Combine(_root, "literal.md")));
    }

    [Fact]
    public async Task MarkdownCreatesEditableValidStructuresAndDoesNotLoadRemoteImages()
    {
        using (var bitmap = new SKBitmap(12, 8))
        {
            bitmap.Erase(SKColors.Red);
            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(Path.Combine(_root, "local.png")); png.SaveTo(file);
        }
        var source = Write("sample.md", """
            # 中文标题
            **粗体**、*斜体*、`code` 和 [链接](https://example.com)
            > 引用

            3. 第三项
               - 嵌套项目
            4. 第四项

            | 名称 | 值 |
            | --- | --- |
            | 中文 | 123 |

            ![本地](local.png)
            ![远程](https://example.com/no-network.png)
            ![缺失](missing.png)

            ```mermaid
            graph TD; A --> B
            ```
            <script>alert('保留源码')</script>
            """);
        var result = await new FileConversionService().ConvertAsync(new(source, FileConversionFormat.Docx));
        Assert.Equal(2, result.Warnings.Count);
        using var doc = WordprocessingDocument.Open(result.OutputPath, false);
        Assert.Empty(new OpenXmlValidator().Validate(doc));
        var body = doc.MainDocumentPart!.Document.Body!;
        Assert.Single(body.Elements<W.Table>());
        Assert.Single(doc.MainDocumentPart.ImageParts);
        Assert.Single(body.Descendants<W.Hyperlink>());
        Assert.Contains(body.Descendants<W.NumberingProperties>(), _ => true);
        Assert.Contains(body.Descendants<W.Bold>(), _ => true);
        Assert.Contains("<script>alert('保留源码')</script>", body.InnerText);
        Assert.Contains("graph TD; A --> B", body.InnerText);
    }

    [Fact]
    public async Task CollisionAndCancellationLeaveSourcesAndExistingOutputsIntact()
    {
        var source = Write("same.txt", "original");
        var existing = Write("same.docx", "do not overwrite");
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => new FileConversionService().ConvertAsync(new(source, FileConversionFormat.Docx))));
        Assert.Equal(3, results.Select(result => result.OutputPath).Distinct().Count());
        Assert.Equal("do not overwrite", File.ReadAllText(existing));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileConversionService().ConvertAsync(new(source, FileConversionFormat.Docx), cts.Token));
        Assert.Empty(Directory.GetFiles(_root, ".MacExplorer*"));
        Assert.Equal("original", File.ReadAllText(source));
    }

    [Fact]
    public async Task SvgUsesCanvasSizeTransparencyAndWhiteJpegBackground()
    {
        var source = Write("icon.svg", """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 80 40"><defs><linearGradient id="g"><stop stop-color="red"/></linearGradient></defs><rect width="20" height="20" fill="url(#g)"/></svg>""");
        var service = new FileConversionService();
        Assert.Equal(new ConversionImageSize(80, 40), await service.GetImageSizeAsync(source));
        var png = await service.ConvertAsync(new(source, FileConversionFormat.Png, new(160, 80)));
        var jpg = await service.ConvertAsync(new(source, FileConversionFormat.Jpg, new(160, 80)));
        using var transparent = SKBitmap.Decode(png.OutputPath);
        using var opaque = SKBitmap.Decode(jpg.OutputPath);
        Assert.Equal(160, transparent.Width); Assert.Equal(80, transparent.Height);
        Assert.Equal(0, transparent.GetPixel(150, 70).Alpha);
        Assert.True(opaque.GetPixel(150, 70).Red > 245);
        Assert.True(opaque.GetPixel(150, 70).Green > 245);
        Assert.Equal(255, opaque.GetPixel(150, 70).Alpha);
        var external = Write("external.svg", """<svg xmlns="http://www.w3.org/2000/svg"><image href="https://example.com/a.png"/></svg>""");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetImageSizeAsync(external));
    }

    [Theory]
    [InlineData("width=\"2in\" height=\"1in\"", 192, 96)]
    [InlineData("viewBox=\"0 0 60 30\" width=\"120\"", 120, 60)]
    [InlineData("", 512, 512)]
    public void SvgSizeFallbacks(string attributes, int width, int height)
    {
        var path = Write("size.svg", $"<svg xmlns=\"http://www.w3.org/2000/svg\" {attributes}/>");
        Assert.Equal(new ConversionImageSize(width, height), FileConversionService.ReadSvg(path).Size);
    }

    [Fact]
    public async Task NativeWordAndMarkdownPdfContainCompleteMultipageText()
    {
        var service = Service();
        using (var bitmap = new SKBitmap(64, 32))
        {
            bitmap.Erase(SKColors.Red);
            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(Path.Combine(_root, "embedded.png")); png.SaveTo(file);
        }
        var source = Write("pages.md", "# 中文标题\n\n![图片](embedded.png)\n\n1. 第一项\n2. 第二项\n\n" + string.Concat(Enumerable.Range(0, 110).Select(i => $"第 {i} 段落 Hello world.\n\n")) + "最后一行 END");
        var docx = await service.ConvertAsync(new(source, FileConversionFormat.Docx));
        var originalDocx = File.ReadAllBytes(docx.OutputPath);
        var markdownPdf = await service.ConvertAsync(new(source, FileConversionFormat.Pdf));
        var wordPdf = await service.ConvertAsync(new(docx.OutputPath, FileConversionFormat.Pdf));
        Assert.Equal(originalDocx, File.ReadAllBytes(docx.OutputPath));
        foreach (var path in new[] { markdownPdf.OutputPath, wordPdf.OutputPath })
            Assert.Matches(@"/Subtype\s*/Image", Encoding.Latin1.GetString(File.ReadAllBytes(path)));
        var script = Write("inspect.swift", """
            import PDFKit
            for path in CommandLine.arguments.dropFirst() {
                guard let pdf = PDFDocument(url: URL(fileURLWithPath: path)), pdf.pageCount > 1,
                      let text = pdf.string, text.contains("END"), text.contains("Hello world") else { exit(1) }
                print(pdf.pageCount)
            }
            """);
        var info = new ProcessStartInfo("/usr/bin/swift") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { script, markdownPdf.OutputPath, wordPdf.OutputPath }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await stderr);
        Assert.All(new[] { markdownPdf.OutputPath, wordPdf.OutputPath }, path => Assert.True(new FileInfo(path).Length < 2_000_000));
    }

    [Fact]
    public async Task MarkdownPdfKeepsShortBlocksTogetherAndPrintsCodeColors()
    {
        var markdown = new StringBuilder("# 中文排版与代码\n\n```javascript\nconst greeting = \"你好\";\nfunction sayHello(name) {\n  // 保留颜色与缩进\n  return greeting + name;\n}\n```\n\n");
        for (var i = 0; i < 24; i++)
        {
            markdown.Append($"PARASTART{i:D2} ");
            markdown.Append(string.Concat(Enumerable.Repeat("这是一个应当尽量保持完整的中文段落。", 12)));
            markdown.Append($" PARAEND{i:D2}\n\n");
            markdown.Append($"```javascript\n// CODESTART{i:D2}\nconst count = 42;\nconst message = \"中文\";\nconsole.log(message);\n// CODEEND{i:D2}\n```\n\n");
        }
        markdown.Append("```text\nLONGSTART\n");
        for (var i = 0; i < 150; i++) markdown.Append($"Long code line {i:D3}\n");
        markdown.Append("LONGEND\n```\n\n<script>document.body.textContent = 'UNSAFE';</script>\n\nDOCUMENTEND");
        var source = Write("pagination.md", markdown.ToString());
        var result = await Service().ConvertAsync(new(source, FileConversionFormat.Pdf));
        var script = Write("pagination.swift", """
            import AppKit
            import PDFKit
            let pdf = PDFDocument(url: URL(fileURLWithPath: CommandLine.arguments[1]))!
            let pages = (0..<pdf.pageCount).map { pdf.page(at: $0)!.string ?? "" }
            func check(_ condition: Bool, _ message: String) {
                if !condition { fputs(message + "\n", stderr); exit(1) }
            }
            check(pages.count > 3 && pages.count < 30, "Unexpected pagination")
            for i in 0..<24 {
                let suffix = String(format: "%02d", i)
                for prefix in ["PARA", "CODE"] {
                    let start = pages.firstIndex { $0.contains(prefix + "START" + suffix) }
                    let end = pages.firstIndex { $0.contains(prefix + "END" + suffix) }
                    check(start != nil && start == end, "Split or missing \(prefix) \(suffix)")
                }
            }
            let all = pages.joined()
            check(all.contains("LONGSTART") && all.contains("LONGEND") && all.contains("DOCUMENTEND"), "Clipped long block")
            for i in 0..<150 { check(all.contains(String(format: "Long code line %03d", i)), "Missing long code line") }
            check(all.contains("document.body.textContent"), "Input HTML was executed")
            let page = pdf.page(at: 0)!
            let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 1190, pixelsHigh: 1684,
                bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
            let context = NSGraphicsContext(bitmapImageRep: bitmap)!.cgContext
            context.setFillColor(NSColor.white.cgColor)
            context.fill(CGRect(x: 0, y: 0, width: 1190, height: 1684))
            context.scaleBy(x: 2, y: 2)
            page.draw(with: .mediaBox, to: context)
            var red = 0, background = 0
            for y in 0..<bitmap.pixelsHigh {
                for x in 0..<bitmap.pixelsWide {
                    let color = bitmap.colorAt(x: x, y: y)!.usingColorSpace(.deviceRGB)!
                    if color.redComponent > 0.6 && color.redComponent > color.greenComponent * 1.7 && color.redComponent > color.blueComponent * 1.4 { red += 1 }
                    if abs(color.redComponent - 246.0/255) < 0.008 && abs(color.greenComponent - 248.0/255) < 0.008 && abs(color.blueComponent - 250.0/255) < 0.008 { background += 1 }
                }
            }
            check(red > 30, "Syntax colors were not printed: \(red)")
            check(background > 1000, "Code background was not printed: \(background)")
            print("\(pages.count) pages; syntax pixels \(red); background pixels \(background)")
            """);
        var info = new ProcessStartInfo("/usr/bin/swift") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(script);
        info.ArgumentList.Add(result.OutputPath);
        using var process = Process.Start(info)!;
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await stderr);
    }

    [Fact]
    public async Task NativeIconsDecodeLargestFrameAndDamagedFilesFail()
    {
        var service = Service();
        var icon = Path.Combine(_root, "multi.ico");
        var frames = new List<byte[]>();
        foreach (var size in new[] { 16, 64 })
        {
            using var bitmap = new SKBitmap(size, size); bitmap.Erase(SKColors.Blue);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100); frames.Add(data.ToArray());
        }
        using (var file = new BinaryWriter(File.Create(icon)))
        {
            file.Write((ushort)0); file.Write((ushort)1); file.Write((ushort)2);
            var offset = 6 + 16 * 2;
            for (var index = 0; index < frames.Count; index++)
            {
                var size = index == 0 ? 16 : 64;
                file.Write((byte)size); file.Write((byte)size); file.Write((ushort)0); file.Write((ushort)1); file.Write((ushort)32);
                file.Write(frames[index].Length); file.Write(offset); offset += frames[index].Length;
            }
            foreach (var frame in frames) file.Write(frame);
        }
        Assert.Equal(new ConversionImageSize(64, 64), await service.GetImageSizeAsync(icon));
        var result = await service.ConvertAsync(new(icon, FileConversionFormat.Png));
        using var image = SKBitmap.Decode(result.OutputPath); Assert.Equal(64, image.Width);
        var invalid = Write("broken.docx", "not a document");
        await Assert.ThrowsAsync<IOException>(() => service.ConvertAsync(new(invalid, FileConversionFormat.Pdf)));
        Assert.False(File.Exists(Path.ChangeExtension(invalid, ".pdf")));
    }

    [Fact]
    public async Task HelperTimeoutTerminatesProcess()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var helper = Write("slow-helper", "#!/bin/sh\nexec sleep 30\n");
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var service = new FileConversionService(helper, TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<TimeoutException>(() => service.RunHelperAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task ReadOnlyDestinationAndMalformedImageDoNotLeavePartialOutputs()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var source = Write("readonly.txt", "source content");
        var before = File.GetUnixFileMode(_root);
        File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new FileConversionService().ConvertAsync(new(source, FileConversionFormat.Docx)));
        }
        finally { File.SetUnixFileMode(_root, before); }
        Assert.False(File.Exists(Path.Combine(_root, "readonly.docx")));
        Assert.Empty(Directory.GetFiles(_root, ".MacExplorer*"));
        Write("broken.png", "invalid image");
        var markdown = Write("broken-image.md", "![说明](broken.png)\n\nEND");
        var result = await new FileConversionService().ConvertAsync(new(markdown, FileConversionFormat.Docx));
        Assert.Single(result.Warnings);
        using var document = WordprocessingDocument.Open(result.OutputPath, false);
        Assert.Contains("END", document.MainDocumentPart!.Document.InnerText);
    }
}
