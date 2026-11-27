using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using MacExplorer.Models;
using MacExplorer.Services.Impl;

namespace MacExplorer.Services;

public sealed class DocumentContentExtractor(IPdfTextExtractionService pdf)
{
    public const long MaxFileBytes = 25 * 1024 * 1024;
    public const long MaxOfficeFileBytes = 128 * 1024 * 1024;
    public const int MaxCharacters = 1_000_000;
    private const string PermanentSizeLimitKey = "PermanentSizeLimit";
    internal static SemaphoreSlim ExtractionGate { get; } = new(1, 1);
    internal static bool IsOffice(string path) => Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".xlsx" or ".pptx";
    internal static bool ExceedsSizeLimit(string path, long size) => size > (IsOffice(path) ? MaxOfficeFileBytes : MaxFileBytes);
    internal static bool IsPermanentSizeLimitException(Exception ex) => ex is IOException && ex.Data.Contains(PermanentSizeLimitKey);
    private static IOException CreateSizeLimitException(string localizationKey)
    {
        var ex = new IOException(LocalizationText.Get(localizationKey));
        ex.Data[PermanentSizeLimitKey] = true;
        return ex;
    }
    internal static bool IsTemporaryOfficeFile(string path)
    {
        var name = Path.GetFileName(path);
        return IsOffice(path) && (name.StartsWith("~$", StringComparison.Ordinal)
            || name.StartsWith(".~", StringComparison.Ordinal) || name.StartsWith("._", StringComparison.Ordinal));
    }
    public static bool Supports(string path) => !IsTemporaryOfficeFile(path)
        && Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".md" or ".markdown" or ".docx" or ".xlsx" or ".pptx" or ".pdf";
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
        if (ExceedsSizeLimit(path, new FileInfo(path).Length))
            throw CreateSizeLimitException(IsOffice(path) ? "content.office-too-large" : "content.too-large");
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
        if (IsTemporaryOfficeFile(path)) throw new IOException(LocalizationText.Get("content.temporary-document"));
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
            using var document = WordprocessingDocument.Open(path, false, OfficeSettings()); var number = 0;
            if (document.MainDocumentPart is { } mainPart)
            {
                using var reader = DocumentFormat.OpenXml.OpenXmlReader.Create(mainPart);
                while (!truncated && reader.Read())
                {
                    if (!reader.IsStartElement || reader.ElementType != typeof(DocumentFormat.OpenXml.Wordprocessing.Paragraph)) continue;
                    token.ThrowIfCancellationRequested();
                    var text = reader.LoadCurrentElement()?.InnerText ?? "";
                    Add(text, new("paragraph", ++number));
                }
            }
        }
        else if (extension == ".xlsx")
        {
            using var document = SpreadsheetDocument.Open(path, false, OfficeSettings());
            var workbook = document.WorkbookPart; var shared = new List<string>(); var sharedSize = 0;
            if (workbook?.SharedStringTablePart is { } sharedPart)
            {
                using var reader = DocumentFormat.OpenXml.OpenXmlReader.Create(sharedPart);
                while (reader.Read())
                {
                    if (!reader.IsStartElement || reader.ElementType != typeof(DocumentFormat.OpenXml.Spreadsheet.SharedStringItem)) continue;
                    token.ThrowIfCancellationRequested();
                    var text = reader.LoadCurrentElement()?.InnerText ?? "";
                    sharedSize += text.Length;
                    if (sharedSize > MaxCharacters) throw CreateSizeLimitException("content.expanded-limit");
                    shared.Add(text);
                }
            }
            foreach (var sheet in workbook?.Workbook.Sheets?.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>() ?? [])
            {
                if (sheet.Id?.Value is not { Length: > 0 } id || workbook!.GetPartById(id) is not WorksheetPart worksheet) continue;
                Add($"[工作表：{sheet.Name?.Value}]", new("cell", Sheet: sheet.Name?.Value, Cell: ""));
                if (truncated) break;
                using var reader = DocumentFormat.OpenXml.OpenXmlReader.Create(worksheet);
                while (!truncated && reader.Read())
                {
                    if (!reader.IsStartElement || reader.ElementType != typeof(DocumentFormat.OpenXml.Spreadsheet.Cell)) continue;
                    token.ThrowIfCancellationRequested();
                    if (reader.LoadCurrentElement() is not DocumentFormat.OpenXml.Spreadsheet.Cell cell) continue;
                    var value = cell.CellValue?.Text ?? cell.InlineString?.InnerText ?? "";
                    if (cell.DataType?.Value == DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString && int.TryParse(value, out var index) && index >= 0 && index < shared.Count) value = shared[index];
                    if (value.Length > 0) Add($"{cell.CellReference?.Value}: {value}", new("cell", Sheet: sheet.Name?.Value, Cell: cell.CellReference?.Value));
                }
                if (truncated) break;
            }
        }
        else
        {
            using var document = PresentationDocument.Open(path, false, OfficeSettings()); var presentation = document.PresentationPart; var number = 0;
            foreach (var id in presentation?.Presentation.SlideIdList?.Elements<DocumentFormat.OpenXml.Presentation.SlideId>() ?? [])
            {
                token.ThrowIfCancellationRequested();
                if (id.RelationshipId?.Value is not { Length: > 0 } relation || presentation!.GetPartById(relation) is not SlidePart slide)
                    throw new InvalidDataException(LocalizationText.Get("content.invalid-document"));
                var text = new StringBuilder();
                var remaining = MaxCharacters - characters;
                using (var reader = DocumentFormat.OpenXml.OpenXmlReader.Create(slide))
                {
                    while (reader.Read())
                    {
                        if (!reader.IsStartElement || reader.ElementType != typeof(DocumentFormat.OpenXml.Drawing.Text)) continue;
                        token.ThrowIfCancellationRequested();
                        if (reader.LoadCurrentElement() is not DocumentFormat.OpenXml.Drawing.Text item) continue;
                        var itemText = item.Text;
                        if (itemText.Length == 0) continue;
                        var separator = text.Length > 0 ? 1 : 0;
                        if (text.Length + separator + itemText.Length > remaining)
                        {
                            var take = remaining - text.Length - separator;
                            if (take > 0)
                            {
                                if (separator > 0) text.Append(' ');
                                text.Append(itemText, 0, take);
                            }
                            truncated = true;
                            break;
                        }
                        if (separator > 0) text.Append(' ');
                        text.Append(itemText);
                    }
                }
                var overflow = truncated; truncated = false; Add(text.ToString(), new("slide", ++number)); truncated |= overflow;
                if (truncated) break;
            }
        }
        return new(result, truncated);
    }
    private static OpenSettings OfficeSettings() => new() { MaxCharactersInPart = 12 * 1024 * 1024 };

    private static void CheckOfficeArchive(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > MaxOfficeFileBytes) throw CreateSizeLimitException("content.office-too-large");
        ZipArchive archive;
        try { archive = ZipFile.OpenRead(path); }
        catch (InvalidDataException ex) { throw new InvalidDataException(LocalizationText.Get("content.invalid-document"), ex); }
        using var lease = archive;
        if (archive.Entries.Count > 2048) throw CreateSizeLimitException("content.expanded-limit");
        long total = 0, xmlTotal = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            // Images and embedded attachments are never read for content extraction.
            var xml = entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);
            if ((total += entry.Length) > MaxOfficeFileBytes
                || (xml && (entry.Length > 12 * 1024 * 1024 || (xmlTotal += entry.Length) > 32 * 1024 * 1024)))
                throw CreateSizeLimitException("content.expanded-limit");
        }
    }
}
