using System.Diagnostics;
using System.IO.Compression;
using System.Globalization;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SkiaSharp;
using Svg.Skia;

namespace MacExplorer.Services.Impl;

public sealed class FileConversionService : IFileConversionService
{
    private readonly string _helperPath;
    private readonly TimeSpan _timeout;

    public FileConversionService() : this(Path.Combine(AppContext.BaseDirectory, "MacExplorer.FileConversion"), TimeSpan.FromMinutes(2)) { }
    internal FileConversionService(string helperPath, TimeSpan timeout) { _helperPath = helperPath; _timeout = timeout; }

    public IReadOnlyList<FileConversionFormat> GetAvailableFormats(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Directory.Exists(path)) return [];
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".doc" or ".docx" => [FileConversionFormat.Pdf],
            ".svg" or ".ico" or ".icns" => [FileConversionFormat.Png, FileConversionFormat.Jpg],
            ".md" or ".markdown" => [FileConversionFormat.Docx, FileConversionFormat.Pdf],
            _ when TextFileTypes.IsText(path) => [FileConversionFormat.Docx, FileConversionFormat.Pdf],
            _ => []
        };
    }

    public async Task<ConversionImageSize> GetImageSizeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
            return await Task.Run(() => ReadSvg(path).Size, cancellationToken);
        var output = await RunHelperAsync(["image-info", path], cancellationToken);
        var parts = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var width) || !int.TryParse(parts[1], out var height))
            throw new InvalidDataException("无法确定图像尺寸。");
        return new(width, height);
    }

    public async Task<FileConversionResult> ConvertAsync(FileConversionRequest request, CancellationToken cancellationToken = default)
    {
        if (!GetAvailableFormats(request.SourcePath).Contains(request.Format))
            throw new InvalidOperationException("此文件不支持所选转换格式。");
        if (!File.Exists(request.SourcePath)) throw new FileNotFoundException("源文件不存在。", request.SourcePath);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var token = timeout.Token;
        var work = Path.Combine(Path.GetTempPath(), "MacExplorer-convert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var extension = request.Format.ToString().ToLowerInvariant();
            var temporaryOutput = Path.Combine(work, "result." + extension);
            var sourceExtension = Path.GetExtension(request.SourcePath).ToLowerInvariant();
            var warnings = new List<string>();
            if (request.Format is FileConversionFormat.Png or FileConversionFormat.Jpg)
            {
                var size = request.ImageSize ?? await GetImageSizeAsync(request.SourcePath, token);
                size.Validate();
                if (sourceExtension == ".svg")
                    await Task.Run(() => RenderSvg(request.SourcePath, temporaryOutput, size, request.Format == FileConversionFormat.Jpg), token);
                else
                    await RunHelperAsync(["image", request.SourcePath, temporaryOutput, size.Width.ToString(CultureInfo.InvariantCulture), size.Height.ToString(CultureInfo.InvariantCulture)], token);
            }
            else if (sourceExtension is ".doc" or ".docx")
            {
                var input = request.SourcePath;
                string? attachments = null;
                if (sourceExtension == ".docx")
                {
                    input = Path.Combine(work, "input.docx");
                    File.Copy(request.SourcePath, input);
                    try { attachments = await Task.Run(() => WordPdfPreparation.Prepare(input, work, warnings, token), token); }
                    catch (Exception ex) when (ex is InvalidDataException or FileFormatException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException)
                    { throw new IOException("Word 文件损坏、已加密或格式无效。", ex); }
                }
                await RunHelperAsync(attachments == null ? ["word-pdf", input, temporaryOutput] : ["word-pdf", input, temporaryOutput, attachments], token);
                warnings.Add("已按基础文档转换；复杂分页、浮动对象和页眉页脚可能与 Word 不同。");
            }
            else
            {
                await Task.Run(async () =>
                {
                    var text = ReadText(request.SourcePath);
                    if (sourceExtension is not (".md" or ".markdown")) text = WrapLiteralText(text);
                    var document = new MarkdownConversionDocument(text, request.SourcePath, work, warnings, token);
                    if (request.Format == FileConversionFormat.Docx) document.WriteDocx(temporaryOutput);
                    else
                    {
                        var html = Path.Combine(work, "document.html");
                        await File.WriteAllTextAsync(html, document.ToHtml(), token);
                        await RunHelperAsync(["html-pdf", html, temporaryOutput], token);
                    }
                }, token);
            }
            token.ThrowIfCancellationRequested();
            if (!File.Exists(temporaryOutput) || new FileInfo(temporaryOutput).Length == 0)
                throw new IOException("转换未生成有效文件。");
            var output = await CommitOutputAsync(temporaryOutput, request.SourcePath, extension, token);
            return new(output, warnings.Distinct().ToArray());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("转换超时，请尝试更小或更简单的文件。");
        }
        finally { Directory.Delete(work, true); }
    }

    internal static void NormalizeDocxRelationships(string path)
    {
        // AppKit rejects package-absolute OOXML relationship targets, including
        // styles and images. Normalize only the temporary copy, never the source.
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        if (zip.GetEntry("_rels/.rels") == null) throw new InvalidDataException("Word 文档缺少根关系文件。");
        foreach (var entry in zip.Entries.Where(entry => entry.FullName.EndsWith(".rels", StringComparison.Ordinal)).ToArray())
        {
            XDocument relationships;
            using (var stream = entry.Open()) relationships = XDocument.Load(stream);
            var directory = Path.GetDirectoryName(Path.GetDirectoryName(entry.FullName)!)?.Replace('\\', '/') ?? "";
            var baseUri = new Uri("https://package/" + (directory.Length == 0 ? "" : directory + "/"));
            var changed = false;
            foreach (var relationship in relationships.Root!.Elements())
            {
                var target = relationship.Attribute("Target");
                if (target?.Value.StartsWith('/') == true && (string?)relationship.Attribute("TargetMode") != "External")
                {
                    target.Value = baseUri.MakeRelativeUri(new Uri("https://package" + target.Value)).OriginalString;
                    changed = true;
                }
            }
            if (!changed) continue;
            var name = entry.FullName;
            entry.Delete();
            using var output = zip.CreateEntry(name).Open();
            relationships.Save(output);
        }
    }

    internal static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Encoding encoding = new UTF8Encoding(false, true);
        var skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { encoding = new UTF32Encoding(false, true, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { encoding = new UTF32Encoding(true, true, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, true, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, true, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) skip = 3;
        try
        {
            var text = encoding.GetString(bytes, skip, bytes.Length - skip);
            if (text.Contains('\0')) throw new InvalidDataException("文件包含二进制内容，无法作为文本转换。");
            return text;
        }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("无法解码文本，请先保存为 UTF-8 或带 BOM 的 Unicode 文本。", ex); }
    }

    internal static string WrapLiteralText(string text)
    {
        var fenceLength = Math.Max(3, Regex.Matches(text, "`+").Select(m => m.Length + 1).DefaultIfEmpty(3).Max());
        var fence = new string('`', fenceLength);
        return fence + "\n" + text.Replace("\r\n", "\n").Replace('\r', '\n') + "\n" + fence;
    }

    internal async Task<string> RunHelperAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (!File.Exists(_helperPath)) throw new FileNotFoundException("缺少内置转换组件，请重新安装应用。", _helperPath);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var info = new ProcessStartInfo(_helperPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("无法启动转换组件。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            if (!cancellationToken.IsCancellationRequested) throw new TimeoutException("转换组件运行超时。");
            throw;
        }
        var error = await stderr;
        if (process.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(error) ? "转换组件执行失败。" : error.Trim());
        return await stdout;
    }

    private static async Task<string> CommitOutputAsync(string temporaryOutput, string source, string extension, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(source)!;
        // Stage on the destination volume, then rename without overwrite. A cancelled
        // copy never exposes a partial file under the final filename.
        var staging = Path.Combine(directory, ".MacExplorer-convert-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var input = File.OpenRead(temporaryOutput))
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output, token);
            var stem = Path.GetFileNameWithoutExtension(source);
            for (var index = 1; ; index++)
            {
                token.ThrowIfCancellationRequested();
                var target = Path.Combine(directory, stem + (index == 1 ? "" : " " + index) + "." + extension);
                if (RenameExclusive(staging, target, 4 /* RENAME_EXCL */) == 0) return target;
                var error = Marshal.GetLastPInvokeError();
                if (error != 17 /* EEXIST */) throw new IOException("无法保存转换文件：" + new Win32Exception(error).Message);
            }
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "renamex_np", SetLastError = true)]
    private static extern int RenameExclusive([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string target, uint flags);

    internal static (string Xml, ConversionImageSize Size) ReadSvg(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var doc = XDocument.Load(reader);
        var root = doc.Root;
        if (root?.Name.LocalName != "svg") throw new InvalidDataException("无效的 SVG 文件。");
        foreach (var element in root.DescendantsAndSelf())
        {
            if (element.Name.LocalName is "script" or "foreignObject") throw new InvalidDataException("SVG 包含不支持的脚本或嵌入网页。");
            foreach (var attribute in element.Attributes())
                if (attribute.Name.LocalName == "href" && !attribute.Value.StartsWith('#') && !attribute.Value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SVG 引用了外部资源，请先将资源嵌入文件。");
        }
        var xml = doc.ToString();
        if (xml.Contains("@import", StringComparison.OrdinalIgnoreCase)
            || Regex.Matches(xml, @"url\(([^)]*)\)", RegexOptions.IgnoreCase)
                .Any(match => !match.Groups[1].Value.Trim().Trim('\'', '"').StartsWith('#')))
            throw new InvalidDataException("SVG 包含外部样式资源。");
        var viewBox = ((string?)root.Attribute("viewBox") ?? "").Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        double vw = 512, vh = 512;
        if (viewBox.Length == 4 && double.TryParse(viewBox[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedWidth)
            && double.TryParse(viewBox[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedHeight) && parsedWidth > 0 && parsedHeight > 0)
        { vw = parsedWidth; vh = parsedHeight; }
        var width = SvgLength((string?)root.Attribute("width"));
        var height = SvgLength((string?)root.Attribute("height"));
        width ??= height.HasValue ? height * vw / vh : vw;
        height ??= width * vh / vw;
        if (!double.IsFinite(width.Value) || !double.IsFinite(height.Value) || width > int.MaxValue || height > int.MaxValue)
            throw new InvalidDataException("SVG 尺寸无效。");
        root.SetAttributeValue("width", width.Value.ToString(CultureInfo.InvariantCulture));
        root.SetAttributeValue("height", height.Value.ToString(CultureInfo.InvariantCulture));
        return (doc.ToString(), new(Math.Max(1, (int)Math.Ceiling(width.Value)), Math.Max(1, (int)Math.Ceiling(height.Value))));
    }

    private static double? SvgLength(string? value)
    {
        if (value == null) return null;
        var match = Regex.Match(value.Trim(), @"^([0-9]*\.?[0-9]+)(px|pt|pc|in|cm|mm)?$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var length = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * (match.Groups[2].Value.ToLowerInvariant() switch
        { "pt" => 96d / 72, "pc" => 16, "in" => 96, "cm" => 96 / 2.54, "mm" => 96 / 25.4, _ => 1 });
        return length > 0 ? length : null;
    }

    internal static void RenderSvg(string input, string output, ConversionImageSize size, bool jpeg = false)
    {
        size.Validate();
        using var svg = new SKSvg();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ReadSvg(input).Xml));
        svg.Load(stream);
        var picture = svg.Picture ?? throw new InvalidDataException("无法渲染 SVG。");
        var bounds = picture.CullRect;
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidDataException("SVG 画布为空。");
        using var surface = SKSurface.Create(new SKImageInfo(size.Width, size.Height)) ?? throw new IOException("无法创建图像。");
        surface.Canvas.Clear(jpeg ? SKColors.White : SKColors.Transparent);
        surface.Canvas.Scale(size.Width / bounds.Width, size.Height / bounds.Height);
        surface.Canvas.Translate(-bounds.Left, -bounds.Top);
        surface.Canvas.DrawPicture(picture);
        using var image = surface.Snapshot();
        using var data = image.Encode(jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, jpeg ? 90 : 100);
        using var file = File.Create(output);
        data.SaveTo(file);
    }
}
