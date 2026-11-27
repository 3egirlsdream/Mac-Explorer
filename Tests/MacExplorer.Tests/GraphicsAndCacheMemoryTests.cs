using Avalonia.Headless.XUnit;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MacExplorer.Controls;
using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using SkiaSharp;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Area", "GraphicsAndCacheMemory")]
public sealed class GraphicsAndCacheMemoryTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(RuntimePaths.TestRoot ?? Path.GetTempPath(), "fk-gfx-cache-" + Guid.NewGuid().ToString("N"))).FullName;

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private DatabaseConnectionFactory CreateDatabase()
    {
        var factory = new DatabaseConnectionFactory(Path.Combine(_root, "db", Guid.NewGuid().ToString("N") + ".db"));
        using var connection = factory.GetConnection();
        SqliteSchema.Initialize(connection);
        return factory;
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private sealed class FakeClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubLauncher : IApplicationLauncherService
    {
        public Task OpenFileAsync(string filePath) => Task.CompletedTask;
        public Task OpenFileWithAppAsync(string filePath, string bundleIdentifier) => Task.CompletedTask;
        public Task OpenInTerminalAsync(string directoryPath) => Task.CompletedTask;
        public Task OpenInEditorAsync(string path, string cliName, string bundleId) => Task.CompletedTask;
        public Task RevealInFinderAsync(string path) => Task.CompletedTask;
    }

    private sealed class StubOpenWithAppService : IOpenWithAppService
    {
        public Task<List<OpenWithApp>> GetAllAsync() => Task.FromResult<List<OpenWithApp>>([]);
        public Task<List<OpenWithApp>> GetTopLevelAppsAsync() => Task.FromResult<List<OpenWithApp>>([]);
        public Task<List<OpenWithApp>> GetSubmenuAppsAsync() => Task.FromResult<List<OpenWithApp>>([]);
        public Task<string?> GetAppIconBase64Async(string bundleId) => Task.FromResult<string?>(null);
        public Task<string?> GetAppIconBase64ByPathAsync(string appPath) => Task.FromResult<string?>(null);
        public Task AddAsync(string bundleId, string label, bool isTopLevel, string? iconBase64 = null) => Task.CompletedTask;
        public Task UpdateAsync(int id, string? label, bool? isTopLevel, int? sortOrder) => Task.CompletedTask;
        public Task RemoveAsync(int id) => Task.CompletedTask;
        public Task<int> RemoveUnavailableAppsAsync() => Task.FromResult(0);
        public Task<List<AppListItem>> GetInstalledAppsAsync() => Task.FromResult<List<AppListItem>>([]);
    }

    [AvaloniaFact]
    [Trait("Kind", "UI")]
    public async Task BackgroundThemeDoesNotDecodeWhenDisabledAndReleasesOnDisableOrIdle()
    {
        using var access = new GrantedFeatureAccess();
        var themeDir = Path.Combine(_root, "themes");
        var settings = new ShortcutMemorySettings();
        settings.Set(BackgroundThemeService.SettingKey, "{\"Enabled\":false,\"PresetId\":\"cyber-circuit\"}");

        using (var disabledService = new BackgroundThemeService(settings, themeDir))
        {
            var window = new AppWindow { Width = 600, Height = 400 };
            try
            {
                using var controller = new WindowBackgroundTheme(window, disabledService);
                var state = ((IMemoryDiagnosticSource)disabledService).GetMemoryState();
                Assert.Equal(0, state.Entries);
                Assert.Equal(0, state.Bytes);
                Assert.Null(disabledService.Image);
                Assert.False(disabledService.HasFullArtwork);

                // Even if settings UI initializes while disabled, only lightweight previews load (no full texture).
                await disabledService.InitializeAsync();
                Assert.NotNull(disabledService.Image);
                Assert.False(disabledService.HasFullArtwork);
                var previewState = ((IMemoryDiagnosticSource)disabledService).GetMemoryState();
                Assert.True(previewState.Bytes < 1024 * 1024, $"Expected preview-only bytes < 1 MiB, got {previewState.Bytes}");
            }
            finally { window.Close(); }

            disabledService.TrimMemory(DateTime.UtcNow.AddMinutes(5));
            var afterTrim = ((IMemoryDiagnosticSource)disabledService).GetMemoryState();
            Assert.Equal(0, afterTrim.Entries);
            Assert.Equal(0, afterTrim.Bytes);
        }

        // Now test enabled -> disabled -> idle trim -> reload on demand.
        using var service = new BackgroundThemeService(settings, themeDir);
        await service.ImportAsync(new MemoryStream(CreatePng(240, 160)), "sample.png", Token);
        Assert.True(service.IsActive);
        Assert.True(service.HasFullArtwork);
        var fullBytes = ((IMemoryDiagnosticSource)service).GetMemoryState().Bytes;
        Assert.True(fullBytes > 0);

        // Disabling immediately releases the full texture.
        service.Update(enabled: false, strength: 40);
        Assert.False(service.IsActive);
        Assert.False(service.HasFullArtwork);
        Assert.True(((IMemoryDiagnosticSource)service).GetMemoryState().Bytes < fullBytes);

        // Idle trim while disabled frees remaining preview artwork completely.
        service.TrimMemory(DateTime.UtcNow.AddMinutes(5));
        var evictedState = ((IMemoryDiagnosticSource)service).GetMemoryState();
        Assert.Equal(0, evictedState.Entries);
        Assert.Equal(0, evictedState.Bytes);
        Assert.Null(service.Image);

        // Re-enabling reloads full artwork on demand.
        service.Update(enabled: true, strength: 40);
        await service.InitializeAsync();
        Assert.True(service.IsActive);
        Assert.True(service.HasFullArtwork);

        // With theme enabled and zero active consumers, idle trim releases artwork; InitializeAsync reloads on demand.
        Assert.Equal(0, service.ActiveConsumerCount);
        service.TrimMemory(DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(0, ((IMemoryDiagnosticSource)service).GetMemoryState().Entries);
        Assert.False(service.HasFullArtwork);

        await service.InitializeAsync();
        Assert.True(service.HasFullArtwork);
        Assert.True(service.IsActive);
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task OpenWithAppServiceIconCacheEnforcesEntryAndByteBoundsAndIdleTrim()
    {
        var database = CreateDatabase();
        var calls = 0;
        using var service = new OpenWithAppService(
            database,
            extractIconByPath: path =>
            {
                Interlocked.Increment(ref calls);
                return new string('A', 100);
            },
            maxIconEntries: 3,
            maxIconBytes: 1024);

        for (var i = 0; i < 6; i++)
        {
            var icon = await service.GetAppIconBase64ByPathAsync($"/Applications/App{i}.app");
            Assert.NotNull(icon);
            Assert.InRange(service.IconCacheCount, 1, 3);
            Assert.InRange(service.IconCacheBytes, 1, 1024);
        }

        Assert.Equal(3, service.IconCacheCount);
        var beforeHitCalls = calls;
        _ = await service.GetAppIconBase64ByPathAsync("/Applications/App5.app");
        Assert.Equal(beforeHitCalls, calls);

        service.TrimMemory(DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(0, service.IconCacheCount);
        Assert.Equal(0, service.IconCacheBytes);
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task MetadataCachesRespectCapacityBoundsAndIdleTrim()
    {
        // MacDisplayNameService
        var displayNameCalls = 0;
        var displayNames = new MacDisplayNameService(path =>
        {
            displayNameCalls++;
            return Path.GetFileName(path);
        }, maxEntries: 4);

        for (var i = 0; i < 10; i++)
            Assert.Equal($"dir{i}", displayNames.GetDisplayName($"/tmp/dir{i}"));
        Assert.Equal(4, displayNames.CachedCount);
        var prevDisplayCalls = displayNameCalls;
        Assert.Equal("dir9", displayNames.GetDisplayName("/tmp/dir9"));
        Assert.Equal(prevDisplayCalls, displayNameCalls);
        displayNames.TrimMemory(DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(0, displayNames.CachedCount);

        // RatingService
        var database = CreateDatabase();
        using var ratings = new RatingService(database, maxCacheEntries: 4, idleRetainEntries: 2);
        for (var i = 0; i < 6; i++)
            await ratings.SetRatingAsync($"/tmp/file{i}.txt", (i % 5) + 1);
        Assert.Equal(4, ratings.CachedCount);
        Assert.Equal(1, ratings.GetRatingCached("/tmp/file5.txt"));
        Assert.Equal(0, ratings.GetRatingCached("/tmp/file0.txt"));
        ratings.TrimMemory(DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(2, ratings.CachedCount);

        // MacContextMenuService
        var contextMenu = new MacContextMenuService(
            new StubLauncher(),
            new StubOpenWithAppService(),
            queryRegisteredApplications: path => [new RegisteredApp { Name = "App", BundleIdentifier = "com.example.app" }],
            queryDefaultApplicationPath: _ => null,
            maxApplicationTypeEntries: 3,
            maxInstalledAppEntries: 4);

        for (var i = 0; i < 6; i++)
            await contextMenu.GetApplicationsForFileAsync($"/tmp/file.ext{i}");
        Assert.Equal(3, contextMenu.CachedApplicationTypeCount);
        contextMenu.TrimMemory(DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(0, contextMenu.CachedApplicationTypeCount);
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task DocumentIndexServiceSkipsUnchangedOversizedFileBeyondRetryDelayAndReindexesAfterChange()
    {
        using var access = new GrantedFeatureAccess();
        var directories = new DirectoryAccess(Path.Combine(_root, "access.json"), true, internalRoots: [_root, RuntimePaths.DataDirectory]);
        using var directoryScope = DirectoryAccess.UseForTests(directories);

        var path = Path.Combine(_root, "huge.docx");
        using (var file = File.Create(path))
            file.SetLength(DocumentContentExtractor.MaxOfficeFileBytes + 1);

        var database = CreateDatabase();
        using var tags = new AiTagService(database);
        var tasks = new BackgroundTaskManager();
        var clock = new FakeClock();
        try
        {
            var service = new DocumentIndexService(database, new DocumentContentExtractor(null!), tags, tasks, clock);
            await service.IndexFileAsync(path, Token);
            var firstFailure = Assert.Single(tasks.Tasks);
            Assert.Equal(BackgroundTaskState.Failed, firstFailure.State);

            // Advance far past the normal 10-minute retry delay: unchanged oversized file must NOT be re-queued.
            clock.Now += DocumentIndexService.FailureRetryDelay + TimeSpan.FromHours(24);
            await service.IndexFileAsync(path, Token);
            Assert.Single(tasks.Tasks);

            // A fresh service instance (app restart) must also skip the unchanged oversized file.
            var restarted = new DocumentIndexService(database, new DocumentContentExtractor(null!), tags, tasks, clock);
            await restarted.IndexFileAsync(path, Token);
            Assert.Single(tasks.Tasks);

            // Once the file is replaced with a valid smaller Office document, signature changes and indexing succeeds.
            File.Delete(path);
            using (var word = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
            {
                word.AddMainDocumentPart().Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                    new DocumentFormat.OpenXml.Wordprocessing.Body(
                        new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                            new DocumentFormat.OpenXml.Wordprocessing.Run(
                                new DocumentFormat.OpenXml.Wordprocessing.Text("compact document")))));
            }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(2));

            await restarted.IndexFileAsync(path, Token);
            Assert.Equal(2, tasks.Tasks.Count);
            Assert.Single(tasks.Tasks.Where(t => t.State == BackgroundTaskState.Completed));
        }
        finally
        {
            await tasks.ShutdownAsync();
        }
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task ExcelStreamingExtractionReadsSharedStringsAndInlineCells()
    {
        var directories = new DirectoryAccess(Path.Combine(_root, "access-xlsx.json"), true, internalRoots: [_root, RuntimePaths.DataDirectory]);
        using var directoryScope = DirectoryAccess.UseForTests(directories);

        var path = Path.Combine(_root, "sheet.xlsx");
        using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook();
            var shared = workbook.AddNewPart<SharedStringTablePart>();
            shared.SharedStringTable = new SharedStringTable(
                new SharedStringItem(new Text("SharedAlpha")),
                new SharedStringItem(new Text("SharedBeta")));
            var worksheet = workbook.AddNewPart<WorksheetPart>("rId1");
            worksheet.Worksheet = new Worksheet(new SheetData(new Row(
                new Cell { CellReference = "A1", DataType = CellValues.SharedString, CellValue = new CellValue("0") },
                new Cell { CellReference = "B1", DataType = CellValues.InlineString, InlineString = new InlineString(new Text("InlineGamma")) },
                new Cell { CellReference = "C1", DataType = CellValues.SharedString, CellValue = new CellValue("1") })));
            workbook.Workbook.Append(new Sheets(new Sheet { Id = "rId1", SheetId = 1U, Name = "Data" }));
        }

        var content = await new DocumentContentExtractor(null!).ExtractAsync(path, Token);
        Assert.False(content.IsTruncated);
        Assert.Contains(content.Segments, s => s.Text == "A1: SharedAlpha" && s.Location.Sheet == "Data" && s.Location.Cell == "A1");
        Assert.Contains(content.Segments, s => s.Text == "B1: InlineGamma" && s.Location.Sheet == "Data" && s.Location.Cell == "B1");
        Assert.Contains(content.Segments, s => s.Text == "C1: SharedBeta" && s.Location.Sheet == "Data" && s.Location.Cell == "C1");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch { }
    }
}
