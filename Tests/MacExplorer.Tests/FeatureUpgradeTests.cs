using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Search;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "FeatureUpgrade")]
public sealed partial class FeatureUpgradeTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(RuntimePaths.TestRoot ?? Path.GetTempPath(), "fk-features-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly GrantedFeatureAccess _access = new();
    private readonly DirectoryAccess _directories;
    private readonly IDisposable _directoryScope;
    public FeatureUpgradeTests()
    {
        _directories = new DirectoryAccess(Path.Combine(_root, "access.json"), true, internalRoots: [_root, RuntimePaths.DataDirectory]);
        _directoryScope = DirectoryAccess.UseForTests(_directories);
    }
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string At(string name, string content = "data")
    { var path = Path.Combine(_root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); return path; }
    private DatabaseConnectionFactory Database()
    {
        var factory = new DatabaseConnectionFactory(Path.Combine(_root, "db", Guid.NewGuid() + ".db"));
        using var connection = factory.GetConnection(); SqliteSchema.Initialize(connection); return factory;
    }
    private sealed class Choice(FileConflictAction action, bool all = false, Action? beforeReturn = null) : IFileConflictResolver
    {
        internal int Count;
        public Task<FileConflictDecision> ResolveAsync(FileConflict conflict, CancellationToken cancellationToken)
        { Count++; beforeReturn?.Invoke(); return Task.FromResult(new FileConflictDecision(action, all)); }
    }
    private CompositeFileService Files(Choice choice, out FileOperationHistoryService history, out BackgroundTaskManager tasks)
    {
        var local = new MacFileService(); var manager = new BackgroundTaskManager();
        FileOperationHistoryService? store = null;
        var files = new CompositeFileService(local, null!, manager, history: () => store, conflicts: choice);
        store = new FileOperationHistoryService(files, tasks: manager); history = store; tasks = manager; return files;
    }
    [Theory]
    [InlineData("DD")]
    [InlineData("AU")]
    [InlineData("UD")]
    [InlineData("UA")]
    [InlineData("DU")]
    [InlineData("AA")]
    [InlineData("UU")]
    public void AllGitConflictPairsAreRecognized(string pair) => Assert.Equal(GitFileStatus.Conflicted, GitStatusService.ParsePorcelainStatus(pair));

    [Fact]
    public async Task LocalReplacementCanBeUndoneWithoutTouchingSourceOrTrash()
    {
        var source = At("source/report.txt", "new"); var target = At("target/report.txt", "old");
        var files = Files(new(FileConflictAction.Replace), out var history, out var tasks);
        Assert.Equal(target, await files.CopyWithProgressAsync(source, Path.GetDirectoryName(target)!, ct: Token));
        Assert.Equal("new", File.ReadAllText(target)); Assert.Equal("new", File.ReadAllText(source));
        Assert.Equal(FileOperationKind.Replace, Assert.Single(history.Records).Kind);
        Assert.True(File.Exists(Path.Combine(history.Records[0].BackupDirectory!, "recovery.json")));
        Assert.True(await history.UndoLastAsync()); Assert.Equal("old", File.ReadAllText(target)); Assert.Equal("new", File.ReadAllText(source));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(target)!, ".MacExplorer-replace*")); await tasks.ShutdownAsync();
    }
    [Fact]
    public async Task ReplacementUndoRejectsLaterChangesAndKeepsBothVersions()
    {
        var source = At("source/file.txt", "new"); var target = At("target/file.txt", "old");
        var files = Files(new(FileConflictAction.Replace), out var history, out var tasks);
        await files.CopyWithProgressAsync(source, Path.GetDirectoryName(target)!, ct: Token);
        File.WriteAllText(target, "changed after replace"); Assert.False(await history.UndoLastAsync());
        Assert.Equal("changed after replace", File.ReadAllText(target));
        Assert.Equal("old", File.ReadAllText(history.Records[0].BackupPath!)); await tasks.ShutdownAsync();
    }
    [Theory]
    [InlineData(FileConflictAction.Skip)]
    [InlineData(FileConflictAction.KeepBoth)]
    [InlineData(FileConflictAction.ReplaceOlder)]
    public async Task ConflictChoicesNeverOverwriteUnexpectedly(FileConflictAction action)
    {
        var source = At("source/file.txt", "new"); var target = At("target/file.txt", "old");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddDays(-1));
        var files = Files(new(action), out _, out var tasks);
        var output = await files.CopyWithProgressAsync(source, Path.GetDirectoryName(target)!, ct: Token);
        Assert.Equal("old", File.ReadAllText(target)); Assert.Equal("new", File.ReadAllText(source));
        if (action == FileConflictAction.KeepBoth) { Assert.NotEqual(target, output); Assert.Equal("new", File.ReadAllText(output)); }
        else Assert.Equal(FileTaskItemState.Skipped, Assert.Single(tasks.Tasks[0].Items).State);
        await tasks.ShutdownAsync();
    }
    [Fact]
    public async Task DirectoryMergeKeepsDestinationOnlyFilesAndSharesTheBatchChoice()
    {
        var source = Path.GetDirectoryName(At("source/folder/a.txt", "new a"))!; At("source/folder/b.txt", "new b");
        var target = Path.GetDirectoryName(At("target/folder/a.txt", "old a"))!; At("target/folder/b.txt", "old b"); At("target/folder/keep.txt", "keep");
        var choice = new Choice(FileConflictAction.Replace, true); var files = Files(choice, out var history, out var tasks);
        await files.CopyWithProgressAsync(source, Path.GetDirectoryName(target)!, ct: Token);
        Assert.Equal(1, choice.Count); Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
        Assert.True(await history.UndoLastAsync()); Assert.Equal("old a", File.ReadAllText(Path.Combine(target, "a.txt"))); Assert.Equal("old b", File.ReadAllText(Path.Combine(target, "b.txt")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt"))); await tasks.ShutdownAsync();
    }
    [Fact]
    public async Task DocumentIndexFindsContentLocationsAndDefaultApiDoesNotSendExcerpts()
    {
        var database = Database(); using var tags = new AiTagService(database); var tasks = new BackgroundTaskManager();
        var path = At("documents/notes.md", "first\nsecond launch keyword\nthird");
        var documents = new DocumentIndexService(database, new DocumentContentExtractor(null!), tags, tasks);
        await documents.IndexFileAsync(path, Token); var catalog = new SearchCatalog(database);
        var query = new IndexedFileQuery { Query = "launch keyword", Source = "content" };
        var result = await catalog.SearchResourcesAsync(_root, query, new(), Token, true);
        var match = Assert.Single(result.Items); Assert.Equal(2, Assert.Single(match.ContentMatches).Location.Number);
        var privateResult = await catalog.SearchResourcesAsync(_root, query, new(), Token);
        Assert.Empty(Assert.Single(privateResult.Items).ContentMatches); Assert.DoesNotContain("second launch", JsonSerializer.Serialize(privateResult));
        File.WriteAllText(path, "changed"); Assert.Empty((await catalog.SearchResourcesAsync(_root, query, new(), Token, true)).Items);
        await documents.IndexFileAsync(path, Token); Assert.Empty((await catalog.SearchResourcesAsync(_root, query, new(), Token, true)).Items);
        File.WriteAllText(path, "restored launch keyword"); await documents.IndexFileAsync(path, Token);
        var renamed = Path.Combine(Path.GetDirectoryName(path)!, "renamed.md"); File.Move(path, renamed);
        await tags.UpdateFilePathAsync(path, renamed);
        Assert.Equal(renamed, Assert.Single((await catalog.SearchResourcesAsync(_root, query, new(), Token, true)).Items).FullPath);
        File.Delete(renamed); await tags.DeleteAnalysisForFileAsync(renamed);
        Assert.Empty((await catalog.SearchResourcesAsync(_root, query, new(), Token, true)).Items);
        await tasks.ShutdownAsync();
    }
    [Fact]
    public async Task OfficeExtractionCarriesParagraphAndRetainsResourceLimits()
    {
        var path = Path.Combine(_root, "document.docx");
        using (var word = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            word.AddMainDocumentPart().Document = new Document(new Body(new Paragraph(new Run(new Text("one"))), new Paragraph(new Run(new Text("two")))));
        var content = await new DocumentContentExtractor(null!).ExtractAsync(path, Token);
        Assert.Equal("paragraph", content.Segments[1].Location.Kind); Assert.Equal(2, content.Segments[1].Location.Number);
        Assert.Equal("one\ntwo", (await new MacExplorer.Copilot.CopilotContentExtractor(null!, null!).ExtractPageAsync(path, cancellationToken: Token)).Text);
    }
    [Fact]
    public async Task DirectoryPreferencesAndLegacySessionStatesRoundTripWithoutViewOrGroup()
    {
        var service = new DirectoryPreferencesService(Database());
        var a = new DirectoryPreferences(SortField.Size, false, SortPriority.Files, FileListFilterState.Empty with { FileName = "invoice" }, [300, 160, 100, 230], true, false, true);
        var b = a with { Sort = SortField.Name, Filters = FileListFilterState.Empty };
        await service.SaveAsync("/a", a); await service.SaveAsync("__remote:server:/a", b);
        Assert.Equal(SortField.Size, (await service.ReadAsync("/a"))!.Sort); Assert.Equal(SortField.Name, (await service.ReadAsync("__remote:server:/a"))!.Sort);
        var session = FolderTabSession.Capture(["/a", null, "/b"], 2, preferences: new() { [0] = a, [2] = b });
        var json = JsonSerializer.Serialize(session); var copy = JsonSerializer.Deserialize<FolderTabSession>(json)!;
        Assert.Equal("invoice", copy.Preferences[0].Filters.FileName); Assert.Equal(SortField.Name, copy.Preferences[1].Sort);
        Assert.DoesNotContain("ViewMode", json); Assert.DoesNotContain("GroupField", json);
    }
    public void Dispose() { _directoryScope.Dispose(); _directories.Dispose(); _access.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
