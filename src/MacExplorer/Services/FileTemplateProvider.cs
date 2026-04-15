namespace MacExplorer.Services;

using System.IO.Compression;
using System.Text;

/// <summary>
/// Generates minimal valid Office (OOXML) and iWork file templates in memory using ZipArchive.
/// Templates are lazily cached so they are only built once.
/// </summary>
public static class FileTemplateProvider
{
    private static readonly Lazy<byte[]> DocxCache = new(BuildDocx);
    private static readonly Lazy<byte[]> XlsxCache = new(BuildXlsx);
    private static readonly Lazy<byte[]> PptxCache = new(BuildPptx);
    private static readonly Lazy<byte[]> PagesCache = new(BuildPages);
    private static readonly Lazy<byte[]> NumbersCache = new(BuildNumbers);
    private static readonly Lazy<byte[]> KeynoteCache = new(BuildKeynote);

    public static byte[]? GetTemplate(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".docx" => DocxCache.Value,
            ".xlsx" => XlsxCache.Value,
            ".pptx" => PptxCache.Value,
            ".pages" => PagesCache.Value,
            ".numbers" => NumbersCache.Value,
            ".key" => KeynoteCache.Value,
            _ => null
        };
    }

    // ───────────────────── helpers ─────────────────────

    private static void AddEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static byte[] BuildZip(Action<ZipArchive> populate)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            populate(zip);
        }
        return ms.ToArray();
    }

    // ───────────────────── DOCX ─────────────────────

    private static byte[] BuildDocx()
    {
        return BuildZip(zip =>
        {
            AddEntry(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "</Types>");

            AddEntry(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                "</Relationships>");

            AddEntry(zip, "word/document.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<w:document xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"" +
                " xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                "<w:body/>" +
                "</w:document>");
        });
    }

    // ───────────────────── XLSX ─────────────────────

    private static byte[] BuildXlsx()
    {
        return BuildZip(zip =>
        {
            AddEntry(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "</Types>");

            AddEntry(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");

            AddEntry(zip, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"" +
                " xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
                "</workbook>");

            AddEntry(zip, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "</Relationships>");

            AddEntry(zip, "xl/worksheets/sheet1.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"" +
                " xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheetData/>" +
                "</worksheet>");
        });
    }

    // ───────────────────── PPTX ─────────────────────

    private static byte[] BuildPptx()
    {
        return BuildZip(zip =>
        {
            AddEntry(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>" +
                "<Override PartName=\"/ppt/slides/slide1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>" +
                "<Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/>" +
                "<Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/>" +
                "</Types>");

            AddEntry(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"ppt/presentation.xml\"/>" +
                "</Relationships>");

            AddEntry(zip, "ppt/presentation.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<p:presentation xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"" +
                " xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"" +
                " xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\">" +
                "<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId2\"/></p:sldMasterIdLst>" +
                "<p:sldIdLst><p:sldId id=\"256\" r:id=\"rId3\"/></p:sldIdLst>" +
                "<p:sldSz cx=\"9144000\" cy=\"6858000\" type=\"screen4x3\"/>" +
                "<p:notesSz cx=\"6858000\" cy=\"9144000\"/>" +
                "</p:presentation>");

            AddEntry(zip, "ppt/_rels/presentation.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/>" +
                "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide1.xml\"/>" +
                "</Relationships>");

            AddEntry(zip, "ppt/slides/slide1.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<p:sld xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"" +
                " xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"" +
                " xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\">" +
                "<p:cSld><p:spTree>" +
                "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
                "<p:grpSpPr/>" +
                "</p:spTree></p:cSld>" +
                "</p:sld>");

            AddEntry(zip, "ppt/slides/_rels/slide1.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" +
                "</Relationships>");

            AddEntry(zip, "ppt/slideLayouts/slideLayout1.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<p:sldLayout xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"" +
                " xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"" +
                " xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" type=\"blank\" preserve=\"1\">" +
                "<p:cSld name=\"Blank\"><p:spTree>" +
                "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
                "<p:grpSpPr/>" +
                "</p:spTree></p:cSld>" +
                "</p:sldLayout>");

            AddEntry(zip, "ppt/slideLayouts/_rels/slideLayout1.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/>" +
                "</Relationships>");

            AddEntry(zip, "ppt/slideMasters/slideMaster1.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<p:sldMaster xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"" +
                " xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"" +
                " xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\">" +
                "<p:cSld><p:bg><p:bgPr><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill><a:effectLst/></p:bgPr></p:bg>" +
                "<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree></p:cSld>" +
                "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst>" +
                "</p:sldMaster>");

            AddEntry(zip, "ppt/slideMasters/_rels/slideMaster1.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" +
                "</Relationships>");
        });
    }

    // ───────────────────── iWork (Pages / Numbers / Keynote) ─────────────────────
    // NOTE: Apple iWork formats use a proprietary protobuf-based .iwa format inside a ZIP container.
    // Generating a fully valid .iwa from scratch without Apple's private schema is not feasible.
    // The approach below creates a minimal ZIP with metadata plists that iWork apps will recognise
    // as a (damaged/empty) document and offer to repair or open. If a fully valid template is needed,
    // consider embedding a pre-built binary template as a resource instead.

    private static byte[] BuildPages()
    {
        return BuildIWorkPackage("com.apple.iwork.pages");
    }

    private static byte[] BuildNumbers()
    {
        return BuildIWorkPackage("com.apple.iwork.numbers");
    }

    private static byte[] BuildKeynote()
    {
        return BuildIWorkPackage("com.apple.iwork.keynote");
    }

    private static byte[] BuildIWorkPackage(string documentType)
    {
        return BuildZip(zip =>
        {
            AddEntry(zip, "Metadata/BuildVersionHistory.plist",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                "<plist version=\"1.0\">\n" +
                "<dict>\n" +
                "    <key>BuildVersionHistory</key>\n" +
                "    <array>\n" +
                "        <string>Template</string>\n" +
                "    </array>\n" +
                "</dict>\n" +
                "</plist>");

            AddEntry(zip, "Metadata/Properties.plist",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                "<plist version=\"1.0\">\n" +
                "<dict>\n" +
                "    <key>documentUUID</key>\n" +
                "    <string>00000000-0000-0000-0000-000000000000</string>\n" +
                "    <key>fileFormatVersion</key>\n" +
                "    <string>2024.1</string>\n" +
                "    <key>documentType</key>\n" +
                $"    <string>{documentType}</string>\n" +
                "</dict>\n" +
                "</plist>");

            // Placeholder for the binary protobuf document index.
            // A real .iwa file requires Apple's proprietary protobuf schema;
            // this empty placeholder ensures the ZIP structure exists.
            var placeholderEntry = zip.CreateEntry("Index/Document.iwa", CompressionLevel.Fastest);
            using var stream = placeholderEntry.Open();
            stream.WriteByte(0);
        });
    }
}
