using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FeatureUpgradeTests
{
    private sealed class AnalysisClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData("~$report.docx")]
    [InlineData(".~report.pptx")]
    [InlineData("._report.xlsx")]
    public async Task OfficeTemporaryFilesNeverCreateAnalysisTasks(string name)
    {
        var path = At(name, "Office owner and lock information");
        Assert.False(DocumentContentExtractor.Supports(path));
        var database = Database(); using var tags = new AiTagService(database);
        var tasks = new BackgroundTaskManager();
        var service = new DocumentIndexService(database, new DocumentContentExtractor(null!), tags, tasks);
        await service.IndexDirectoryAsync([new FileSystemEntry { FullPath = path, Name = name }], Token);
        await service.IndexFileAsync(path, Token);
        Assert.Empty(tasks.Tasks);
        using var connection = database.GetConnection(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM document_index";
        Assert.Equal(0L, command.ExecuteScalar());
        await tasks.ShutdownAsync();
    }

    [Fact]
    public async Task FailedDocumentsUsePersistentCooldownAndRetryAfterChangesOrDelay()
    {
        var path = At("broken.docx", "not an Office archive"); var database = Database();
        using var tags = new AiTagService(database); var tasks = new BackgroundTaskManager(); var clock = new AnalysisClock();
        var service = new DocumentIndexService(database, new DocumentContentExtractor(null!), tags, tasks, clock);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => service.IndexFileAsync(path, Token)));
        var failure = Assert.Single(tasks.Tasks);
        Assert.Equal(BackgroundTaskState.Failed, failure.State);
        Assert.Equal(LocalizationText.Get("content.invalid-document"), failure.ErrorMessage);
        Assert.Contains("InvalidDataException", failure.ErrorDetail);
        // A new service instance must also honor the persisted cooldown.
        service = new DocumentIndexService(database, new DocumentContentExtractor(null!), tags, tasks, clock);
        await service.IndexFileAsync(path, Token); Assert.Single(tasks.Tasks);
        clock.Now += DocumentIndexService.FailureRetryDelay + TimeSpan.FromSeconds(1);
        await service.IndexFileAsync(path, Token); Assert.Equal(2, tasks.Tasks.Count);
        await service.IndexFileAsync(path, Token, retryFailed: true); Assert.Equal(3, tasks.Tasks.Count);
        File.Delete(path);
        using (var word = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
            word.AddMainDocumentPart().Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                new DocumentFormat.OpenXml.Wordprocessing.Body(new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                    new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("repaired content")))));
        await service.IndexFileAsync(path, Token);
        Assert.Single(tasks.Tasks.Where(t => t.State == BackgroundTaskState.Completed));
        using var connection = database.GetConnection(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT status || ':' || retry_after FROM document_index";
        Assert.Equal("Ready:0", command.ExecuteScalar());
        await service.IndexFileAsync(path, Token); Assert.Equal(4, tasks.Tasks.Count);
        await tasks.ShutdownAsync();
    }

    [Fact]
    public async Task OfficeBinaryAttachmentsDoNotConsumeTheXmlExtractionBudget()
    {
        var path = Path.Combine(_root, "large-attachment.xlsx");
        using (var book = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbook = book.AddWorkbookPart(); workbook.Workbook = new Workbook();
            var worksheet = workbook.AddNewPart<WorksheetPart>();
            worksheet.Worksheet = new Worksheet(new SheetData(new Row(new Cell
            {
                CellReference = "A1", DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text("attachment-independent content"))
            })));
            workbook.Workbook.Append(new Sheets(new Sheet { Name = "Sheet1", SheetId = 1, Id = workbook.GetIdOfPart(worksheet) }));
        }
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        using (var attachment = zip.CreateEntry("xl/embeddings/oleObject1.bin", System.IO.Compression.CompressionLevel.NoCompression).Open())
        {
            var buffer = new byte[1024 * 1024];
            for (var i = 0; i < 39; i++) await attachment.WriteAsync(buffer, Token);
        }
        Assert.True(new FileInfo(path).Length > DocumentContentExtractor.MaxFileBytes);
        var content = await new DocumentContentExtractor(null!).ExtractAsync(path, Token);
        Assert.Contains(content.Segments, s => s.Text.Contains("attachment-independent content") && s.Location.Cell == "A1");
        var copilot = await new MacExplorer.Copilot.CopilotContentExtractor(null!, null!).ExtractPageAsync(path, cancellationToken: Token);
        Assert.Contains("attachment-independent content", copilot.Text);
        var database = Database(); using var tags = new AiTagService(database); var tasks = new BackgroundTaskManager();
        await new DocumentIndexService(database, new DocumentContentExtractor(null!), tags, tasks).IndexFileAsync(path, Token);
        Assert.Equal(BackgroundTaskState.Completed, Assert.Single(tasks.Tasks).State);
        await tasks.ShutdownAsync();
    }

    [Fact]
    public async Task OfficeXmlExpansionAndTotalFileSizeRemainBounded()
    {
        var path = At("oversized-xml.docx"); File.Delete(path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var xml = zip.CreateEntry("word/document.xml").Open())
        {
            var buffer = new byte[1024 * 1024];
            for (var i = 0; i < 13; i++) await xml.WriteAsync(buffer, Token);
        }
        var error = await Assert.ThrowsAsync<IOException>(() => new DocumentContentExtractor(null!).ExtractAsync(path, Token));
        Assert.Equal(LocalizationText.Get("content.expanded-limit"), error.Message);
        using (var file = File.Create(path)) file.SetLength(DocumentContentExtractor.MaxOfficeFileBytes + 1);
        error = await Assert.ThrowsAsync<IOException>(() => new DocumentContentExtractor(null!).ExtractAsync(path, Token));
        Assert.Equal(LocalizationText.Get("content.office-too-large"), error.Message);
    }

    [Fact]
    public void VersionTwelveDocumentIndexMigratesWithoutLosingExistingContent()
    {
        var database = Database(); using var connection = database.GetConnection(); using var command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE document_index DROP COLUMN retry_after;
            INSERT INTO document_index VALUES('/kept.txt',123,456,'Ready',NULL);
            INSERT INTO document_segments VALUES('/kept.txt',0,'kept content','{}');
            DELETE FROM schema_version; INSERT INTO schema_version VALUES(12);
            """;
        command.ExecuteNonQuery();
        SqliteSchema.Initialize(connection); SqliteSchema.Initialize(connection);
        command.CommandText = "SELECT retry_after FROM document_index WHERE path='/kept.txt'";
        Assert.Equal(0L, command.ExecuteScalar());
        command.CommandText = "SELECT text FROM document_segments WHERE path='/kept.txt'";
        Assert.Equal("kept content", command.ExecuteScalar());
        command.CommandText = "SELECT max(version) FROM schema_version";
        Assert.Equal((long)SqliteSchema.CurrentVersion, command.ExecuteScalar());
    }
}
