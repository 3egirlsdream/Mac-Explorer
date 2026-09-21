using System.Diagnostics;
using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

public sealed class PdfAnalysisTests : IDisposable
{
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("MACEXPLORER_TEST_ROOT") ?? Path.GetTempPath(), $"pdf-analysis-{Guid.NewGuid():N}");
    private readonly AiTagService _tags;
    private readonly SettingsService _settings;
    private readonly BackgroundTaskManager _tasks = new();
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public PdfAnalysisTests()
    {
        Directory.CreateDirectory(_root);
        var factory = new DatabaseConnectionFactory(Path.Combine(_root, "index.db"));
        using (var connection = factory.GetConnection()) SqliteSchema.Initialize(connection);
        _tags = new AiTagService(factory);
        _settings = new SettingsService(factory);
    }

    [Fact]
    public async Task CacheIsSharedAcrossTabs_AndChangedFileReplacesOldText()
    {
        var entry = FileEntry("book.PDF");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extractor = new Extractor(async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return Texts("旧版正文"); });
        using var service = new PdfAnalysisService(extractor, _tags);
        var first = Vm(service).TriggerImageAnalysisAsync([entry], _root, TestToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestToken);
        var second = Vm(service).TriggerImageAnalysisAsync([entry], _root, TestToken);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, extractor.Calls);
        Assert.Equal([entry.FullPath], await _tags.SearchByTagAsync("正文"));
        await Vm(service).TriggerImageAnalysisAsync([entry], _root, TestToken);
        Assert.Equal(1, extractor.Calls);
        File.SetLastWriteTime(entry.FullPath, entry.LastModified.AddSeconds(5));
        extractor.Extract = (_, _) => Task.FromResult(Texts("新版内容"));
        await Vm(service).TriggerImageAnalysisAsync([Entry(entry.FullPath)], _root, TestToken);
        Assert.Equal(2, extractor.Calls);
        Assert.Empty(await _tags.SearchByTagAsync("旧版"));
        Assert.Single(await _tags.SearchByTagAsync("新版"));
    }

    [Fact]
    public async Task DifferentDocumentsShareOneExtractionSlot_AndWaitingRequestCanCancel()
    {
        var first = FileEntry("first.pdf");
        var second = FileEntry("second.pdf");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extractor = new Extractor(async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Texts("串行处理");
        });
        using var service = new PdfAnalysisService(extractor, _tags);
        var running = service.AnalyzeAsync(first.FullPath, first.LastModified.Ticks, null, TestToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestToken);
        using var waitingToken = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var waiting = service.AnalyzeAsync(second.FullPath, second.LastModified.Ticks, null, waitingToken.Token);
        Assert.Equal(1, extractor.Calls);
        waitingToken.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        release.SetResult();
        await running;
        Assert.False(await _tags.IsFileAnalyzedAsync(second.FullPath, second.LastModified.Ticks));
        await service.AnalyzeAsync(second.FullPath, second.LastModified.Ticks, null, TestToken);
        Assert.Equal(2, extractor.Calls);
    }

    [Fact]
    public async Task FailuresContinue_EmptyFilesComplete_AndMissingFilesAreCleaned()
    {
        var bad = FileEntry("bad.pdf");
        var blank = FileEntry("blank.pdf");
        var good = FileEntry("good.pdf");
        var extractor = new Extractor((path, _) => path == bad.FullPath
            ? throw new IOException("damaged") : Task.FromResult(path == blank.FullPath ? Texts() : Texts("采购订单", "采购订单")));
        using var service = new PdfAnalysisService(extractor, _tags);
        var vm = Vm(service);
        await vm.TriggerImageAnalysisAsync([bad, blank, good], _root, TestToken);
        Assert.False(await _tags.IsFileAnalyzedAsync(bad.FullPath, bad.LastModified.Ticks));
        Assert.True(await _tags.IsFileAnalyzedAsync(blank.FullPath, blank.LastModified.Ticks));
        Assert.Equal([good.FullPath], await _tags.SearchByTagAsync("采购"));
        Assert.Equal(BackgroundTaskState.Failed, Assert.Single(_tasks.Tasks).State);
        Assert.Contains("bad.pdf", _tasks.Tasks[0].ErrorDetail);
        // An empty/filtered listing must retain a PDF that still exists.
        await vm.TriggerImageAnalysisAsync([], _root, TestToken);
        Assert.Single(await _tags.SearchByTagAsync("采购"));
        File.Delete(good.FullPath);
        await vm.TriggerImageAnalysisAsync([], _root, TestToken);
        Assert.Empty(await _tags.SearchByTagAsync("采购"));
    }

    [Theory]
    [InlineData("navigation")]
    [InlineData("setting")]
    [InlineData("task")]
    public async Task CancellationDoesNotCommit_AndCanRetry(string source)
    {
        var entry = FileEntry("cancel.pdf");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extractor = new Extractor(async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Texts(); });
        using var service = new PdfAnalysisService(extractor, _tags);
        using var navigation = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var vm = Vm(service);
        var running = vm.TriggerImageAnalysisAsync([entry], _root, navigation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestToken);
        if (source == "navigation") navigation.Cancel();
        else if (source == "setting") Vm(service).IsAiAnalysisEnabled = false; // another tab
        else _tasks.CancelTask(Assert.Single(_tasks.Tasks).Id);
        await running.WaitAsync(TimeSpan.FromSeconds(5), TestToken);
        Assert.False(await _tags.IsFileAnalyzedAsync(entry.FullPath, entry.LastModified.Ticks));
        Assert.Equal(BackgroundTaskState.Cancelled, Assert.Single(_tasks.Tasks).State);
        vm.IsAiAnalysisEnabled = false;
        vm.IsAiAnalysisEnabled = true;
        extractor.Extract = (_, _) => Task.FromResult(Texts("重试成功"));
        await vm.TriggerImageAnalysisAsync([entry], _root, TestToken);
        Assert.Single(await _tags.SearchByTagAsync("重试"));
    }

    [Fact]
    public async Task ModifiedDuringExtractionIsNotCommitted_AndRemoteEntriesAreIgnored()
    {
        var entry = FileEntry("changed.pdf");
        var extractor = new Extractor((path, _) =>
        {
            File.SetLastWriteTime(path, entry.LastModified.AddSeconds(5));
            return Task.FromResult(Texts("过时结果"));
        });
        using var service = new PdfAnalysisService(extractor, _tags);
        await Vm(service).TriggerImageAnalysisAsync([entry,
            new() { FullPath = "sftp://host/remote.pdf", Extension = ".pdf" },
            new() { FullPath = Path.Combine(_root, "virtual.pdf"), Extension = ".pdf", IsVirtual = true }], _root, TestToken);
        Assert.Equal(1, extractor.Calls);
        Assert.Empty(await _tags.SearchByTagAsync("过时"));
        Assert.False(await _tags.IsFileAnalyzedAsync(entry.FullPath, entry.LastModified.Ticks));
    }

    [Fact]
    public async Task PdfTagsSurviveRename_AndDeleteWithDirectoryPrefix()
    {
        var entry = FileEntry("rename.pdf");
        using var service = new PdfAnalysisService(new Extractor((_, _) => Task.FromResult(Texts("目录检索"))), _tags);
        await service.AnalyzeAsync(entry.FullPath, entry.LastModified.Ticks, null, TestToken);
        var renamed = Path.Combine(_root, "renamed.pdf");
        File.Move(entry.FullPath, renamed);
        await _tags.UpdateFilePathAsync(entry.FullPath, renamed);
        Assert.Equal([renamed], await _tags.SearchByTagAsync("目录"));
        await _tags.DeleteAnalysisForPathPrefixAsync(_root);
        Assert.Empty(await _tags.SearchByTagAsync("目录"));
    }

    [Fact]
    public async Task NativeHelperExtractsTextScansAndMixedPages_AndRejectsInvalidDocuments()
    {
        var repo = RepositoryRoot();
        var fixtures = Path.Combine(_root, "fixtures");
        await RunAsync("/usr/bin/xcrun", ["swift", Path.Combine(repo, "Tools/Testing/PdfFixtures.swift"), fixtures]);
        var config = AppContext.BaseDirectory.Contains("/Release/") ? "Release" : "Debug";
        var helper = Path.Combine(repo, "bin", config, "net10.0/osx-arm64/MacExplorer.ImageAnalysis");
        var extractor = new MacPdfTextExtractionService(helper, TimeSpan.FromSeconds(60));
        using var service = new PdfAnalysisService(extractor, _tags);
        foreach (var name in new[] { "text.pdf", "scan.pdf", "mixed.pdf", "blank.pdf" })
        {
            var entry = Entry(Path.Combine(fixtures, name));
            var updates = new List<PdfAnalysisProgress>();
            await service.AnalyzeAsync(entry.FullPath, entry.LastModified.Ticks, new ProgressSink(updates.Add), TestToken);
            Assert.True(await _tags.IsFileAnalyzedAsync(entry.FullPath, entry.LastModified.Ticks));
            Assert.Equal(updates[^1].TotalPages, updates[^1].CompletedPages);
            var tags = await _tags.GetTagsForFileAsync(entry.FullPath);
            if (name == "blank.pdf") Assert.Empty(tags);
            if (name is "text.pdf" or "mixed.pdf") Assert.Contains(tags, t => t.TagValue.Contains("原生文字测试"));
            if (name is "scan.pdf" or "mixed.pdf") Assert.Contains(tags, t => t.TagValue.Contains("4826"));
        }
        Assert.Equal(2, (await _tags.SearchByTagAsync("Native invoice")).Count);
        Assert.Equal(2, (await _tags.SearchByTagAsync("4826")).Count);
        foreach (var name in new[] { "locked.pdf", "corrupt.pdf", "oversized.pdf" })
        {
            var entry = Entry(Path.Combine(fixtures, name));
            await Assert.ThrowsAsync<IOException>(() => service.AnalyzeAsync(entry.FullPath, entry.LastModified.Ticks, null, TestToken));
            Assert.False(await _tags.IsFileAnalyzedAsync(entry.FullPath, entry.LastModified.Ticks));
        }
        // Existing image invocation/protocol still produces OCR text.
        var imageJson = await RunAsync(helper, [Path.Combine(fixtures, "scan.png")]);
        Assert.Contains("4826", imageJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HungHelperIsKilledOnTimeoutOrCancellation(bool cancel)
    {
        var pidPath = Path.Combine(_root, "pid");
        var helper = Path.Combine(_root, "hung-helper");
        await File.WriteAllTextAsync(helper, $"#!/bin/sh\necho $$ > '{pidPath}'\nexec /bin/sleep 30\n", TestToken);
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var service = new MacPdfTextExtractionService(helper, TimeSpan.FromSeconds(3));
        var running = service.ExtractAsync("ignored.pdf", null, cancellation.Token);
        if (cancel)
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
            startup.CancelAfter(TimeSpan.FromSeconds(10));
            while (!File.Exists(pidPath) && !running.IsCompleted) await Task.Delay(10, startup.Token);
            Assert.True(File.Exists(pidPath), "Helper did not reach startup before timeout.");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
        else await Assert.ThrowsAsync<TimeoutException>(() => running);
        var pid = int.Parse(await File.ReadAllTextAsync(pidPath, TestToken));
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    private AiViewModel Vm(PdfAnalysisService service) => new(aiTagService: _tags, taskManager: _tasks, settingsService: _settings, pdfAnalysisService: service);
    private FileSystemEntry FileEntry(string name) { var path = Path.Combine(_root, name); File.WriteAllText(path, "fixture"); return Entry(path); }
    private static FileSystemEntry Entry(string path) => new() { FullPath = path, Name = Path.GetFileName(path), Extension = Path.GetExtension(path), LastModified = File.GetLastWriteTime(path) };
    private static IReadOnlyList<RecognizedText> Texts(params string[] values) => values.Select(t => new RecognizedText { Text = t, Confidence = 1 }).ToList();
    private sealed class Extractor(Func<string, CancellationToken, Task<IReadOnlyList<RecognizedText>>> extract) : IPdfTextExtractionService
    {
        public int Calls;
        public Func<string, CancellationToken, Task<IReadOnlyList<RecognizedText>>> Extract = extract;
        public Task<IReadOnlyList<RecognizedText>> ExtractAsync(string path, IProgress<PdfAnalysisProgress>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Extract(path, ct);
        }
    }
    private sealed class ProgressSink(Action<PdfAnalysisProgress> report) : IProgress<PdfAnalysisProgress>
    {
        public void Report(PdfAnalysisProgress progress) => report(progress);
    }
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "MacExplorer.csproj"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
    private static async Task<string> RunAsync(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestToken);
        var error = process.StandardError.ReadToEndAsync(TestToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(TestToken); } }
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }
    public void Dispose() { _tags.Dispose(); _settings.Dispose(); Directory.Delete(_root, true); }
}
