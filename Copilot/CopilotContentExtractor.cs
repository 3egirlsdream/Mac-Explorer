using System.Text;
using System.IO.Compression;
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
    public const long MaxOfficeExpandedBytes = 32 * 1024 * 1024;
    public const int MaxOfficeCharacters = 1_000_000;

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
            _ => await Task.Run(() => ExtractOffice(path, extension, cancellationToken), cancellationToken)
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

    private sealed class BoundedOfficeText(CancellationToken cancellationToken)
    {
        private readonly StringBuilder _text = new();
        private bool _hasItem;
        public void Add(string? value, string separator = "\n")
        {
            cancellationToken.ThrowIfCancellationRequested();
            value ??= string.Empty;
            if (_text.Length + value.Length + (_hasItem ? separator.Length : 0) > MaxOfficeCharacters)
                throw new IOException("Office 文档提取文字超过 100 万字符上限，请先缩小文档范围。");
            if (_hasItem) _text.Append(separator);
            _text.Append(value);
            _hasItem = true;
        }
        public override string ToString() => _text.ToString();
    }

    private static void CheckOfficeArchive(string path, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 2048)
            throw new IOException("Office 文档压缩条目过多，请先缩小文档范围。");
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Length > 12 * 1024 * 1024 || (total += entry.Length) > MaxOfficeExpandedBytes)
                throw new IOException("Office 文档解压后超过资源上限，请先缩小文档范围。");
        }
    }

    private static string ExtractOffice(string path, string extension, CancellationToken cancellationToken)
    {
        CheckOfficeArchive(path, cancellationToken);
        var output = new BoundedOfficeText(cancellationToken);
        switch (extension)
        {
            case ".docx":
                using (var document = WordprocessingDocument.Open(path, false))
                {
                    foreach (var paragraph in document.MainDocumentPart?.Document.Body?
                                 .Descendants<Paragraph>() ?? [])
                        output.Add(paragraph.InnerText);
                    return output.ToString();
                }
            case ".xlsx":
                using (var document = SpreadsheetDocument.Open(path, false))
                {
                    var shared = new List<string>();
                    var sharedCharacters = 0;
                    foreach (var item in document.WorkbookPart?.SharedStringTablePart?.SharedStringTable?
                                 .Elements<DocumentFormat.OpenXml.Spreadsheet.SharedStringItem>() ?? [])
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var value = item.InnerText;
                        sharedCharacters += value.Length;
                        if (sharedCharacters > MaxOfficeCharacters)
                            throw new IOException("Office 文档共享文字超过 100 万字符上限，请先缩小文档范围。");
                        shared.Add(value);
                    }
                    var workbook = document.WorkbookPart;
                    foreach (var sheet in workbook?.Workbook.Sheets?
                                 .Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>() ?? [])
                    {
                        // Relationship enumeration is not the user-visible sheet order; chart sheets have no cells.
                        if (sheet.Id?.Value is not { Length: > 0 } relationshipId
                            || workbook!.GetPartById(relationshipId) is not WorksheetPart worksheet) continue;
                        output.Add($"[工作表：{sheet.Name?.Value}]");
                        foreach (var row in worksheet.Worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Row>())
                        {
                            var values = row.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>().Select(cell =>
                            {
                                var value = cell.CellValue?.Text ?? cell.InlineString?.InnerText ?? string.Empty;
                                if (cell.DataType?.Value == DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString
                                    && int.TryParse(value, out var index) && index >= 0 && index < shared.Count)
                                    value = shared[index];
                                // XLSX omits empty cells. Keep coordinates so A1/C1 cannot be mistaken for A1/B1.
                                return cell.CellReference?.Value is { Length: > 0 } reference
                                    ? $"{reference}: {value}" : value;
                            });
                            output.Add(string.Join("\t", values));
                        }
                    }
                    return output.ToString();
                }
            case ".pptx":
                using (var document = PresentationDocument.Open(path, false))
                {
                    var presentation = document.PresentationPart;
                    foreach (var id in presentation?.Presentation.SlideIdList?
                                 .Elements<DocumentFormat.OpenXml.Presentation.SlideId>() ?? [])
                    {
                        if (id.RelationshipId?.Value is not { Length: > 0 } relationshipId
                            || presentation!.GetPartById(relationshipId) is not SlidePart slide)
                            throw new InvalidDataException("幻灯片引用无效。");
                        var slideText = new BoundedOfficeText(cancellationToken);
                        foreach (var text in slide.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>())
                            slideText.Add(text.Text, " ");
                        output.Add(slideText.ToString());
                    }
                    return output.ToString();
                }
            default: throw new NotSupportedException("不支持的 Office 文档类型。");
        }
    }
}
