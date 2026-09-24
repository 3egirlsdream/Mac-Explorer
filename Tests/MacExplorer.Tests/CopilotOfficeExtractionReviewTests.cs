using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MacExplorer.Copilot;
using Xunit;
using S = DocumentFormat.OpenXml.Spreadsheet;
using P = DocumentFormat.OpenXml.Presentation;
using D = DocumentFormat.OpenXml.Drawing;

namespace MacExplorer.Tests;

public sealed partial class CopilotReviewRegressionTests
{
    [Fact]
    public async Task OfficePagesContinueUntilTheWholeNormalDocumentIsRead()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "pages.docx");
        var expected = new string('a', CopilotContentExtractor.MaxPageUtf8Bytes + 240);
        WriteWord(path, expected);
        var extractor = new CopilotContentExtractor(null!, null!);
        var first = await extractor.ExtractPageAsync(path);
        Assert.True(first.HasMore);
        Assert.Equal(CopilotContentExtractor.MaxPageUtf8Bytes, first.Text.Length);
        var second = await extractor.ExtractPageAsync(path, first.NextOffset);
        Assert.False(second.HasMore);
        Assert.Equal(expected, first.Text + second.Text);
    }

    [Theory]
    [InlineData(1_100_000, "字符")]
    [InlineData(13_000_000, "解压")]
    public async Task HighlyCompressibleOfficeContentReportsItsLimit(int characters, string reason)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, $"large-{characters}.docx");
        WriteWord(path, new string('x', characters));
        Assert.True(new FileInfo(path).Length < CopilotContentExtractor.MaxDocumentBytes);
        var error = await Assert.ThrowsAsync<IOException>(() =>
            new CopilotContentExtractor(null!, null!).ExtractPageAsync(path));
        Assert.Contains(reason, error.Message);
    }

    private static void WriteWord(string path, string text)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new Document(new Body(new Paragraph(new Run(new Text(text)))));
    }

    [Fact]
    public async Task SpreadsheetExtractionKeepsSheetOrderAndSparseCellCoordinates()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "ordered.xlsx");
        using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new S.Workbook();
            var shared = workbook.AddNewPart<SharedStringTablePart>();
            shared.SharedStringTable = new S.SharedStringTable(new S.SharedStringItem(new S.Text("three")));
            var first = workbook.AddNewPart<WorksheetPart>("rIdFirst");
            first.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(
                InlineCell("A1", "one"),
                new S.Cell { CellReference = "C1", DataType = S.CellValues.SharedString, CellValue = new S.CellValue("0") })
                { RowIndex = 1U }));
            var second = workbook.AddNewPart<WorksheetPart>("rIdSecond");
            second.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(InlineCell("B3", "second"))
                { RowIndex = 3U }));
            // Part creation order deliberately differs from the visible workbook order.
            workbook.Workbook.Append(new S.Sheets(
                new S.Sheet { Id = "rIdSecond", SheetId = 2U, Name = "Second" },
                new S.Sheet { Id = "rIdFirst", SheetId = 1U, Name = "First" }));
        }
        var page = await new CopilotContentExtractor(null!, null!).ExtractPageAsync(path);
        Assert.Equal("[工作表：Second]\nB3: second\n[工作表：First]\nA1: one\tC1: three", page.Text);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task PresentationExtractionUsesSlideIdOrderInsteadOfRelationshipOrder()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "ordered.pptx");
        using (var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation))
        {
            var presentation = document.AddPresentationPart();
            presentation.Presentation = new P.Presentation();
            AddSlide(presentation, "rIdFirst", "first");
            AddSlide(presentation, "rIdSecond", "second");
            presentation.Presentation.Append(new P.SlideIdList(
                new P.SlideId { Id = 257U, RelationshipId = "rIdSecond" },
                new P.SlideId { Id = 256U, RelationshipId = "rIdFirst" }));
        }
        var page = await new CopilotContentExtractor(null!, null!).ExtractPageAsync(path);
        Assert.Equal("second\nfirst", page.Text);
        Assert.False(page.HasMore);
    }

    private static S.Cell InlineCell(string reference, string text) => new()
    {
        CellReference = reference, DataType = S.CellValues.InlineString,
        InlineString = new S.InlineString(new S.Text(text))
    };

    private static void AddSlide(PresentationPart presentation, string id, string text)
    {
        var slide = presentation.AddNewPart<SlidePart>(id);
        slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(),
            new P.Shape(
                new P.NonVisualShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 2U, Name = "Text" },
                    new P.NonVisualShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
                new P.ShapeProperties(),
                new P.TextBody(new D.BodyProperties(), new D.ListStyle(),
                    new D.Paragraph(new D.Run(new D.Text(text))))))));
    }
}
