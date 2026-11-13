using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using MacExplorer.Models;
using MacExplorer.Services.Impl;

namespace MacExplorer.Services;

public sealed class DocumentContentExtractor(IPdfTextExtractionService pdf)
{
    public const long MaxFileBytes = 25 * 1024 * 1024;
    public const int MaxCharacters = 1_000_000;
    internal static SemaphoreSlim ExtractionGate { get; } = new(1, 1);
    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".md" or ".markdown" or ".docx" or ".xlsx" or ".pptx" or ".pdf";
    public async Task<DocumentContent> ExtractAsync(string path, CancellationToken token = default)
    {
        await ExtractionGate.WaitAsync(token);
        try { return await ExtractCoreAsync(path, token); }
        finally { ExtractionGate.Release(); }
    }
    private async Task<DocumentContent> ExtractCoreAsync(string path, CancellationToken token)
    {
        DirectoryAccess.Current.EnsureAccess(path);
        if (!File.Exists(path)) throw new FileNotFoundException(path);
        if (new FileInfo(path).Length > MaxFileBytes) throw new IOException(LocalizationText.Get("content.too-large"));
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var texts = await pdf.ExtractAsync(path, ct: token);
            var segments = new List<DocumentTextSegment>(); var count = 0; var truncated = false;
            foreach (var text in texts)
            {
                token.ThrowIfCancellationRequested();
                var remaining = MaxCharacters - count;
                if (remaining <= 0 || segments.Count >= 20000) { truncated = true; break; }
                var value = text.Text;
                if (value.Length > remaining) { value = value[..remaining]; truncated = true; }
                segments.Add(new(value, new("page", text.Page ?? 1))); count += value.Length;
            }
            return new(segments, truncated);
        }
        return await Task.Run(() => ExtractSegments(path, token), token);
    }
    internal static DocumentContent ExtractSegments(string path, CancellationToken token)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var result = new List<DocumentTextSegment>(); var characters = 0; var truncated = false;
        void Add(string text, DocumentLocation location)
        {
            token.ThrowIfCancellationRequested();
            if (text.Length == 0 || truncated) return;
            var remaining = MaxCharacters - characters;
            if (result.Count >= 20000 || remaining <= 0) { truncated = true; return; }
            if (text.Length > remaining) { text = text[..remaining]; truncated = true; }
            characters += text.Length;
            result.Add(new(text, location));
        }
        if (extension is not (".docx" or ".xlsx" or ".pptx"))
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var line = new StringBuilder(); var buffer = new char[4096]; var number = 1; int count;
            while (!truncated && (count = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                for (var i = 0; i < count && !truncated; i++)
                {
                    var c = buffer[i];
                    if (c == '\n') { Add(line.ToString().TrimEnd('\r'), new("line", number++)); line.Clear(); }
                    else if (line.Length >= MaxCharacters) { Add(line.ToString(), new("line", number)); truncated = true; }
                    else line.Append(c);
                }
            }
            if (line.Length > 0 && !truncated) Add(line.ToString(), new("line", number));
            return new(result, truncated);
        }
        CheckOfficeArchive(path, token);
        if (extension == ".docx")
        {
            using var document = WordprocessingDocument.Open(path, false); var number = 0;
            foreach (var paragraph in document.MainDocumentPart?.Document.Body?.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>() ?? [])
            { Add(paragraph.InnerText, new("paragraph", ++number)); if (truncated) break; }
        }
        else if (extension == ".xlsx")
        {
            using var document = SpreadsheetDocument.Open(path, false);
            var workbook = document.WorkbookPart; var shared = new List<string>(); var sharedSize = 0;
            foreach (var item in workbook?.SharedStringTablePart?.SharedStringTable?.Elements<DocumentFormat.OpenXml.Spreadsheet.SharedStringItem>() ?? [])
            {
                token.ThrowIfCancellationRequested(); sharedSize += item.InnerText.Length;
                if (sharedSize > MaxCharacters) throw new IOException(LocalizationText.Get("content.expanded-limit"));
                shared.Add(item.InnerText);
            }
            foreach (var sheet in workbook?.Workbook.Sheets?.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>() ?? [])
            {
                if (sheet.Id?.Value is not { Length: > 0 } id || workbook!.GetPartById(id) is not WorksheetPart worksheet) continue;
                Add($"[工作表：{sheet.Name?.Value}]", new("cell", Sheet: sheet.Name?.Value, Cell: ""));
                foreach (var cell in worksheet.Worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>())
                {
                    var value = cell.CellValue?.Text ?? cell.InlineString?.InnerText ?? "";
                    if (cell.DataType?.Value == DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString && int.TryParse(value, out var index) && index >= 0 && index < shared.Count) value = shared[index];
                    if (value.Length > 0) Add($"{cell.CellReference?.Value}: {value}", new("cell", Sheet: sheet.Name?.Value, Cell: cell.CellReference?.Value));
                    if (truncated) break;
                }
                if (truncated) break;
            }
        }
        else
        {
            using var document = PresentationDocument.Open(path, false); var presentation = document.PresentationPart; var number = 0;
            foreach (var id in presentation?.Presentation.SlideIdList?.Elements<DocumentFormat.OpenXml.Presentation.SlideId>() ?? [])
            {
                token.ThrowIfCancellationRequested();
                if (id.RelationshipId?.Value is not { Length: > 0 } relation || presentation!.GetPartById(relation) is not SlidePart slide)
                    throw new InvalidDataException(LocalizationText.Get("content.invalid-document"));
                var text = new StringBuilder();
                foreach (var item in slide.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>())
                {
                    token.ThrowIfCancellationRequested();
                    if (text.Length + item.Text.Length > MaxCharacters) { truncated = true; break; }
                    if (text.Length > 0) text.Append(' '); text.Append(item.Text);
                }
                var overflow = truncated; truncated = false; Add(text.ToString(), new("slide", ++number)); truncated |= overflow;
                if (truncated) break;
            }
        }
        return new(result, truncated);
    }
    private static void CheckOfficeArchive(string path, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 2048) throw new IOException(LocalizationText.Get("content.expanded-limit"));
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Length > 12 * 1024 * 1024 || (total += entry.Length) > 32 * 1024 * 1024)
                throw new IOException(LocalizationText.Get("content.expanded-limit"));
        }
    }
}
