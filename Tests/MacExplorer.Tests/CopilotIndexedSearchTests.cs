using System.Text.Json;
using Avalonia.Headless.XUnit;
using MacExplorer.Copilot;
using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Search;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MacExplorer.Tests;

public sealed class CopilotIndexedSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(RuntimePaths.TestRoot ?? Path.GetTempPath(), "copilot-index-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteFileIndex _index;
    private readonly DatabaseConnectionFactory _factory;
    private readonly AiTagService _tags;
    private readonly SearchCatalog _catalog;
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public CopilotIndexedSearchTests()
    {
        _factory = new(Path.Combine(_root, ".data", "index.db"));
        _index = new(Path.Combine(_root, ".data", "index.db"), _factory);
        _tags = new(_factory);
        _catalog = new(_factory);
    }

    private string FileAt(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture bytes");
        return path;
    }
    private Task Analyze(string path, params string[] text) => _tags.SaveAnalysisResultAsync(path,
        File.GetLastWriteTime(path).Ticks, new ImageAnalysisResult
        {
            RecognizedTexts = text.Select((t, i) => new RecognizedText { Text = t, Confidence = (i + 1) / 10f }).ToList()
        }, Token);
    private Task<IndexedFileSearchResult> Search(IndexedFileQuery query) =>
        _catalog.SearchResourcesAsync(_root, query, new(), Token);
    private AppCapabilityRegistry Registry(IFileService? files = null, IMetadataService? metadata = null) => new(files!, null!, null!, null!, null!, null!, null!, null!,
        null!, null!, null!, null!, null!, new CopilotContentExtractor(null!, null!, _tags),
        null!, null!, null!, null!, null!, _catalog, metadata, _tags);

    [Fact]
    public async Task PdfContentUsesAnalysisEvenWithoutFileCacheOrSearchEntryAndNeverReturnsExcerpt()
    {
        var path = FileAt("unrelated-name.PDF");
        await Analyze(path, "第一段 应用场景概述 第二段", "机密正文不应返回搜索结果");
        var result = await Search(new() { Query = "应用场景概述", Source = "content", Extensions = ["pdf"] });
        var match = Assert.Single(result.Items);
        Assert.Equal(path, match.FullPath);
        Assert.Equal(["text", "extension"], match.MatchedSources);
        Assert.True(match.HasCurrentAnalysis);
        Assert.DoesNotContain("机密正文", JsonSerializer.Serialize(result));
        Assert.Equal(1, result.AnalyzedFiles);
        Assert.Contains(result.IndexStatus, s => s.StartsWith("Unindexed"));
    }

    [Fact]
    public async Task CachePreservesPageOrderAndPaginationWhileChangedPdfFallsBackToExtractor()
    {
        var path = FileAt("book.pdf");
        var firstText = new string('中', 19000);
        await Analyze(path, firstText, "末页 😀");
        var pdf = new CountingPdf();
        var extractor = new CopilotContentExtractor(pdf, null!, _tags);
        var first = await extractor.ExtractPageAsync(path, cancellationToken: Token);
        var second = await extractor.ExtractPageAsync(path, first.NextOffset, Token);
        Assert.True(first.HasMore);
        Assert.False(second.HasMore);
        Assert.Equal(firstText + "\n末页 😀", first.Text + second.Text);
        Assert.Equal("analysis-database", first.Source);
        Assert.Equal(0, pdf.Calls);
        File.SetLastWriteTime(path, File.GetLastWriteTime(path).AddSeconds(3));
        var fresh = await extractor.ExtractPageAsync(path, cancellationToken: Token);
        Assert.Equal("重新提取的正文", fresh.Text);
        Assert.Equal("file", fresh.Source);
        Assert.Equal(1, pdf.Calls);
        Assert.Empty((await Search(new() { Query = "末页", Source = "content" })).Items);
    }

    [Fact]
    public async Task LiteralUnicodeQueriesRespectRootHiddenFilesDeletionAndStaleAnalysis()
    {
        var target = FileAt("visible.pdf");
        var sibling = FileAt("../" + Path.GetFileName(_root) + "-sibling/outside.pdf");
        var hidden = FileAt(".hidden/item.pdf");
        var gone = FileAt("deleted.pdf");
        var stale = FileAt("changed.pdf");
        try
        {
            foreach (var path in new[] { target, sibling, hidden, gone, stale }) await Analyze(path, "字面 100%_' 单引号");
            File.Delete(gone);
            File.SetLastWriteTime(stale, File.GetLastWriteTime(stale).AddSeconds(4));
            Assert.Equal(target, Assert.Single((await Search(new() { Query = "100%_'", Source = "content" })).Items).FullPath);
            Assert.Empty((await Search(new() { Query = "100%_' OR 1=1 --", Source = "content" })).Items);
        }
        finally { Directory.Delete(Path.GetDirectoryName(sibling)!, true); }
    }

    [Fact]
    public async Task CameraDateLocationSemanticTagsRatingsAndFilePropertiesCombineBeforePaging()
    {
        var path = FileAt("holiday.jpg");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 9, 20, 2, 0, 0, DateTimeKind.Utc));
        await _tags.SaveAnalysisResultAsync(path, File.GetLastWriteTime(path).Ticks, new ImageAnalysisResult
        {
            CameraInfo = "Canon EOS R5", Location = new LocationInfo { PlaceName = "杭州" },
            DateInfo = new PhotoDateInfo { TakenAt = new(2026, 9, 18), Day = "2026-09-18", YearMonth = "2026-09" },
            Classifications = [new ClassificationLabel { TagType = "object", DisplayName = "猫", Confidence = 0.9f }]
        }, Token);
        await _index.UpdateDirectoryAsync(_root, [new FileSystemEntry
        {
            FullPath = path, Name = "holiday.jpg", Extension = ".jpg", Size = new FileInfo(path).Length,
            Created = File.GetCreationTimeUtc(path), LastModified = File.GetLastWriteTimeUtc(path)
        }], Token);
        Execute("INSERT INTO file_ratings VALUES(@path,4,1); INSERT INTO file_tags VALUES(@path,'旅行',0,1)", path);
        var query = new IndexedFileQuery
        {
            Query = "猫", Source = "analysis", Extensions = ["jpg"], FileTag = "旅行", MinRating = 4,
            Tags = [new("camera", "canon"), new("location", "杭州")],
            MinSize = 10, MaxSize = 30, TakenFrom = new(2026, 9, 18), TakenTo = new(2026, 9, 19),
            ModifiedFrom = DateTimeOffset.Parse("2026-09-20T10:00:00+08:00"),
            ModifiedTo = DateTimeOffset.Parse("2026-09-21T00:00:00+08:00"),
            CreatedFrom = new DateTimeOffset(File.GetCreationTimeUtc(path)).AddSeconds(-1),
            CreatedTo = new DateTimeOffset(File.GetCreationTimeUtc(path)).AddSeconds(1)
        };
        Assert.Equal(path, Assert.Single((await Search(query)).Items).FullPath);
        Assert.Empty((await Search(query with { MinRating = 5 })).Items);
        Assert.Empty((await Search(query with { TakenTo = new(2026, 9, 18), TakenFrom = null })).Items);
        Assert.Empty((await Search(query with { ModifiedTo = query.ModifiedFrom, ModifiedFrom = null })).Items);
        Assert.Contains("camera", await _catalog.GetAnalysisFieldsAsync(Token));
    }

    [Fact]
    public async Task NamedPeopleAndFutureAnalysisFieldsAreSearchableWithoutExposingFaceFeatures()
    {
        var path = FileAt("portrait.jpg");
        await Analyze(path);
        Execute("INSERT INTO face_clusters(id,display_name,created_at,updated_at) VALUES(1,'小王',1,1); INSERT INTO face_observations(file_path,cluster_id,bounding_box_x,bounding_box_y,bounding_box_w,bounding_box_h,feature_print,created_at) VALUES(@path,1,0,0,1,1,x'01020304',1); INSERT INTO ai_tags(file_path,tag_type,tag_value,confidence,created_at) VALUES(@path,'lens','RF 50mm',1,1)", path);
        var result = await Search(new() { Person = "小王", Tags = [new("lens", "50mm")] });
        Assert.Equal(path, Assert.Single(result.Items).FullPath);
        Assert.Contains("person", result.Items[0].MatchedSources);
        Assert.Contains("lens", await _catalog.GetAnalysisFieldsAsync(Token));
        Assert.DoesNotContain("01020304", JsonSerializer.Serialize(result));
        Assert.Contains("person", Assert.Single((await Search(new() { Query = "小王" })).Items).MatchedSources);
    }

    [AvaloniaFact]
    public async Task ContentWrongRouteReturnsApprovalGuidanceAndIndexResultsBecomeCandidateArtifacts()
    {
        var path = FileAt("answer.pdf");
        await Analyze(path, "应用场景概述");
        var store = new CopilotStore(Path.Combine(_root, ".data", "copilot.db"));
        using var engine = new CopilotEngine(Registry(), null!, null!, store, null!, () => null);
        using var wrong = JsonDocument.Parse(await engine.CallReadOnlyAsync("file.content", JsonSerializer.Serialize(new { path })));
        Assert.False(wrong.RootElement.GetProperty("Success").GetBoolean());
        Assert.Equal("PreviewOperation", wrong.RootElement.GetProperty("Data").GetProperty("RequiredTool").GetString());
        Assert.Empty(store.ListArtifacts(engine.SessionId, 20));
        using var result = JsonDocument.Parse(await engine.CallReadOnlyAsync("file.search-index",
            JsonSerializer.Serialize(new { path = _root, query = "应用场景概述", source = "content", extensions = new[] { "pdf" } })));
        var artifact = store.GetArtifact(engine.SessionId, result.RootElement.GetProperty("artifactId").GetString()!)!;
        using var saved = JsonDocument.Parse(artifact.Value);
        Assert.Equal(path, saved.RootElement.GetProperty("Data")[0].GetProperty("FullPath").GetString());
        Assert.Contains("未分析", saved.RootElement.GetProperty("Message").GetString());
        Assert.False(saved.RootElement.GetProperty("Coverage").GetProperty("HasMore").GetBoolean());
        using var invalid = JsonDocument.Parse(await engine.CallReadOnlyAsync("file.search-index",
            JsonSerializer.Serialize(new { path = _root, query = "应用场景概述", offset = -1 })));
        Assert.False(invalid.RootElement.GetProperty("Success").GetBoolean());
        Assert.Contains("offset", invalid.RootElement.GetProperty("Message").GetString());
    }

    [Fact]
    public async Task FileInfoExposesExifButExcludesRawSpotlightBodyAndCachedOcr()
    {
        var path = FileAt("photo.jpg");
        await Analyze(path, "secret cached OCR");
        var result = await Registry(new MacFileService(), new MetadataFixture()).ExecuteReadAsync("file.info",
            JsonSerializer.Serialize(new { path }), null);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var image = json.RootElement.GetProperty("Data").GetProperty("Image");
        Assert.Equal("Canon", image.GetProperty("CameraMake").GetString());
        Assert.Equal("RF 50mm", image.GetProperty("LensModel").GetString());
        Assert.Equal("1/100", image.GetProperty("ExposureTime").GetString());
        Assert.DoesNotContain("secret", json.RootElement.GetRawText());
    }

    [Fact]
    public async Task IndexResultsContinueWithoutDuplicatesAndIncludeUnindexedAnalysis()
    {
        for (var i = 0; i < 105; i++) await Analyze(FileAt($"page-{i:000}.pdf"), "needle");
        var first = await Search(new() { Query = "needle", Source = "content" });
        Assert.Equal(100, first.Items.Count);
        Assert.Equal(100, first.NextOffset);
        var second = await Search(new() { Query = "needle", Source = "content", Offset = first.NextOffset!.Value });
        Assert.Equal(5, second.Items.Count);
        Assert.False(second.HasMore);
        Assert.Equal(105, first.Items.Concat(second.Items).Select(x => x.FullPath).Distinct().Count());
    }

    private void Execute(string sql, string path)
    {
        using var connection = _factory.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@path", path);
        command.ExecuteNonQuery();
    }
    private sealed class CountingPdf : IPdfTextExtractionService
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<RecognizedText>> ExtractAsync(string path,
            IProgress<PdfAnalysisProgress>? progress = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<RecognizedText>>([new() { Text = "重新提取的正文" }]);
        }
    }
    private sealed class MetadataFixture : IMetadataService
    {
        public Task<FileMetadata> GetMetadataAsync(string path) => Task.FromResult(new FileMetadata
        {
            FullPath = path, ImageInfo = new ImageMetadata { CameraMake = "Canon", LensModel = "RF 50mm",
                ExposureTime = "1/100", AllProperties = new() { ["kMDItemTextContent"] = "secret raw Spotlight body" } }
        });
    }
    public void Dispose()
    {
        _tags.Dispose();
        _index.Dispose();
        Directory.Delete(_root, true);
    }
}
