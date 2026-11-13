#if DEBUG
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Search;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using MacExplorer.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.Testing;

internal static class FeatureUpgradeSmoke
{
    internal static async Task RunAsync(MainWindow owner)
    {
        var output = Path.Combine(RuntimePaths.TestRoot!, "feature-upgrade"); var screenshots = Path.Combine(output, "screenshots");
        Directory.CreateDirectory(screenshots);
        var checks = new List<string>(); string? error = null; Process? serverProcess = null; Process? displayLease = null;
        var keepOpen = Environment.GetEnvironmentVariable("MACEXPLORER_FEATURE_UPGRADE_KEEP_OPEN") == "1";
        var services = App.Services; var model = (MainWindowViewModel)owner.DataContext!; var vm = model.FileList;
        var edits = services.GetRequiredService<IRemoteFileEditService>(); var connections = services.GetRequiredService<IRemoteConnectionService>();
        try
        {
            var power = new ProcessStartInfo("/usr/bin/caffeinate") { UseShellExecute = false };
            foreach (var argument in new[] { "-d", "-i", "-u", "-w", Environment.ProcessId.ToString() }) power.ArgumentList.Add(argument);
            displayLease = Process.Start(power);
            FolderTabRestoreSmoke.ActivateApplication(); owner.ShowAndActivate(); owner.Width = 1280; owner.Height = 800;
            owner.RequestedThemeVariant = ThemeVariant.Light;
            var settings = services.GetRequiredService<ISettingsService>(); settings.Set("AiAnalysisEnabled", false);
            vm.IsAiAnalysisEnabled = false; services.GetRequiredService<ILocalizationService>().SetLanguage("zh-CN");
            var samples = Directory.CreateDirectory(Path.Combine(RuntimePaths.TestRoot!, "Documents", "功能验收样本")).FullName;
            var docs = Directory.CreateDirectory(Path.Combine(samples, "文档")).FullName;
            var note = Path.Combine(docs, "项目计划.md"); await File.WriteAllTextAsync(note, "# 项目计划\n\n目标与里程碑\nLaunch review 4826\n本周完成搜索与同步验收。\n");
            var word = Path.Combine(docs, "需求说明.docx");
            using (var document = WordprocessingDocument.Create(word, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
                document.AddMainDocumentPart().Document = new Document(new Body(new Paragraph(new Run(new Text("需求说明"))), new Paragraph(new Run(new Text("Launch review 4826")))));
            var pdf = Path.Combine(docs, "验收清单.pdf"); MakePdf(pdf);
            var index = services.GetRequiredService<DocumentIndexService>();
            foreach (var path in new[] { note, word, pdf }) await index.IndexFileAsync(path);
            await vm.NavigateToAsync(docs);
            await vm.SearchAsync("Launch review 4826");
            Check(vm.Entries.Count == 3, "正文搜索同时找到 Markdown、DOCX 和 PDF");
            Check(!vm.IsInfoPanelVisible, "正文搜索不自动展开右侧信息面板");
            Check(owner.GetVisualDescendants().OfType<Avalonia.Controls.Control>().All(control => control.Name != "AdvancedSearchButton"), "原搜索框已恢复，无高级搜索入口");
            vm.SelectEntry(vm.Entries.Single(e => e.FullPath == note)); await Capture(owner, "01-search-restored");
            var match = new ContentMatchDialog(vm.Entries.Single(e => e.FullPath == note), "Launch review 4826"); var matchTask = match.ShowDialog(owner);
            await Capture(match, "08-text-hit-location"); match.Close(); await matchTask;
            var pdfMatch = new ContentMatchDialog(vm.Entries.Single(e => e.FullPath == pdf), "Launch review 4826"); var pdfTask = pdfMatch.ShowDialog(owner);
            await Capture(pdfMatch, "08-pdf-hit-location", 1200); pdfMatch.Close(); await pdfTask;
            var office = new ContentMatchDialog(vm.Entries.Single(e => e.FullPath == word), "Launch review 4826"); var officeTask = office.ShowDialog(owner);
            await Capture(office, "08-office-original-preview", 1200); office.Close(); await officeTask;
            var largeFolder = Directory.CreateDirectory(Path.Combine(samples, "大文本")).FullName;
            var largeFile = Path.Combine(largeFolder, "大文件.txt");
            await using (var stream = File.Create(largeFile))
            {
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("Large text needle 4826\n"));
                var padding = new byte[64 * 1024]; Array.Fill(padding, (byte)'x');
                for (var i = 0; i < 80; i++) await stream.WriteAsync(padding);
            }
            var largeEntry = new FileSystemEntry { FullPath = largeFile, Name = Path.GetFileName(largeFile),
                SearchMatches = [new("Large text needle 4826", new("line", 1))] };
            var largeDialog = new ContentMatchDialog(largeEntry, "needle"); var largeTask = largeDialog.ShowDialog(owner);
            await Wait(() => largeDialog.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Any(editor => editor.SelectedText == "needle"));
            await Capture(largeDialog, "08-large-text-excerpt"); largeDialog.Close(); await largeTask;
            Check(true, "超出完整文本预览上限仍显示并高亮命中片段");

            await vm.ExitSearchAsync(); await vm.NavigateToAsync(docs);
            var tags = services.GetRequiredService<IFileTagService>(); var tag = await tags.CreateTagAsync("待审核"); await tags.SetTagAsync([note, word], tag, true);
            vm.IsInfoPanelVisible = true; vm.SelectEntry(vm.Entries.Single(e => e.FullPath == note));
            await Capture(owner, "04-info-panel-single", 1000);
            vm.SetSelection(vm.Entries.Where(e => !e.IsDirectory).ToArray());
            await Capture(owner, "04-info-panel-restored", 1000);
            var infoPanel = owner.GetVisualDescendants().OfType<InfoPanelView>().First(view => view.DataContext == vm);
            Check(!infoPanel.FindControl<StackPanel>("DetailsPanel")!.IsVisible
                && infoPanel.FindControl<StackPanel>("MultiSelectionPanel") == null, "多选时恢复原有选择提示，不显示汇总和批量标签面板");

            var browseDirectory = Directory.CreateDirectory(Path.Combine(samples, "浏览索引")).FullName;
            var browseFile = Path.Combine(browseDirectory, "新浏览文档.txt"); await File.WriteAllTextAsync(browseFile, "browse triggered content 6291");
            var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnIndexed(string path) { if (path == browseFile) indexed.TrySetResult(); }
            index.Changed += OnIndexed;
            try
            {
                vm.IsAiAnalysisEnabled = true; await vm.NavigateToAsync(browseDirectory);
                await indexed.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Check((await services.GetRequiredService<SearchCatalog>().SearchResourcesAsync(browseDirectory,
                    new() { Query = "browse triggered content", Source = "content" }, new())).Items.Count == 1, "浏览目录自动建立正文索引");
            }
            finally { index.Changed -= OnIndexed; vm.IsAiAnalysisEnabled = false; }

            var pages = Directory.CreateDirectory(Path.Combine(samples, "搜索样本")).FullName;
            for (var i = 0; i < 125; i++) await File.WriteAllTextAsync(Path.Combine(pages, $"report-{i:000}.txt"), "pagination fixture");
            services.GetRequiredService<SearchIndexer>().Refresh(pages);
            await vm.NavigateToAsync(pages); await vm.SearchAsync("report");
            Check(vm.Entries.Count == 125, $"原搜索一次显示超过 100 项，无新增分页（实际 {vm.Entries.Count} 项）");

            var sourceDir = Directory.CreateDirectory(Path.Combine(samples, "复制来源")).FullName;
            var targetDir = Directory.CreateDirectory(Path.Combine(samples, "复制目标")).FullName;
            var source = Path.Combine(sourceDir, "项目说明.txt"); var target = Path.Combine(targetDir, "项目说明.txt");
            await File.WriteAllTextAsync(source, "新版本 · 2026-11-12"); await File.WriteAllTextAsync(target, "旧版本 · 保留用于撤销");
            await vm.NavigateToAsync(targetDir);
            var files = services.GetRequiredService<IFileService>(); var copy = files.CopyWithProgressAsync(source, targetDir);
            await Wait(() => owner.OwnedWindows.OfType<FileConflictDialog>().Any());
            var conflict = owner.OwnedWindows.OfType<FileConflictDialog>().Single(); await Capture(conflict, "02-file-conflict-light");
            conflict.Close(new FileConflictDecision(FileConflictAction.Replace)); await copy;
            Check(await File.ReadAllTextAsync(target) == "新版本 · 2026-11-12", "替换发布新版本");
            Check(await services.GetRequiredService<IFileOperationHistoryService>().UndoLastAsync(), "替换可撤销");
            Check(await File.ReadAllTextAsync(target) == "旧版本 · 保留用于撤销", "撤销恢复旧内容");
            var cancelledCopy = files.CopyWithProgressAsync(source, targetDir);
            await Wait(() => owner.OwnedWindows.OfType<FileConflictDialog>().Any());
            var waitingTask = services.GetRequiredService<IBackgroundTaskManager>().Tasks.Last(t => t.IsActive);
            services.GetRequiredService<IBackgroundTaskManager>().CancelTask(waitingTask.Id);
            try { await cancelledCopy; throw new InvalidOperationException("Copy should be cancelled."); }
            catch (OperationCanceledException) { }
            await Wait(() => !owner.OwnedWindows.OfType<FileConflictDialog>().Any());
            Check(await File.ReadAllTextAsync(target) == "旧版本 · 保留用于撤销", "取消任务同步关闭冲突询问并保留原目标");
            await FeatureUpgradeVolumeSmoke.RunAsync(Check);

            var folderA = Directory.CreateDirectory(Path.Combine(samples, "设计资料")).FullName;
            var folderB = Directory.CreateDirectory(Path.Combine(samples, "开发项目")).FullName;
            foreach (var folder in new[] { folderA, folderB }) { await File.WriteAllTextAsync(Path.Combine(folder, "合同草稿.md"), "test"); await File.WriteAllTextAsync(Path.Combine(folder, "项目计划.txt"), "test data"); }
            await vm.NavigateToAsync(folderA); var view = vm.ViewMode; var grouping = vm.GroupField;
            vm.SetSort(SortField.Size, false); vm.FileNameFilter = "合同"; vm.IsInfoPanelVisible = true;
            await vm.NavigateToAsync(folderB); vm.SetSort(SortField.Modified, true); vm.IsInfoPanelVisible = false;
            await vm.NavigateToAsync(folderA);
            Check(vm.SortField == SortField.Size && !vm.SortAscending && vm.FileNameFilter == "合同" && vm.IsInfoPanelVisible, "目录偏好独立恢复排序、筛选和面板");
            Check(vm.ViewMode == view && vm.GroupField == grouping, "默认视图与分组保持现状");
            await owner.OpenNewTabAsync(folderB); var group = model.CreateGroup(model.Tabs.Take(2).ToArray()); group.Name = "验收工作组";
            model.ActivateGroup(group); await owner.SetPaneLayoutAsync(PaneLayout.TwoColumns);
            var session = owner.CaptureFolderTabs();
            var preset = TabGroupPreset.Capture("验收工作组", model.Tabs.Select(t => t.FileList.CurrentPath), model.CaptureGroups().Single(g => g.Id == group.Id),
                preferences: model.Tabs.Select((tab, i) => (tab, i)).ToDictionary(p => p.i, p => p.tab.FileList.CaptureDirectoryPreferences()))!;
            TabGroupPresets.Save(settings, preset); await Capture(owner, "05-directory-preferences-workspace");
            await owner.CloseGroupAsync(group); await owner.OpenGroupPresetAsync(preset);
            Check(model.Tabs.Any(t => t.FileList.CurrentPath == folderA && t.FileList.SortField == SortField.Size && t.FileList.FileNameFilter == "合同"), "工作组预设恢复各栏偏好");
            vm = model.FileList;

            var repository = Directory.CreateDirectory(Path.Combine(samples, "Git 项目")).FullName;
            await Git(repository, "init", "-q"); var tracked = Path.Combine(repository, "release.md"); await File.WriteAllTextAsync(tracked, "# Release\nOriginal plan\n");
            await Git(repository, "add", "release.md"); await Git(repository, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-qm", "fixture");
            await File.WriteAllTextAsync(tracked, "# Release\nSearch and sync improvements\n"); await Git(repository, "add", "release.md");
            await File.AppendAllTextAsync(tracked, "Final review\n");
            var git = services.GetRequiredService<IGitStatusService>(); var diff = await git.GetDiffAsync(tracked, false);
            Check(diff.Contains("Final review"), "读取未暂存 Git 差异");
            var gitDialog = new GitDiffDialog(diff, false); var gitTask = gitDialog.ShowDialog(owner); await Capture(gitDialog, "09-git-diff-light"); gitDialog.Close(); await gitTask;
            Check((await git.GetDiffAsync(tracked, true)).Contains("Search and sync"), "读取已暂存 Git 差异");

            var portraits = Directory.CreateDirectory(Path.Combine(samples, "人物照片")).FullName;
            var alice = Path.Combine(portraits, "测试人物-甲.png"); var bob = Path.Combine(portraits, "测试人物-乙.png"); MakePortrait(alice, false); MakePortrait(bob, true);
            var ai = services.GetRequiredService<IAiTagService>();
            var observation = new ImageAnalysisResult { Faces = [new DetectedFace { BoundingBoxX = .15f, BoundingBoxY = .15f, BoundingBoxW = .7f, BoundingBoxH = .7f, FeaturePrint = new byte[16] }] };
            foreach (var path in new[] { alice, bob }) await ai.SaveAnalysisResultAsync(path, File.GetLastWriteTime(path).Ticks, observation);
            await ai.RunClusteringAsync(); var cluster = (await ai.GetAllFaceClustersAsync()).First(c => c.FaceCount > 0);
            await ai.SetClusterNameAsync(cluster.Id, "测试人物");
            await owner.SetPaneLayoutAsync(PaneLayout.Single); vm = model.FileList;
            await vm.NavigateToAsync(VirtualPath.AiPeople);
            vm.IsInfoPanelVisible = false;
            await Capture(owner, "10-people-restored", 1200);
            Check(vm.FaceClusters.Any(person => person.Id == cluster.Id && person.DisplayName == "测试人物"), "人物页面保留原有自动分类与重命名");
            Check(owner.GetVisualDescendants().OfType<Avalonia.Controls.Control>().All(control => control.Name != "PeopleButton"), "已移除新增的人物纠错管理入口");

            var python = Environment.GetEnvironmentVariable("MACEXPLORER_SFTP_TEST_PYTHON")!;
            var fixture = Path.Combine(Environment.GetEnvironmentVariable("MACEXPLORER_FEATURE_SOURCE")!, "Tests/MacExplorer.Tests/TestData/sftp-loopback.py");
            var remoteRoot = Directory.CreateDirectory(Path.Combine(RuntimePaths.TestRoot!, "sftp-fixture")).FullName;
            var start = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { fixture, remoteRoot, "ui-fixture", "0" }) start.ArgumentList.Add(arg);
            serverProcess = Process.Start(start)!; var port = int.Parse((await serverProcess.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)))!);
            connections.ConfirmHostKeyAsync = (_, _) => Task.FromResult(true);
            var server = new RemoteServerInfo { Name = "本地 SFTP 验收", Host = "127.0.0.1", Port = port, Username = "fixture", Password = "fixture-secret" };
            var client = await connections.ConnectAsync(server); connections.SaveServer(server); client.WriteAllText("/配置说明.txt", "server initial");
            await vm.ConnectToServerAsync(server); var local = await edits.DownloadForEditAsync("/配置说明.txt", server.Id); edits.WatchForChanges(local, "/配置说明.txt", server.Id);
            await File.WriteAllTextAsync(local, "latest local version"); await Wait(() => edits.Sessions.Any(s => s.State == RemoteEditState.Synced) && client.ReadAllText("/配置说明.txt") == "latest local version");
            Check(client.ReadAllText("/配置说明.txt") == "latest local version", "远程编辑自动回传保存内容");
            client.WriteAllText("/配置说明.txt", "changed by another editor"); await File.WriteAllTextAsync(local, "unsynced local edit"); await Wait(() => edits.Sessions.Any(s => s.State == RemoteEditState.Conflict));
            var remote = new RemoteEditDialog(edits); var remoteTask = remote.ShowDialog(owner); await Capture(remote, "03-remote-edit-conflict");
            remote.RequestedThemeVariant = ThemeVariant.Dark; remote.Width = 430; await Capture(remote, "03-remote-edit-conflict-dark-narrow"); remote.Close(); await remoteTask;
            services.GetRequiredService<ILocalizationService>().SetLanguage("en");
            var remoteEnglish = new RemoteEditDialog(edits); var remoteEnglishTask = remoteEnglish.ShowDialog(owner);
            await Capture(remoteEnglish, "03-remote-edit-conflict-English"); remoteEnglish.Close(); await remoteEnglishTask;
            services.GetRequiredService<ILocalizationService>().SetLanguage("zh-CN");
            Check(client.ReadAllText("/配置说明.txt") == "changed by another editor" && File.ReadAllText(local) == "unsynced local edit", "远程冲突保留双方内容");
            await edits.ResolveConflictAsync(local, true);
            serverProcess.Kill(true); await serverProcess.WaitForExitAsync();
            await File.WriteAllTextAsync(local, "saved while network is unavailable");
            await Wait(() => edits.Sessions.Any(s => s.State == RemoteEditState.Failed));
            var disconnected = new RemoteEditDialog(edits); var disconnectedTask = disconnected.ShowDialog(owner);
            await Capture(disconnected, "03-remote-edit-disconnected");
            disconnected.RequestedThemeVariant = ThemeVariant.Dark; disconnected.Width = 430;
            await Capture(disconnected, "03-remote-edit-disconnected-dark-narrow"); disconnected.Close(); await disconnectedTask;
            services.GetRequiredService<ILocalizationService>().SetLanguage("fr");
            var remoteFrench = new RemoteEditDialog(edits) { Width = 430 }; var remoteFrenchTask = remoteFrench.ShowDialog(owner);
            await Capture(remoteFrench, "03-remote-edit-disconnected-French-narrow"); remoteFrench.Close(); await remoteFrenchTask;
            services.GetRequiredService<ILocalizationService>().SetLanguage("zh-CN");
            Check(File.ReadAllText(local) == "saved while network is unavailable", "SFTP 断线后失败状态保留编辑副本");
            if (keepOpen)
            {
                serverProcess.Dispose(); start.ArgumentList[3] = port.ToString(); serverProcess = Process.Start(start)!;
                await serverProcess.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
                await edits.ReconnectAsync(local);
            }
            await edits.StopAsync(); connections.DisconnectAll();

            owner.RequestedThemeVariant = ThemeVariant.Dark;
            var darkGit = new GitDiffDialog(diff, false) { RequestedThemeVariant = ThemeVariant.Dark }; darkGit.Width = 780; var darkGitTask = darkGit.ShowDialog(owner); await Capture(darkGit, "09-git-diff-dark"); darkGit.Close(); await darkGitTask;
            await owner.SetPaneLayoutAsync(PaneLayout.Single);
            vm = model.FileList; await vm.NavigateToAsync(docs); vm.IsInfoPanelVisible = false;
            await vm.SearchAsync("Launch review 4826"); vm.SelectEntry(vm.Entries.Single(entry => entry.FullPath == note));
            owner.Width = 900; await Capture(owner, "01-search-restored-dark-narrow");
            services.GetRequiredService<ILocalizationService>().SetLanguage("en");
            owner.Width = 1280; owner.RequestedThemeVariant = ThemeVariant.Light;
            await Capture(owner, "01-search-restored-English");
            services.GetRequiredService<ILocalizationService>().SetLanguage("fr");
            owner.Width = 900; await Capture(owner, "01-search-restored-French-narrow");
            Check(Directory.GetFiles(screenshots, "*.png").Length >= 12, "真实窗口截图覆盖保留功能、还原的搜索界面及深色、窄窗口和多语言");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close();
            await edits.StopAsync(); connections.DisconnectAll();
            if (displayLease is { HasExited: false }) displayLease.Kill(); displayLease?.Dispose();
            if (!keepOpen || error != null)
            { if (serverProcess is { HasExited: false }) serverProcess.Kill(true); serverProcess?.Dispose(); }
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { success = error == null, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Feature upgrade smoke: {output}; success={error == null}");
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            {
                if (keepOpen && error == null)
                {
                    services.GetRequiredService<ILocalizationService>().SetLanguage("zh-CN");
                    owner.RequestedThemeVariant = ThemeVariant.Light;
                    await model.FileList.NavigateToAsync(Path.Combine(RuntimePaths.TestRoot!, "Documents", "功能验收样本"));
                    lifetime.Exit += (_, _) => { if (serverProcess is { HasExited: false }) serverProcess.Kill(true); serverProcess?.Dispose(); };
                }
                else lifetime.Shutdown(error == null ? 0 : 1);
            }
        }
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks.Add(message); }
        async Task Capture(Window window, string name, int delay = 450)
        {
            for (var attempt = 0; ; attempt++)
            {
                await Run("/usr/bin/caffeinate", ["-u", "-t", "1"]);
                window.Activate(); window.UpdateLayout(); await Task.Delay(attempt == 0 ? delay : 500);
                var native = (IMacOSTopLevelPlatformHandle)window.TryGetPlatformHandle()!;
                Console.WriteLine($"Capture {name}: visible={window.IsVisible}; state={window.WindowState}; bounds={window.Bounds}");
                try
                {
                    await Run("/usr/sbin/screencapture", ["-x", "-l", SendInt(native.NSWindow, Selector("windowNumber")).ToString(), Path.Combine(screenshots, name + ".png")]);
                    return;
                }
                catch (IOException) when (attempt < 2) { Console.WriteLine("Waiting for native screenshot surface: " + name); }
            }
        }
    }
    private static async Task Wait(Func<bool> ready)
    { var timer = Stopwatch.StartNew(); while (!ready()) { if (timer.Elapsed > TimeSpan.FromSeconds(35)) throw new TimeoutException("Feature smoke state did not settle."); await Task.Delay(40); } }
    private static Task Git(string directory, params string[] args) => Run("/usr/bin/git", args, directory);
    private static async Task Run(string executable, string[] arguments, string? directory = null)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }; if (directory != null) start.WorkingDirectory = directory;
        if (executable.EndsWith("/git", StringComparison.Ordinal)) { start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null"; start.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; }
        foreach (var argument in arguments) start.ArgumentList.Add(argument); using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException(await error + await output);
    }
    private static void MakePortrait(string path, bool alternate)
    {
        using var bitmap = new SkiaSharp.SKBitmap(320, 320); using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(alternate ? SkiaSharp.SKColor.Parse("#DCE9F5") : SkiaSharp.SKColor.Parse("#F4E4D4"));
        using var paint = new SkiaSharp.SKPaint { IsAntialias = true, Color = SkiaSharp.SKColor.Parse("#F3C9A8") }; canvas.DrawOval(70, 45, 180, 230, paint);
        paint.Color = SkiaSharp.SKColor.Parse(alternate ? "#52423A" : "#302A27"); canvas.DrawOval(67, 37, 186, 85, paint);
        canvas.DrawCircle(125, 150, 9, paint); canvas.DrawCircle(195, 150, 9, paint); paint.Style = SkiaSharp.SKPaintStyle.Stroke; paint.StrokeWidth = 6; canvas.DrawArc(new SkiaSharp.SKRect(130, 175, 190, 220), 0, 180, false, paint);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap); using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100); using var stream = File.Create(path); data.SaveTo(stream);
    }
    private static void MakePdf(string path)
    {
        using var stream = File.Create(path); using var document = SkiaSharp.SKDocument.CreatePdf(stream); using var typeface = SkiaSharp.SKTypeface.FromFamilyName("Helvetica");
        using var font = new SkiaSharp.SKFont(typeface, 22); using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black, IsAntialias = true };
        var canvas = document.BeginPage(612, 792); canvas.DrawText("Acceptance checklist", 50, 80, font, paint); document.EndPage();
        canvas = document.BeginPage(612, 792); canvas.DrawText("Launch review 4826", 50, 80, font, paint); document.EndPage(); document.Close();
    }
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")] private static extern IntPtr Selector(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern nint SendInt(IntPtr target, IntPtr selector);
}
#endif
