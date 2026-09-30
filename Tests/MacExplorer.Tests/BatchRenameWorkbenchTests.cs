using System.Diagnostics;
using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using MacExplorer.Indexing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Automation;
using MacExplorer.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views.Dialogs;
using Xunit;

namespace MacExplorer.Tests;

public sealed class BatchRenameWorkbenchTests(ITestOutputHelper output)
{
    private static FileSystemEntry Entry(string path) => new()
    { FullPath = path, Name = Path.GetFileName(path), Created = new DateTime(2025, 1, 2), LastModified = new DateTime(2025, 3, 4) };
    private static List<BatchRenamePreviewItem> Compute(BatchRenameRequest request) =>
        BatchRenameRuleEngine.Generate(request, new Dictionary<string, DateTime?>(), default);

    [Fact]
    public void OrderedRulesHaveIndependentCountersAndExcludedItemsDoNotConsumeNumbers()
    {
        var entries = new[] { Entry("/fixtures/DSC_10.JPG"), Entry("/fixtures/DSC_2.JPG"), Entry("/fixtures/DSC_3.JPG") };
        var request = new BatchRenameRequest
        {
            Entries = entries, Options = new() { Sort = RenameSort.Name }, ExcludedPaths = new HashSet<string> { entries[2].FullPath },
            Rules = [new() { Type = BatchRenameRuleType.FindReplace, FindText = "DSC_" },
                new() { Type = BatchRenameRuleType.Sequence },
                new() { Type = BatchRenameRuleType.Sequence, SequenceStart = 10, SequenceStep = 2, Placement = RenamePosition.Before }]
        };
        var items = Compute(request);
        Assert.Equal(new[] { "010_2_001.JPG", "DSC_3.JPG", "012_10_002.JPG" }, items.Select(i => i.NewName));
        Assert.Equal(new[] { "2.JPG", "2_001.JPG", "010_2_001.JPG" }, items[0].StepNames);
        Assert.False(items[1].IsIncluded);
        request.Options.RestartPerDirectory = true;
        request = new BatchRenameRequest { Entries = [entries[0], Entry("/other/DSC_1.JPG")], Rules = request.Rules, Options = request.Options };
        Assert.All(Compute(request), i => Assert.StartsWith("010_", i.NewName));
    }

    [Fact]
    public void ManualNamesOverrideRuleErrorsWithoutMutatingSavedRulesOrNumbering()
    {
        var rules = new[] { new BatchRenameRule { Type = BatchRenameRuleType.Template, TemplateText = "{taken:yyyyMMdd}_{n:000}" } };
        var request = new BatchRenameRequest { Entries = [Entry("/fixtures/a.jpg"), Entry("/fixtures/b.jpg")], Rules = rules,
            ManualNames = new Dictionary<string, string> { ["/fixtures/a.jpg"] = "例外.jpg" } };
        var first = Compute(request);
        Assert.Equal("例外.jpg", first[0].NewName); Assert.False(first[0].HasError); Assert.True(first[0].IsManual);
        Assert.Contains("缺少拍摄时间", first[1].ErrorReason);
        var snapshot = request.Snapshot(); rules[0].FallbackToModified = true;
        Assert.True(Compute(snapshot)[1].HasError);
        Assert.Equal("20250304_002.jpg", Compute(request)[1].NewName);
    }

    [Fact]
    public void ReplacementDistinguishesLiteralTextCapturesOccurrenceAndRegexTimeout()
    {
        var request = new BatchRenameRequest { Entries = [Entry("/fixtures/one_one.txt")],
            Rules = [new() { FindText = "one", ReplaceText = "$1", MatchOccurrence = -1 }] };
        Assert.Equal("one_$1.txt", Compute(request)[0].NewName);
        request.Rules[0].UseRegex = true; request.Rules[0].FindText = "(one)"; request.Rules[0].ReplaceText = "$1!";
        Assert.Equal("one_one!.txt", Compute(request)[0].NewName);
        request.Rules[0].FindText = "^(a+)+$";
        request = new BatchRenameRequest { Entries = [Entry("/fixtures/" + new string('a', 180) + "!.txt")], Rules = request.Rules };
        var watch = Stopwatch.StartNew();
        Assert.Contains("超时", Compute(request)[0].ErrorReason);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData("👨‍👩‍👧‍👦你好.tar.gz", "你好.tar.gz")]
    [InlineData(".隐藏文件", "隐藏文件")]
    [InlineData("é你好", "你好")]
    public void CharacterRangesKeepEmojiCombiningCharactersAndExtensionsIntact(string before, string after)
    {
        var items = Compute(new BatchRenameRequest { Entries = [Entry("/fixtures/" + before)],
            Rules = [new() { Type = BatchRenameRuleType.RemoveText, Position = 0, RemoveLength = 1 }] });
        Assert.Equal(after, items[0].NewName);
    }

    [Fact]
    public void TitleCaseKeepsExistingSeparatorsAndCurrentDateIsFrozen()
    {
        var request = new BatchRenameRequest { Entries = [Entry("/fixtures/hello world_test-file.txt")], CapturedAt = new DateTime(2024, 5, 6),
            Rules = [new() { Type = BatchRenameRuleType.CaseConversion, CaseMode = CaseConversionMode.TitleCase },
                new() { Type = BatchRenameRuleType.Date, DateFormat = "yyyyMMdd" }] };
        Assert.Equal("Hello World_Test-File_20240506.txt", Compute(request)[0].NewName);
        var brace = Compute(new() { Entries = [Entry("/fixtures/doc{en}.txt")], Rules = [new() { Type = BatchRenameRuleType.Template }] });
        Assert.False(brace[0].HasError); Assert.Equal("doc{en}_001.txt", brace[0].NewName);
    }

    [Fact]
    public async Task FileAndFolderConflictsAreLocalToTheirDirectoryAndAncestorsAreBlocked()
    {
        using var env = new RenameFixture();
        var a = await env.File("one/a.txt", "A"); var b = await env.File("two/a.txt", "B");
        Directory.CreateDirectory(Path.Combine(env.Root, "one", "taken.txt"));
        var service = new BatchRenameService(env.Files);
        var plan = await service.GeneratePreviewAsync(new() { Entries = [a, b], Rules = [new() { Type = BatchRenameRuleType.Template, TemplateText = "taken" }] });
        Assert.True(plan.Items[0].HasConflict); Assert.False(plan.Items[1].HasConflict); Assert.False(plan.CanExecute);
        plan = await service.GeneratePreviewAsync(new() { Entries = [a, b], Rules = [new() { Type = BatchRenameRuleType.Template, TemplateText = "taken" }],
            Options = new() { ResolveConflicts = true } });
        Assert.True(plan.CanExecute); Assert.Equal("taken (2).txt", plan.Items[0].NewName);
        var folder = (await env.Files.GetEntryAsync(Path.GetDirectoryName(a.FullPath)!))!;
        plan = await service.GeneratePreviewAsync(new() { Entries = [folder, a], Rules = [new() { Type = BatchRenameRuleType.AddPrefix, PrefixText = "new-" }] });
        Assert.All(plan.Items, i => Assert.Contains("父文件夹", i.ErrorReason));
    }

    [Fact]
    public async Task ExchangeCycleCaseOnlyAndGroupedUndoPreserveFileContents()
    {
        using var env = new RenameFixture();
        var entries = new[] { await env.File("a.txt", "A"), await env.File("b.txt", "B"), await env.File("c.txt", "C") };
        var rename = new BatchRenameService(env.Files);
        var history = new FileOperationHistoryService(env.Files, batchRename: rename);
        var operation = new BatchRenameOperationService(rename, history, new BackgroundTaskManager(null));
        var names = new Dictionary<string, string> { [entries[0].FullPath] = "b.txt", [entries[1].FullPath] = "c.txt", [entries[2].FullPath] = "a.txt" };
        var plan = await rename.GeneratePreviewAsync(new() { Entries = entries, ManualNames = names });
        Assert.True(plan.CanExecute);
        var result = await operation.ExecuteAsync(plan);
        Assert.Equal(3, result.SuccessCount); Assert.Empty(result.Errors);
        Assert.Equal("C", File.ReadAllText(entries[0].FullPath)); Assert.Equal("A", File.ReadAllText(entries[1].FullPath));
        Assert.True(await history.UndoBatchAsync(result.HistoryBatchId!.Value)); Assert.False(history.CanUndo);
        Assert.Equal(new[] { "A", "B", "C" }, entries.Select(e => File.ReadAllText(e.FullPath)));
        plan = await rename.GeneratePreviewAsync(new() { Entries = [entries[0]], ManualNames = new Dictionary<string, string> { [entries[0].FullPath] = "A.TXT" } });
        Assert.True(plan.CanExecute); Assert.Equal(1, (await rename.ExecuteAsync(plan)).SuccessCount);
        Assert.Contains("A.TXT", Directory.GetFiles(env.Root).Select(Path.GetFileName));
        Assert.DoesNotContain(Directory.GetFiles(env.Root), p => Path.GetFileName(p).StartsWith(".macexplorer-"));
    }

    [Fact]
    public async Task ChangedSourcesAndNewTargetOccupantsPreventAllMutations()
    {
        using var env = new RenameFixture(); var a = await env.File("a.txt", "A"); var b = await env.File("b.txt", "B");
        var rename = new BatchRenameService(env.Files);
        var request = new BatchRenameRequest { Entries = [a, b], Rules = [new() { Type = BatchRenameRuleType.AddPrefix, PrefixText = "new-" }] };
        var plan = await rename.GeneratePreviewAsync(request); File.AppendAllText(b.FullPath, "changed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => rename.ExecuteAsync(plan));
        Assert.True(File.Exists(a.FullPath)); Assert.False(File.Exists(Path.Combine(env.Root, "new-a.txt")));
        plan = await rename.GeneratePreviewAsync(request); await env.File("new-b.txt", "occupied");
        await Assert.ThrowsAsync<InvalidOperationException>(() => rename.ExecuteAsync(plan));
        Assert.Equal("occupied", File.ReadAllText(Path.Combine(env.Root, "new-b.txt")));
        Assert.True(File.Exists(a.FullPath));
    }

    [Fact]
    public async Task FailedCycleRestoresOriginalsAndCanRetryTheAcceptedTargets()
    {
        using var env = new RenameFixture(); var a = await env.File("a.txt", "A"); var b = await env.File("b.txt", "B");
        var rename = new BatchRenameService(env.Files);
        var plan = await rename.GeneratePreviewAsync(new() { Entries = [a, b], ManualNames = new Dictionary<string, string> { [a.FullPath] = "b.txt", [b.FullPath] = "a.txt" } });
        env.Files.FailOnceOnName = "b.txt";
        var failed = await rename.ExecuteAsync(plan); Assert.Equal(2, failed.FailedCount); Assert.Empty(failed.SuccessfulItems);
        Assert.Equal("A", File.ReadAllText(a.FullPath)); Assert.Equal("B", File.ReadAllText(b.FullPath));
        Assert.Equal(2, (await rename.ExecuteAsync(failed.FailedItems)).SuccessCount);
        Assert.Equal("B", File.ReadAllText(a.FullPath)); Assert.Equal("A", File.ReadAllText(b.FullPath));
    }

    [Fact]
    public async Task StopAtSafeBoundaryKeepsOneUndoBatchAcrossRetry()
    {
        using var env = new RenameFixture(); var a = await env.File("a.txt", "A"); var b = await env.File("b.txt", "B");
        var rename = new BatchRenameService(env.Files); var history = new FileOperationHistoryService(env.Files, batchRename: rename);
        var operation = new BatchRenameOperationService(rename, history, new BackgroundTaskManager(null));
        var plan = await rename.GeneratePreviewAsync(new() { Entries = [a, b], Rules = [new() { Type = BatchRenameRuleType.AddPrefix, PrefixText = "new-" }] });
        using var cts = new CancellationTokenSource(); env.Files.AfterRename = () => cts.Cancel();
        var partial = await operation.ExecuteAsync(plan, cts.Token); Assert.True(partial.WasCancelled); Assert.Equal(1, partial.SuccessCount);
        env.Files.AfterRename = null;
        var remaining = plan.Items.Except(partial.SuccessfulItems).ToList();
        var retry = await operation.ExecuteAsync(remaining, historyBatchId: partial.HistoryBatchId);
        Assert.Equal(partial.HistoryBatchId, retry.HistoryBatchId); Assert.Equal(1, retry.SuccessCount);
        Assert.True(await history.UndoLastAsync()); Assert.False(history.CanUndo);
        Assert.Equal("A", File.ReadAllText(a.FullPath)); Assert.Equal("B", File.ReadAllText(b.FullPath));
    }

    [Fact]
    public async Task MetadataIsCachedAndMissingPhotoDatesRequireAnExplicitFallback()
    {
        using var env = new RenameFixture(); var a = await env.File("a.jpg", "A"); var b = await env.File("b.jpg", "B");
        var metadata = new PhotoMetadata(a.FullPath); var rename = new BatchRenameService(env.Files, metadata: metadata);
        var rule = new BatchRenameRule { Type = BatchRenameRuleType.Template, TemplateText = "{taken:yyyyMMdd}_{n:000}" };
        var request = new BatchRenameRequest { Entries = [a, b], Rules = [rule] };
        var first = await rename.GeneratePreviewAsync(request); Assert.Equal("20250607_001.jpg", first.Items[0].NewName);
        Assert.True(first.Items[1].HasError); Assert.False(first.CanExecute);
        rule.FallbackToModified = true;
        Assert.True((await rename.GeneratePreviewAsync(request)).CanExecute); Assert.Equal(2, metadata.Reads);
        request.Options.Sort = RenameSort.PhotoTaken;
        Assert.False((await rename.GeneratePreviewAsync(request)).CanExecute);
        request.Options.PhotoSortFallbackToModified = true;
        Assert.True((await rename.GeneratePreviewAsync(request)).CanExecute);
    }

    [AvaloniaFact]
    public void TenThousandPreviewRowsAreVirtualizedAndNarrowLayoutKeepsActionsVisible()
    {
        using var theme = new FastListTestTheme();
        var dialog = new BatchRenameDialog(); var list = dialog.FindControl<ListBox>("PreviewList")!;
        var rows = Compute(new BatchRenameRequest { Entries = Enumerable.Range(0, 10000).Select(i => Entry($"/fixtures/照片_{i}.jpg")).ToArray(),
            Rules = [new() { Type = BatchRenameRuleType.Sequence }] }).Select(i => new BatchRenamePreviewRow(i)).ToArray();
        list.ItemsSource = rows; dialog.Show(); Dispatcher.UIThread.RunJobs();
        Assert.InRange(list.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 100);
        list.ScrollIntoView(rows[^1]); dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.NotNull(list.ContainerFromItem(rows[^1]));
        dialog.Width = 800; dialog.Height = 500; dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, dialog.FindControl<Grid>("BodyGrid")!.ColumnDefinitions.Count);
        Assert.True(dialog.FindControl<Button>("ApplyButton")!.Bounds.Width > 0); dialog.Close();
    }

    [Fact]
    public async Task TenThousandRealFilesPreviewWithoutBlockingAndCanBeCancelled()
    {
        using var env = new RenameFixture();
        var entries = new List<FileSystemEntry>();
        for (var i = 0; i < 10000; i++) entries.Add(await env.File($"照片_{i}.jpg", "X"));
        var service = new BatchRenameService(env.Files);
        var request = new BatchRenameRequest { Entries = entries, Rules = [new() { Type = BatchRenameRuleType.Template, TemplateText = "照片_{n:00000}" }],
            Options = new() { Sort = RenameSort.Name } };
        var watch = Stopwatch.StartNew();
        var task = service.GeneratePreviewAsync(request, TestContext.Current.CancellationToken);
        Assert.False(task.IsCompleted);
        var plan = await task;
        output.WriteLine($"10000 real-file preview: {watch.Elapsed.TotalMilliseconds:F0} ms");
        Assert.Equal(10000, plan.Items.Count); Assert.True(plan.CanExecute);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GeneratePreviewAsync(request, cancelled.Token));
    }

    [Fact]
    public async Task IndexSyncFailuresDoNotReclassifySuccessfulRenamesAsRetryableFailures()
    {
        using var env = new RenameFixture(); var a = await env.File("a.txt", "A");
        var service = new BatchRenameService(env.Files, fileIndexWriter: new RenameIndex { Fail = true });
        var plan = await service.GeneratePreviewAsync(new() { Entries = [a], Rules = [new() { Type = BatchRenameRuleType.AddPrefix, PrefixText = "new-" }] });
        var result = await service.ExecuteAsync(plan);
        Assert.Equal(1, result.SuccessCount); Assert.Equal(0, result.FailedCount); Assert.Empty(result.FailedItems);
        Assert.Contains("信息同步失败", Assert.Single(result.Warnings));
        Assert.Equal("A", File.ReadAllText(Path.Combine(env.Root, "new-a.txt")));
    }

    [Fact]
    public async Task IndexRecordsUseTemporaryLogicalPathsDuringNameExchange()
    {
        using var env = new RenameFixture(); var a = await env.File("a.txt", "A"); var b = await env.File("b.txt", "B");
        var index = new RenameIndex(); index.Records[a.FullPath] = "A tags"; index.Records[b.FullPath] = "B tags";
        var service = new BatchRenameService(env.Files, fileIndexWriter: index);
        var plan = await service.GeneratePreviewAsync(new() { Entries = [a, b], ManualNames = new Dictionary<string, string> { [a.FullPath] = "b.txt", [b.FullPath] = "a.txt" } });
        using var cts = new CancellationTokenSource(); env.Files.AfterRename = () => cts.Cancel();
        var result = await service.ExecuteAsync(plan, cancellationToken: cts.Token);
        Assert.Equal(2, result.SuccessCount); Assert.True(result.WasCancelled); Assert.Empty(result.Warnings);
        Assert.Equal("B tags", index.Records[a.FullPath]); Assert.Equal("A tags", index.Records[b.FullPath]);
        Assert.Equal(2, Directory.GetFiles(env.Root).Length);
    }

    [AvaloniaFact]
    public async Task RuleDragPresetRestoreAndCompletionStatusUseActualDialogControls()
    {
        using var env = new RenameFixture(); var a = await env.File("a.txt", "A"); var b = await env.File("b.txt", "B");
        using var theme = new FastListTestTheme();
        var service = new BatchRenameService(env.Files); var history = new FileOperationHistoryService(env.Files, batchRename: service);
        var operation = new BatchRenameOperationService(service, history, new BackgroundTaskManager(null));
        var settings = new PluginTestEnvironment.MemorySettings();
        var saved = new BatchRenamePreset { Name = "saved", Rules = [new() { Type = BatchRenameRuleType.Template, TemplateText = "项目_{n:000}" }] };
        settings.Set("batch-rename.presets", JsonSerializer.Serialize(new[] { saved }));
        var dialog = new BatchRenameDialog();
        dialog.Configure(service, operation, settings, history, new() { Entries = [a, b], Rules = [new() { Type = BatchRenameRuleType.AddPrefix, PrefixText = "x_" }, saved.Rules[0]] });
        dialog.Show();
        try
        {
            var apply = dialog.FindControl<Button>("ApplyButton")!;
            await WaitUntil(() => apply.IsEnabled);
            var list = dialog.FindControl<ListBox>("RuleList")!;
            var rules = list.ItemsSource!.Cast<BatchRenameRule>().ToArray();
            var start = ((Control)list.ContainerFromItem(rules[1])!).TranslatePoint(new Point(90, 15), dialog)!.Value;
            var end = ((Control)list.ContainerFromItem(rules[0])!).TranslatePoint(new Point(90, 15), dialog)!.Value;
            dialog.MouseDown(start, MouseButton.Left); dialog.MouseMove(end, RawInputModifiers.LeftMouseButton); dialog.MouseUp(end, MouseButton.Left);
            await WaitUntil(() => apply.IsEnabled);
            Assert.Equal(BatchRenameRuleType.Template, list.ItemsSource!.Cast<BatchRenameRule>().First().Type);
            var preview = dialog.FindControl<ListBox>("PreviewList")!;
            Assert.Equal("x_项目_001.txt", preview.ItemsSource!.Cast<BatchRenamePreviewRow>().First().NewName);
            var presets = dialog.FindControl<ComboBox>("PresetCombo")!;
            presets.SelectedItem = presets.ItemsSource!.Cast<BatchRenamePreset>().Single(p => p.Name == "saved");
            await WaitUntil(() => apply.IsEnabled);
            Assert.Equal(2, preview.ItemCount); Assert.Equal("项目_001.txt", preview.ItemsSource!.Cast<BatchRenamePreviewRow>().First().NewName);
            var number = dialog.FindControl<StackPanel>("RuleEditor")!.GetVisualDescendants().OfType<NumericUpDown>()
                .Single(input => AutomationProperties.GetName(input) == "起始编号");
            number.Text = "invalid"; Dispatcher.UIThread.RunJobs();
            Assert.False(apply.IsEnabled); Assert.Contains("整数", dialog.FindControl<Button>("StatusButton")!.Content!.ToString());
            number.Text = "10"; await WaitUntil(() => apply.IsEnabled);
            Assert.Equal("项目_010.txt", preview.ItemsSource!.Cast<BatchRenamePreviewRow>().First().NewName);
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => dialog.FindControl<Button>("UndoButton")!.IsVisible);
            await Task.Delay(100, TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs();
            Assert.Equal("成功 2 项", dialog.FindControl<Button>("StatusButton")!.Content);
            dialog.FindControl<Button>("UndoButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => !dialog.FindControl<Button>("UndoButton")!.IsVisible);
            Assert.True(File.Exists(a.FullPath)); Assert.True(File.Exists(b.FullPath));
        }
        finally { dialog.Close(); }
    }
    [Fact]
    public async Task FolderExchangeAndUndoMigrateDescendantIndexTagsAndPins()
    {
        using var env = new RenameFixture();
        var aPhoto = await env.File("a/sub/photo.jpg", "A"); var bPhoto = await env.File("b/sub/photo.jpg", "B");
        var neighbor = await env.File("a-other/photo.jpg", "neighbor");
        var aPath = Path.Combine(env.Root, "a"); var bPath = Path.Combine(env.Root, "b");
        var a = (await env.Files.GetEntryAsync(aPath))!; var b = (await env.Files.GetEntryAsync(bPath))!;
        var factory = new DatabaseConnectionFactory(Path.Combine(env.Root, "index.db"));
        using var index = new SqliteFileIndex(Path.Combine(env.Root, "index.db"), factory);
        using var tags = new FileTagService(factory); using var ai = new AiTagService(factory); using var pins = new PinnedFolderService(factory);
        await index.AddEntryAsync(a); await index.AddEntryAsync(b); await index.AddEntryAsync(neighbor);
        await index.UpdateDirectoryAsync(Path.GetDirectoryName(aPhoto.FullPath)!, [aPhoto], TestContext.Current.CancellationToken);
        await index.UpdateDirectoryAsync(Path.GetDirectoryName(bPhoto.FullPath)!, [bPhoto], TestContext.Current.CancellationToken);
        await tags.ReplaceFileTagsAsync(aPhoto.FullPath, ["标签A"], TestContext.Current.CancellationToken);
        await tags.ReplaceFileTagsAsync(bPhoto.FullPath, ["标签B"], TestContext.Current.CancellationToken);
        await ai.SaveAnalysisResultAsync(aPhoto.FullPath, aPhoto.LastModified.Ticks, new() { RecognizedTexts = [new() { Text = "A", Confidence = 1 }] }, TestContext.Current.CancellationToken);
        await ai.SaveAnalysisResultAsync(bPhoto.FullPath, bPhoto.LastModified.Ticks, new() { RecognizedTexts = [new() { Text = "B", Confidence = 1 }] }, TestContext.Current.CancellationToken);
        await pins.PinAsync(aPath, "a"); await pins.PinAsync(Path.GetDirectoryName(aPhoto.FullPath)!, "A子目录");
        var rename = new BatchRenameService(env.Files, index, ai, pins, fileTagService: tags);
        var history = new FileOperationHistoryService(env.Files, batchRename: rename);
        var plan = await rename.GeneratePreviewAsync(new() { Entries = [a, b], ManualNames = new Dictionary<string, string> { [aPath] = "b", [bPath] = "a" } });
        var result = await new BatchRenameOperationService(rename, history, new BackgroundTaskManager(null)).ExecuteAsync(plan);
        Assert.Equal(2, result.SuccessCount); Assert.Empty(result.Warnings);
        Assert.Equal("A", File.ReadAllText(bPhoto.FullPath));
        Assert.Equal("标签A", Assert.Single(await tags.GetFileTagsAsync(bPhoto.FullPath, TestContext.Current.CancellationToken)).Name);
        Assert.True(await ai.IsFileAnalyzedAsync(bPhoto.FullPath, aPhoto.LastModified.Ticks));
        Assert.Equal(bPhoto.FullPath, Assert.Single(await index.GetDirectoryContentsAsync(Path.GetDirectoryName(bPhoto.FullPath)!)).FullPath);
        Assert.Contains(await pins.GetAllAsync(), pin => pin.FolderPath == Path.Combine(bPath, "sub") && pin.DisplayName == "A子目录");
        Assert.Single(await index.GetDirectoryContentsAsync(Path.GetDirectoryName(neighbor.FullPath)!));
        Assert.True(await history.UndoBatchAsync(result.HistoryBatchId!.Value));
        Assert.Equal("A", File.ReadAllText(aPhoto.FullPath));
        Assert.Equal("标签A", Assert.Single(await tags.GetFileTagsAsync(aPhoto.FullPath, TestContext.Current.CancellationToken)).Name);
        Assert.Contains(await pins.GetAllAsync(), pin => pin.FolderPath == Path.Combine(aPath, "sub") && pin.DisplayName == "A子目录");
    }

    [AvaloniaFact]
    public void DifferenceRunsHighlightRemovedPrefixesAndKeepJoinedEmojiTogether()
    {
        var old = new RenameDiffText { Before = "photo.JPG", After = "DSC_photo.JPG" };
        Assert.Equal("DSC_", old.Inlines!.OfType<Run>().ElementAt(1).Text);
        var emoji = new RenameDiffText { Before = "👨‍👩‍👧‍👦报告.txt", After = "👨‍👩‍👧报告.txt" };
        var runs = emoji.Inlines!.OfType<Run>().ToArray();
        Assert.Equal("👨‍👩‍👧", runs[1].Text);
        Assert.Equal(emoji.After, string.Concat(runs.Select(r => r.Text)));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompactSpinnersAndDisclosureKeepTheirAppearanceAndNativeInput(bool dark)
    {
        using var env = new RenameFixture(); var entry = await env.File("DSC_001.JPG", "A");
        using var theme = new FastListTestTheme();
        var components = (Avalonia.Styling.Styles)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml"));
        Application.Current!.Styles.Add(components);
        var service = new BatchRenameService(env.Files); var history = new FileOperationHistoryService(env.Files, batchRename: service);
        var dialog = new BatchRenameDialog { RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light };
        dialog.Configure(service, new(service, history, new BackgroundTaskManager(null)), null, history,
            new() { Entries = [entry], Rules = [new() { Type = BatchRenameRuleType.AddPrefix, PrefixText = "new-" }, new() { Type = BatchRenameRuleType.Sequence }, new() { Type = BatchRenameRuleType.CaseConversion, CaseMode = CaseConversionMode.Lowercase }] });
        dialog.Show();
        try
        {
            var apply = dialog.FindControl<Button>("ApplyButton")!; await WaitUntil(() => apply.IsEnabled);
            var rules = dialog.FindControl<ListBox>("RuleList")!; rules.SelectedIndex = 1; dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var number = dialog.FindControl<StackPanel>("RuleEditor")!.GetVisualDescendants().OfType<NumericUpDown>()
                .Single(input => AutomationProperties.GetName(input) == "起始编号");
            var editor = number.GetVisualDescendants().OfType<TextBox>().Single(); editor.Focus();
            Assert.Equal(Avalonia.Media.TextAlignment.Center, editor.TextAlignment);
            var surface = number.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "NumericSurface");
            var background = surface.Background; var bounds = surface.Bounds; var radius = surface.CornerRadius;
            var increase = number.GetVisualDescendants().OfType<RepeatButton>().Single(button => button.Name == "PART_IncreaseButton");
            var point = increase.TranslatePoint(new Point(increase.Bounds.Width / 2, increase.Bounds.Height / 2), dialog)!.Value;
            dialog.MouseMove(point); dialog.MouseDown(point, MouseButton.Left); dialog.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, number.Value); Assert.Equal(background, surface.Background); Assert.Equal(bounds, surface.Bounds); Assert.Equal(radius, surface.CornerRadius);
            editor.Focus(); dialog.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null); Dispatcher.UIThread.RunJobs(); Assert.Equal(3, number.Value);
            await WaitUntil(() => apply.IsEnabled);
            var preview = dialog.FindControl<ListBox>("PreviewList")!; var row = preview.ItemsSource!.Cast<BatchRenamePreviewRow>().Single();
            var container = (Control)preview.ContainerFromItem(row)!;
            var expander = container.GetVisualDescendants().OfType<Expander>().Single();
            var toggle = expander.GetVisualDescendants().OfType<ToggleButton>().Single(button => button.Name == "ExpanderHeader");
            var glyph = expander.GetVisualDescendants().OfType<PathIcon>().Single(icon => icon.Name == "DisclosureIcon");
            var collapsed = glyph.Data;
            var disclosure = toggle.TranslatePoint(new Point(14, 14), dialog)!.Value;
            dialog.MouseDown(disclosure, MouseButton.Left); dialog.MouseUp(disclosure, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Assert.True(row.IsExpanded); Assert.Equal(3, row.Steps.Count); Assert.NotSame(collapsed, glyph.Data);
            Assert.Equal(row.Item.StepNames[1], row.Steps[1].After); Assert.Equal(row.Steps[0].After, row.Steps[1].Before);
            dialog.FindControl<ComboBox>("StepCombo")!.SelectedIndex = 2;
            Assert.Equal(row.Item.StepNames[1], row.DisplayedName); Assert.NotEqual(row.NewName, row.DisplayedName);
            toggle.Focus(); dialog.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " "); dialog.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " "); Dispatcher.UIThread.RunJobs();
            Assert.False(row.IsExpanded); Assert.Same(collapsed, glyph.Data);
        }
        finally { dialog.Close(); Application.Current.Styles.Remove(components); }
    }

    [AvaloniaFact]
    public async Task TemplateParametersFollowTheFieldsActuallyUsedWithoutLosingDefaults()
    {
        using var env = new RenameFixture(); var entry = await env.File("DSC_001.JPG", "A");
        using var theme = new FastListTestTheme();
        var service = new BatchRenameService(env.Files); var history = new FileOperationHistoryService(env.Files, batchRename: service);
        var dialog = new BatchRenameDialog();
        dialog.Configure(service, new(service, history, new BackgroundTaskManager(null)), null, history,
            new() { Entries = [entry], Rules = [new() { Type = BatchRenameRuleType.Template, TemplateText = "照片_{taken:yyyyMMdd}_{n:000}" }] });
        dialog.Show();
        try
        {
            await WaitUntil(() => dialog.FindControl<Button>("StatusButton")!.Content!.ToString()!.Contains("问题"));
            var editor = dialog.FindControl<StackPanel>("RuleEditor")!;
            var numbers = editor.GetVisualDescendants().OfType<NumericUpDown>().Where(input => input.IsEffectivelyVisible).ToArray();
            Assert.Equal(2, numbers.Length); Assert.DoesNotContain(numbers, input => AutomationProperties.GetName(input) == "位数");
            Assert.DoesNotContain(editor.GetVisualDescendants().OfType<ComboBox>(), input => input.IsEffectivelyVisible && AutomationProperties.GetName(input) == "日期来源");
            var rule = (BatchRenameRule)dialog.FindControl<ListBox>("RuleList")!.SelectedItem!;
            rule.TemplateText = "项目_{n}"; Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, editor.GetVisualDescendants().OfType<NumericUpDown>().Count(input => input.IsEffectivelyVisible));
            rule.TemplateText = "项目"; Dispatcher.UIThread.RunJobs();
            Assert.Empty(editor.GetVisualDescendants().OfType<NumericUpDown>().Where(input => input.IsEffectivelyVisible));
            Assert.True(rule.IsEnabled); Assert.True(rule.TrimWhitespace); Assert.True(rule.CollapseWhitespace); Assert.False(rule.FallbackToModified);
        }
        finally { dialog.Close(); }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        { await Task.Delay(20, TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs(); }
        Assert.True(condition());
    }
    private sealed class RenameIndex : IFileIndexWriter
    {
        public bool Fail { get; init; }
        public Dictionary<string, string> Records { get; } = new();
        public Task RenameEntryAsync(string oldPath, string newPath, string newName)
        {
            if (Fail) throw new IOException("injected sync failure");
            if (Records.Remove(oldPath, out var data)) Records[newPath] = data;
            return Task.CompletedTask;
        }
        public Task UpdateDirectoryAsync(string path, IReadOnlyList<FileSystemEntry> entries, CancellationToken token = default) => Task.CompletedTask;
        public Task InvalidateDirectoriesAsync(IEnumerable<string> paths) => Task.CompletedTask;
        public Task RemoveEntryAsync(string path) => Task.CompletedTask;
        public Task AddEntryAsync(FileSystemEntry entry) => Task.CompletedTask;
    }

    private sealed class PhotoMetadata(string knownPath) : IMetadataService
    {
        private int _reads;
        public int Reads => _reads;
        public Task<FileMetadata> GetMetadataAsync(string path)
        {
            Interlocked.Increment(ref _reads);
            return Task.FromResult(new FileMetadata { ImageInfo = new() { PhotoTakenDate = path == knownPath ? new DateTime(2025, 6, 7) : null } });
        }
    }
    private sealed class RenameFixture : IDisposable
    {
        public string Root { get; } = Path.Combine("/private/tmp", "fkfinder-rename-" + Guid.NewGuid().ToString("N"));
        public ControlledFiles Files { get; } = new();
        public RenameFixture() => Directory.CreateDirectory(Root);
        public async Task<FileSystemEntry> File(string name, string text)
        {
            var path = Path.Combine(Root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await System.IO.File.WriteAllTextAsync(path, text); return (await Files.GetEntryAsync(path))!;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
    private sealed class ControlledFiles : MacFileService, IFileService
    {
        public string? FailOnceOnName { get; set; }
        public Action? AfterRename { get; set; }
        async Task IFileService.RenameAsync(string path, string name)
        {
            if (name == FailOnceOnName) { FailOnceOnName = null; throw new UnauthorizedAccessException("injected permission failure"); }
            await base.RenameAsync(path, name); AfterRename?.Invoke();
        }
    }
}
