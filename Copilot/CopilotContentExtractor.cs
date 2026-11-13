using System.Text;
using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MacExplorer.Services;
using MacExplorer.Models;

namespace MacExplorer.Copilot;

/// <summary>Local extraction only. The caller decides when extracted text may enter model context.</summary>
public sealed class CopilotContentExtractor(
    IPdfTextExtractionService pdf, IImageAnalysisService images, IAiTagService? aiTags = null)
{
    public const int MaxPageUtf8Bytes = TextContentPage.MaxUtf8Bytes;
    public const long MaxDocumentBytes = 25 * 1024 * 1024;
    public const long MaxOfficeExpandedBytes = 32 * 1024 * 1024;
    public const int MaxOfficeCharacters = 1_000_000;

    public sealed record Page(string Text, long Offset, long NextOffset, bool HasMore)
    {
        public string Kind => "file-content-page";
        public string Source { get; init; } = "file";
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
        await DocumentContentExtractor.ExtractionGate.WaitAsync(cancellationToken);
        try { return await ExtractPageCoreAsync(path, offset, cancellationToken); }
        finally { DocumentContentExtractor.ExtractionGate.Release(); }
    }

    private async Task<Page> ExtractPageCoreAsync(string path, long offset, CancellationToken cancellationToken)
    {
        MacExplorer.Services.DirectoryAccess.Current.EnsureAccess(path);
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
        if (extension is ".pdf" or ".png" or ".jpg" or ".jpeg" or ".heic" or ".tif" or ".tiff"
            && aiTags != null)
        {
            var modified = File.GetLastWriteTime(path).Ticks;
            var cached = await aiTags.GetCachedTextPageAsync(path, modified, offset, cancellationToken).ConfigureAwait(false);
            if (cached != null && File.GetLastWriteTime(path).Ticks == modified)
            {
                return new Page(cached.Text, offset, cached.NextOffset, cached.HasMore) { Source = "analysis-database" };
            }
        }
        if (!DocumentContentExtractor.IsOffice(path) && new FileInfo(path).Length > MaxDocumentBytes)
            throw new IOException("非文本文件超过 25 MB，请先缩小范围。");
        if (extension == ".pdf")
        {
            var texts = await pdf.ExtractAsync(path, ct: cancellationToken).ConfigureAwait(false);
            var page = TextContentPage.Read(OpenSegments(texts), offset, cancellationToken);
            return new(page.Text, offset, page.NextOffset, page.HasMore);
        }
        var content = extension switch
        {
            ".png" or ".jpg" or ".jpeg" or ".heic" or ".tif" or ".tiff" =>
                string.Join("\n", (await images.AnalyzeImageAsync(path, cancellationToken)).RecognizedTexts.Select(item => item.Text)),
            _ => await Task.Run(() => ExtractOffice(path, extension, cancellationToken), cancellationToken)
        };
        if (offset > content.Length) throw new ArgumentOutOfRangeException(nameof(offset), "起始字符位置超出文件末尾。");
        return Slice(content.AsSpan((int)offset), offset, false);
    }

    private static IEnumerable<TextReader> OpenSegments(IEnumerable<RecognizedText> texts)
    {
        foreach (var text in texts)
        {
            using var reader = new StringReader(text.Text);
            yield return reader;
        }
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

    private static string ExtractOffice(string path, string extension, CancellationToken cancellationToken)
    {
        var content = DocumentContentExtractor.ExtractSegments(path, cancellationToken);
        if (content.IsTruncated) throw new IOException("Office 文档提取文字超过 100 万字符上限，请先缩小文档范围。");
        if (extension != ".xlsx") return string.Join("\n", content.Segments.Select(s => s.Text));
        var output = new StringBuilder(); string? previousRow = null;
        foreach (var segment in content.Segments)
        {
            var cell = segment.Location.Cell;
            var row = string.IsNullOrEmpty(cell) ? null : segment.Location.Sheet + ":" + new string(cell.Where(char.IsDigit).ToArray());
            if (output.Length > 0) output.Append(row != null && row == previousRow ? '\t' : '\n');
            output.Append(segment.Text); previousRow = row;
        }
        return output.ToString();
    }
}
