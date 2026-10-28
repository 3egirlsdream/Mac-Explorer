#if DEMO_AUTOMATION
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Copilot;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Search;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.DemoRecord;

/// <summary>File mailbox exists only in a dedicated build and a runner-owned temporary profile.</summary>
internal static class DemoAutomation
{
    private static string _root = "";
    private static string _token = "";
    private static bool _stopping;
    private static int _mainActivations;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static IClassicDesktopStyleApplicationLifetime Desktop =>
        (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;

    internal static void ValidateStartup()
    {
        var root = RuntimePaths.TestRoot;
        var token = Environment.GetEnvironmentVariable("MACEXPLORER_DEMO_TOKEN");
        if (root == null || !root.StartsWith("/private/tmp/fkfinder-test.", StringComparison.Ordinal)
            || token == null || token.Length < 32 || File.ReadAllText(Path.Combine(root, ".demo-owner")) != token)
            throw new InvalidOperationException("Demo build cannot start without a runner-owned temporary profile.");
    }

    internal static void Start(MainWindow owner)
    {
        owner.Activated += (_, _) => _mainActivations++;
        _root = RuntimePaths.TestRoot ?? throw new InvalidOperationException("Demo build requires an isolated test root.");
        _token = Environment.GetEnvironmentVariable("MACEXPLORER_DEMO_TOKEN") ?? "";
        if (_token.Length < 32 || File.ReadAllText(Path.Combine(_root, ".demo-owner")) != _token)
            throw new InvalidOperationException("Missing demo runner ownership marker.");
        var mailbox = Path.Combine(_root, ".demo");
        Directory.CreateDirectory(mailbox);
        _ = Task.Run(async () =>
        {
            try
            {
                await WriteAsync(Path.Combine(mailbox, "ready.json"), new
                {
                    pid = Environment.ProcessId, token = _token, root = _root,
                    database = RuntimePaths.DatabasePath, executable = Environment.ProcessPath,
                    version = typeof(App).Assembly.GetName().Version?.ToString()
                });
                while (!_stopping)
                {
                    foreach (var file in Directory.GetFiles(mailbox, "*.request.json").Order())
                    {
                        object response;
                        try
                        {
                            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file));
                            var command = document.RootElement.Clone();
                            if (command.GetProperty("token").GetString() != _token)
                                throw new InvalidOperationException("Invalid demo token.");
                            var result = await Dispatcher.UIThread.InvokeAsync(() => ExecuteAsync(owner, command));
                            response = new { ok = true, data = result };
                        }
                        catch (Exception ex) { response = new { ok = false, error = ex.ToString() }; }
                        await WriteAsync(file.Replace(".request.json", ".response.json"), response);
                        File.Delete(file);
                    }
                    await Task.Delay(30);
                }
            }
            catch (Exception ex) { await WriteAsync(Path.Combine(mailbox, "fatal.json"), new { error = ex.ToString() }); }
        });
    }

    private static async Task WriteAsync(string path, object value)
    {
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(value, Json));
        File.Move(path + ".tmp", path, true);
    }

    private static string Text(JsonElement command, string key, string fallback = "") =>
        command.TryGetProperty(key, out var value) ? value.GetString() ?? fallback : fallback;

    private static string LocalPath(string value)
    {
        var path = Path.GetFullPath(value);
        if (path != _root && !path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Demo paths must stay within the isolated profile.");
        for (var current = new DirectoryInfo(Path.GetDirectoryName(path)!); current != null && current.FullName != _root; current = current.Parent)
            if (current.LinkTarget != null) throw new InvalidOperationException("Demo paths cannot cross symlinks.");
        if (File.Exists(path) && new FileInfo(path).LinkTarget != null)
            throw new InvalidOperationException("Demo files cannot be symbolic links.");
        if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget != null)
            throw new InvalidOperationException("Demo directories cannot be symbolic links.");
        return path;
    }

    private static Window Window(MainWindow main, string kind) => kind == "main" ? main :
        Desktop.Windows.Last(w => w.IsVisible && (w.GetType().Name == kind
            || kind == "delivery" && w is FileDeliveryWindow));

    private static FileListViewModel Pane(MainWindow main, string kind) => kind == "delivery"
        ? (FileListViewModel)((FileDeliveryWindow)Window(main, kind)).FileList.DataContext!
        : ((MainWindowViewModel)main.DataContext!).FileList;

    private static IEnumerable<Control> Controls(Control root)
    {
        yield return root;
        foreach (var control in root.GetVisualDescendants().OfType<Control>()) yield return control;
        foreach (var popup in root.GetVisualDescendants().OfType<Popup>().Where(p => p.IsOpen && p.Child != null))
            foreach (var control in Controls(popup.Child!)) yield return control;
        foreach (var list in root.GetVisualDescendants().OfType<FileListView>())
            if (list.DemoMenu is { IsOpen: true } menu)
                foreach (var control in Controls(menu)) yield return control;
    }

    private static async Task CommitAsync(Window window)
    {
        window.UpdateLayout();
        if (ElementComposition.GetElementVisual(window)?.Compositor is { } compositor)
            await compositor.RequestCommitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<object?> ExecuteAsync(MainWindow main, JsonElement command)
    {
        var op = Text(command, "op");
        var kind = Text(command, "window", "main");
        var pane = Pane(main, kind);
        switch (op)
        {
            case "state":
                return new
                {
                    path = pane.CurrentPath, loading = pane.IsLoading, entries = pane.Entries.Select(e => e.Name).ToArray(),
                    selected = pane.SelectedEntries.Select(e => e.Name).ToArray(), viewMode = pane.ViewMode.ToString(),
                    layout = ((MainWindowViewModel)main.DataContext!).PaneLayout.ToString(),
                    hints = main.IsShortcutHintVisible, mainActive = main.IsActive, mainActivations = _mainActivations,
                    preview = main.DemoPreviewVisible, previewReady = main.DemoPreviewReady, globalSearch = main.DemoGlobalSearchVisible,
                    globalResults = main.DemoGlobalResults,
                    editorText = main.DemoEditorText,
                    menus = main.GetVisualDescendants().OfType<FileListView>().Where(v => v.DemoMenu?.IsOpen == true)
                        .SelectMany(v => Controls(v.DemoMenu!).OfType<MenuItem>()).Select(m => m.Header?.ToString()).ToArray(),
                    windows = Desktop.Windows.Where(w => w.IsVisible).Select(w => new { type = w.GetType().Name, w.Title }).ToArray(),
                    tasks = App.Services.GetRequiredService<IBackgroundTaskManager>().Tasks.Select(t => new { t.Id, t.Label, state = t.State.ToString(), t.Progress, t.ErrorMessage }).ToArray(),
                    pendingImages = main.GetVisualDescendants().OfType<FastFileList>().Where(c => c.IsEffectivelyVisible).Sum(c => c.DemoPendingImages)
                };
            case "setup":
                main.Width = 1200; main.Height = 680;
                main.Position = new PixelPoint(120, 70);
                var tags = App.Services.GetRequiredService<IFileTagService>();
                var tag = await tags.CreateTagAsync("山野精选");
                await tags.SetTagPinnedAsync(tag, true);
                await tags.SetTagAsync([LocalPath(Path.Combine(_root, "Documents/山野计划/摄影作品/高山湖泊.jpg"))], tag, true);
                await main.NavigateToPathAsync(LocalPath(Path.Combine(_root, "Documents/山野计划")));
                await CommitAsync(main);
                return true;
            case "capability":
                var id = Text(command, "id");
                if (!id.StartsWith("ui.", StringComparison.Ordinal) && id != "file.info")
                    throw new InvalidOperationException("Only UI capabilities are exposed by the demo mailbox.");
                var args = command.TryGetProperty("arguments", out var a) ? a : JsonSerializer.SerializeToElement(new { });
                ValidatePaths(args);
                var result = await App.Services.GetRequiredService<IAppCapabilityRegistry>().ExecuteReadAsync(id, args.GetRawText(), pane);
                if (!result.Success) throw new InvalidOperationException(result.Message);
                await CommitAsync(main);
                return result;
            case "navigate":
                await pane.NavigateToAsync(LocalPath(Text(command, "path")));
                await CommitAsync(Window(main, kind));
                return true;
            case "select":
                var names = command.GetProperty("names").EnumerateArray().Select(x => x.GetString()).ToHashSet();
                var selected = pane.Entries.Where(e => names.Contains(e.Name)).ToArray();
                if (selected.Length != names.Count) throw new InvalidOperationException("Selection does not match visible files.");
                pane.SetSelection(selected);
                await CommitAsync(Window(main, kind));
                return true;
            case "target":
                var window = Window(main, kind);
                Control target;
                Rect rect;
                if (command.TryGetProperty("file", out var name))
                {
                    var list = window.GetVisualDescendants().OfType<FileListView>()
                        .First(c => c.IsEffectivelyVisible && ReferenceEquals(c.DataContext, pane));
                    var fast = list.GetVisualDescendants().OfType<FastFileList>().First(c => c.IsEffectivelyVisible);
                    var entry = fast.Rows.Single(e => e.Name == name.GetString());
                    fast.ScrollToEntry(entry);
                    await CommitAsync(window);
                    var index = fast.Rows.ToList().IndexOf(entry);
                    rect = fast.IsGrid ? fast.GridIconTargetBounds(index) : fast.NameBounds(index);
                    target = fast;
                }
                else
                {
                    var selector = Text(command, "name");
                    target = Controls(window).First(c => c.IsEffectivelyVisible && (c.Name == selector
                        || AutomationProperties.GetName(c) == selector
                        || c is MenuItem mi && mi.Header?.ToString() == selector
                        || c is Button button && (button.Tag?.ToString() == selector || button.Content as string == selector)));
                    rect = new Rect(target.Bounds.Size);
                }
                if (rect.Width <= 0 || rect.Height <= 0 || !target.IsEffectivelyVisible)
                    throw new InvalidOperationException("Target is not visible.");
                var p = target.PointToScreen(rect.Center);
                // Avalonia macOS PixelPoint uses desktop coordinates; recorder/input reports display scale separately.
                return new { x = p.X, y = p.Y, width = rect.Width, height = rect.Height, window = window.GetType().Name };
            case "text":
                var box = Controls(Window(main, kind)).OfType<TextBox>().Single(c => c.Name == Text(command, "name"));
                box.Text = Text(command, "text");
                return true;
            case "preview":
                await main.DemoPreviewAsync(LocalPath(Text(command, "path")));
                await CommitAsync(main);
                return true;
            case "preview-close": main.DemoClosePreview(); return true;
            case "global-search-open": main.DemoOpenGlobalSearch(); await CommitAsync(main); return true;
            case "editor-open": await main.OpenMarkdownEditorAsync(LocalPath(Text(command, "path"))); return true;
            case "editor-save":
                var editor = main.GetVisualDescendants().OfType<MarkdownEditorView>().Single();
                return await editor.SaveAsync();
            case "index-ready":
                var indexer = App.Services.GetRequiredService<SearchIndexer>();
                indexer.EnsureRoot(_root);
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    while (true)
                    {
                        var observation = indexer.Observe(_root);
                        if (observation.Status.Phase == SearchIndexPhase.Ready) break;
                        if (observation.Status.Phase is SearchIndexPhase.Partial or SearchIndexPhase.Unavailable)
                            throw new IOException(observation.Status.Error);
                        await observation.Changed.WaitAsync(timeout.Token);
                    }
                return true;
            case "analyze":
                var analysisPath = LocalPath(Text(command, "path"));
                var ticks = File.GetLastWriteTime(analysisPath).Ticks;
                var analysisTags = App.Services.GetRequiredService<IAiTagService>();
                if (Path.GetExtension(analysisPath) == ".pdf")
                    await App.Services.GetRequiredService<PdfAnalysisService>().AnalyzeAsync(analysisPath, ticks, null, CancellationToken.None);
                else
                {
                    var analysis = await App.Services.GetRequiredService<IImageAnalysisService>().AnalyzeImageAsync(analysisPath);
                    await analysisTags.SaveAnalysisResultAsync(analysisPath, ticks, analysis);
                }
                if (!await analysisTags.IsFileAnalyzedAsync(analysisPath, ticks)) throw new IOException("Analysis was not saved.");
                return true;
            case "content-search":
                await pane.NavigateToAsync(VirtualPath.AiTextSearch);
                await CommitAsync(main);
                await main.GetVisualDescendants().OfType<AiView>().Single(c => c.IsEffectivelyVisible).DemoSearchAsync(Text(command, "query"));
                return true;
            case "classification-open":
                var classifiedPath = LocalPath(Text(command, "path"));
                var label = (await App.Services.GetRequiredService<IAiTagService>().GetTagsForFileAsync(classifiedPath, false))
                    .Where(t => t.TagType is "scene" or "object" or "animal" or "food" or "plant")
                    .OrderByDescending(t => t.Confidence).FirstOrDefault()
                    ?? throw new InvalidOperationException("No real classification result was produced.");
                await pane.NavigateToAsync($"{VirtualPath.AiPrefix}{label.TagType}:{label.TagValue}");
                return new { label.TagType, label.TagValue, label.Confidence };
            case "script-dialog":
                var scriptPath = LocalPath(Path.Combine(_root, "Documents/山野计划/自动化/整理交付.sh"));
                _ = new global::MacExplorer.Views.Dialogs.HomeScriptCommandsDialog(scriptPath,
                    App.Services.GetRequiredService<HomeWorkspaceService>()).ShowDialog(main);
                return true;
            case "tag-navigate":
                var found = (await App.Services.GetRequiredService<IFileTagService>().GetSidebarTagsAsync()).Single(t => t.Name == Text(command, "name"));
                await pane.NavigateToAsync(found.VirtualPath);
                return true;
            case "copy":
                var sources = command.GetProperty("paths").EnumerateArray().Select(x => LocalPath(x.GetString()!)).ToArray();
                var destination = LocalPath(Text(command, "destination"));
                await pane.FileOps.CopyEntriesAsync(sources, destination);
                return true;
            case "undo":
                return await App.Services.GetRequiredService<IFileOperationHistoryService>().UndoLastAsync();
            case "extract":
                var archive = pane.Entries.Single(e => e.FullPath == LocalPath(Text(command, "path")));
                pane.ExtractToNamedFolder(archive);
                return true;
            case "delivery-navigate":
                await Pane(main, "delivery").NavigateToAsync(LocalPath(Text(command, "path")));
                return true;
            case "close-dialogs":
                foreach (var other in Desktop.Windows.Where(w => w != main && w.IsVisible).ToArray())
                    if (other is FileDeliveryWindow) other.Hide(); else other.Close();
                main.DemoClosePreview();
                main.Activate();
                return true;
            case "commit": await CommitAsync(Window(main, kind)); return true;
            case "shutdown":
                _stopping = true;
                Dispatcher.UIThread.Post(() => Desktop.TryShutdown(), DispatcherPriority.Background);
                return true;
            default: throw new ArgumentException($"Unknown demo operation: {op}");
        }
    }

    private static void ValidatePaths(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) ValidatePaths(property.Value);
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidatePaths(item);
        else if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text && Path.IsPathFullyQualified(text))
            LocalPath(text);
    }
}
#endif
