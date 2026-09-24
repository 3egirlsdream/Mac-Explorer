using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MacExplorer.Services;

namespace MacExplorer.Copilot;

/// <summary>Local extraction only. The caller decides when extracted text may enter model context.</summary>
public sealed class CopilotContentExtractor(
    IPdfTextExtractionService pdf, IImageAnalysisService images)
{
    public const int MaxPageUtf8Bytes = 50 * 1024;
    public const long MaxDocumentBytes = 25 * 1024 * 1024;

    public sealed record Page(string Text, long Offset, long NextOffset, bool HasMore)
    {
        public string Kind => "file-content-page";
    }

    public bool Supports(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return TextFileTypes.IsText(path) || extension is ".pdf" or ".docx" or ".xlsx" or ".pptx"
            or ".png" or ".jpg" or ".jpeg" or ".heic" or ".tif" or ".tiff";
    }

    public async Task<Page> ExtractPageAsync(string path, long offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new FileNotFoundException("只能提取本地文件内容。", path);
        if (!Supports(path)) throw new NotSupportedException("文件类型暂不支持内容提取。");
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "起始字符位置不能为负数。");

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (TextFileTypes.IsText(path))
        {
            await using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var skipBuffer = new char[8192];
            var remaining = offset;
            while (remaining > 0)
            {
                var skipped = await reader.ReadBlockAsync(
                    skipBuffer.AsMemory(0, (int)Math.Min(skipBuffer.Length, remaining)), cancellationToken);
                if (skipped == 0) throw new ArgumentOutOfRangeException(nameof(offset), "起始字符位置超出文件末尾。");
                remaining -= skipped;
            }
            var buffer = new char[MaxPageUtf8Bytes + 1];
            var read = await reader.ReadBlockAsync(buffer, cancellationToken);
            return Slice(buffer.AsSpan(0, read), offset, read == buffer.Length);
        }
        if (new FileInfo(path).Length > MaxDocumentBytes)
            throw new IOException("非文本文件超过 25 MB，请先缩小范围。");
        var content = extension switch
        {
            ".pdf" => string.Join("\n", (await pdf.ExtractAsync(path, ct: cancellationToken)).Select(item => item.Text)),
            ".png" or ".jpg" or ".jpeg" or ".heic" or ".tif" or ".tiff" =>
                string.Join("\n", (await images.AnalyzeImageAsync(path, cancellationToken)).RecognizedTexts.Select(item => item.Text)),
            _ => await Task.Run(() => ExtractOffice(path, extension), cancellationToken)
        };
        if (offset > content.Length) throw new ArgumentOutOfRangeException(nameof(offset), "起始字符位置超出文件末尾。");
        return Slice(content.AsSpan((int)offset), offset, false);
    }

    private static Page Slice(ReadOnlySpan<char> content, long offset, bool hasUnreadText)
    {
        var length = 0;
        var bytes = 0;
        foreach (var rune in content.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > MaxPageUtf8Bytes) break;
            bytes += rune.Utf8SequenceLength;
            length += rune.Utf16SequenceLength;
        }
        return new Page(new string(content[..length]), offset, offset + length, hasUnreadText || length < content.Length);
    }

    private static string ExtractOffice(string path, string extension)
    {
        switch (extension)
        {
            case ".docx":
                using (var document = WordprocessingDocument.Open(path, false))
                    return string.Join("\n", document.MainDocumentPart?.Document.Body?
                        .Descendants<Paragraph>().Select(p => p.InnerText) ?? []);
            case ".xlsx":
                using (var document = SpreadsheetDocument.Open(path, false))
                {
                    var shared = document.WorkbookPart?.SharedStringTablePart?.SharedStringTable?
                        .Elements<DocumentFormat.OpenXml.Spreadsheet.SharedStringItem>()
                        .Select(item => item.InnerText).ToArray() ?? [];
                    var lines = new List<string>();
                    foreach (var worksheet in document.WorkbookPart?.WorksheetParts ?? [])
                    {
                        foreach (var row in worksheet.Worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Row>())
                        {
                            var values = row.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>().Select(cell =>
                            {
                                var value = cell.CellValue?.Text ?? cell.InlineString?.InnerText ?? string.Empty;
                                if (cell.DataType?.Value == DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString
                                    && int.TryParse(value, out var index) && index >= 0 && index < shared.Length)
                                    return shared[index];
                                return value;
                            });
                            lines.Add(string.Join("\t", values));
                        }
                    }
                    return string.Join("\n", lines);
                }
            case ".pptx":
                using (var document = PresentationDocument.Open(path, false))
                    return string.Join("\n", document.PresentationPart?.SlideParts.Select(slide =>
                        string.Join(" ", slide.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>()
                            .Select(text => text.Text))) ?? []);
            default: throw new NotSupportedException("不支持的 Office 文档类型。");
        }
    }
}
