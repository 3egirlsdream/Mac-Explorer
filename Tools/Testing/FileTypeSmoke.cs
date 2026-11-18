#if DEBUG
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.Testing;

internal static class FileTypeSmoke
{
    public static async Task RunAsync(MainWindow owner)
    {
        var output = Path.Combine(RuntimePaths.TestRoot!, "file-types");
        var fixture = Path.Combine(output, "fixtures"); Directory.CreateDirectory(fixture);
        var checks = new List<string>(); var rows = new List<object>(); string? error = null;
        try
        {
            foreach (var name in new[] { "NapCat.Framework", "Project.xcodeproj", "Example.app", "普通目录" })
                Directory.CreateDirectory(Path.Combine(fixture, name));
            await File.WriteAllTextAsync(Path.Combine(fixture, "NapCat.Framework", "napcat-inside.txt"), "isolated fixture");
            await File.WriteAllTextAsync(Path.Combine(fixture, "完整名称 Full file name.txt"), "isolated fixture");
            Directory.CreateSymbolicLink(Path.Combine(fixture, "Browser shortcut.app"), "Example.app");
            if (Directory.Exists("/Applications/Safari.app") && DirectoryAccess.Current.CanAccess("/Applications/Safari.app"))
                Directory.CreateSymbolicLink(Path.Combine(fixture, "Safari.app"), "/Applications/Safari.app");
            var model = (MainWindowViewModel)owner.DataContext!;
            var language = App.Services.GetRequiredService<ILocalizationService>();
            owner.Width = 1100; owner.Height = 740;
            var files = App.Services.GetRequiredService<IFileService>();
            var coldFolder = Directory.CreateDirectory(Path.Combine(output, "cold")).FullName;
            await model.FileList.NavigateToAsync(coldFolder);
            var cold = await files.GetEntryAsync("/Applications/Safari.app") ?? throw new IOException("Safari missing");
            var cache = Path.Combine(RuntimePaths.CacheDirectory, "icon-cache");
            if (cold.IconUrl != null || Directory.EnumerateFiles(cache, "*.png").Any())
                throw new InvalidOperationException("An application icon was already cached before the cold panel check.");
            model.FileList.IsInfoPanelVisible = true;
            model.FileList.SelectEntry(cold);
            await WaitForApplicationPanelAsync(cold);
            checks.Add("Empty list and empty disk icon cache: selecting Safari loads its native panel bitmap");
            var timer = Stopwatch.StartNew();
            await model.FileList.NavigateToAsync(fixture);
            var loadMs = timer.Elapsed.TotalMilliseconds;
            var project = model.FileList.Entries.Single(item => item.Name == "Project.xcodeproj");
            if (project.Package?.IsPackage == true && project.DetailsIconSource.IconKey != "file-code")
                throw new InvalidOperationException("Project package lost its type icon.");
            rows.Add(new { project.Name, package = project.Package?.IsPackage, icon = project.DetailsIconSource.IconKey });
            await files.ResolveAppIconsAsync(model.FileList.Entries);
            foreach (var linkedApp in model.FileList.Entries.Where(item => item.IsSymbolicLink))
            {
                if (!linkedApp.IsApplication || linkedApp.Package?.IsPackage != true || linkedApp.IsFolder || linkedApp.IconUrl == null)
                    throw new InvalidOperationException("Application symlink was misclassified or has no native icon.");
                rows.Add(new { linkedApp.Name, linkedApp.FullPath, linkedApp.DisplayName, linkedApp.IsSymbolicLink,
                    linkedApp.IsApplication, nativeIcon = true });
            }
            checks.Add("Application links, including a chain to system Safari when available, retain their paths and use native application icons");
            foreach (var (locale, dark, mode, width) in new[] {
                ("zh-CN", false, ViewMode.Grid, 1100), ("en", true, ViewMode.Grid, 760),
                ("zh-Hant", true, ViewMode.List, 1100), ("fr", false, ViewMode.List, 760) })
            {
                language.SetLanguage(locale); owner.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                owner.MinWidth = 640; owner.Width = width; model.FileList.SetViewMode(mode);
                var entry = model.FileList.Entries.Single(item => item.Name == "NapCat.Framework");
                if (!entry.IsFolder || entry.Package?.IsPackage != false || entry.IsApplication || entry.DisplayName != entry.Name
                    || entry.DetailsIconSource.IconKey != "folder" || entry.KindText != LocalizationText.Get("文件夹"))
                    throw new InvalidOperationException("Framework directory was misclassified.");
                model.FileList.SelectEntry(entry); model.FileList.IsInfoPanelVisible = width >= 1000 || mode == ViewMode.List;
                await ButtonComponentsSmoke.CaptureAsync(owner, Path.Combine(output, $"{locale}-{mode}.png"));
                rows.Add(new { locale, mode = mode.ToString(), entry.Name, entry.DisplayName, entry.KindText,
                    entry.IsDirectory, entry.IsFolder, entry.IsApplication });
            }
            checks.Add("Four languages, light/dark, grid/list, narrow window and information panel");
            language.SetLanguage("zh-CN"); owner.RequestedThemeVariant = ThemeVariant.Light;
            owner.Width = 1100; model.FileList.SetViewMode(ViewMode.List); model.FileList.IsInfoPanelVisible = true;
            model.FileList.SelectEntry(model.FileList.Entries.FirstOrDefault(item => item.Name == "Safari.app")
                ?? model.FileList.Entries.Single(item => item.Name == "Browser shortcut.app"));
            var selectedApplication = model.FileList.SelectedEntries.Single();
            await WaitForApplicationPanelAsync(selectedApplication);
            await ButtonComponentsSmoke.CaptureAsync(owner, Path.Combine(output, "application-link.png"));
            var framework = model.FileList.Entries.Single(entry => entry.Name == "NapCat.Framework");
            var menu = await model.FileList.LoadCompleteFileContextMenuAsync(framework);
            if (menu.Any(action => action.Label == LocalizationText.Get("显示包内容"))) throw new InvalidOperationException("Ordinary directory has package action.");
            model.FileList.SetViewMode(ViewMode.Tree);
            await model.FileList.ExpandTreeDirectoryAsync(framework);
            if (!model.FileList.TreeRows.Any(row => row.Depth > 0 && row.Entry.Name == "napcat-inside.txt"))
                throw new InvalidOperationException("Framework folder did not expand.");
            await model.FileList.OpenEntryAsync(framework);
            if (model.FileList.CurrentPath != framework.FullPath) throw new InvalidOperationException("Directory did not open internally.");
            checks.Add("Ordinary framework directory opens internally and has no package action");
            var search = App.Services.GetRequiredService<Services.Search.ISearchSessionService>();
            var matches = new List<FileSystemEntry>();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await foreach (var snapshot in search.SearchSnapshotsAsync(fixture, "napcat", 20, deadline.Token))
            {
                matches.Clear(); matches.AddRange(snapshot.Entries);
            }
            var found = matches.Single(entry => entry.Name == "NapCat.Framework");
            if (!found.IsFolder || found.DisplayName != "NapCat.Framework" || !matches.Any(entry => entry.Name == "napcat-inside.txt"))
                throw new InvalidOperationException("Search metadata or directory coverage differs from enumeration.");
            checks.Add("Search agrees with enumeration and indexes ordinary framework contents");
            rows.Add(new { loadMs, searchMatches = matches.Count });
            if (Directory.Exists("/Applications/Utilities") && DirectoryAccess.Current.CanAccess("/Applications"))
            {
                owner.Width = 1400; model.FileList.SetViewMode(ViewMode.Tree);
                await model.FileList.NavigateToAsync("/Applications");
                model.FileList.IsInfoPanelVisible = true;
                var utilities = model.FileList.TreeRows.Single(row => row.Entry.FullPath == "/Applications/Utilities").Entry;
                await model.FileList.ToggleTreeDirectoryAsync(utilities);
                await WaitAsync(() => model.FileList.TreeRows.Any(row => row.Depth > 0 && row.Entry.IsApplication)
                    && model.FileList.TreeRows.Where(row => row.Depth > 0 && row.Entry.IsApplication).All(row => row.Entry.IconUrl != null));
                var applications = model.FileList.TreeRows.Where(row => row.Depth > 0 && row.Entry.IsApplication).Select(row => row.Entry).ToArray();
                foreach (var application in applications.Where(entry => entry.Name is "Terminal.app" or "Activity Monitor.app" or "Disk Utility.app"))
                {
                    model.FileList.SelectEntry(application); await WaitForApplicationPanelAsync(application);
                    var view = owner.GetVisualDescendants().OfType<FileListView>().Single(item => item.IsEffectivelyVisible);
                    view.FindControl<FastFileList>("FastList")!.ScrollToEntry(application, 200);
                    await ButtonComponentsSmoke.CaptureAsync(owner, Path.Combine(output, Path.GetFileNameWithoutExtension(application.Name) + "-tree.png"));
                    rows.Add(new { application.Name, application.Size, nativePreview = true, treeDepth = 1 });
                }
                var safari = model.FileList.Entries.FirstOrDefault(entry => entry.Name == "Safari.app");
                if (safari != null)
                {
                    model.FileList.SelectEntry(safari); await WaitForApplicationPanelAsync(safari);
                    var view = owner.GetVisualDescendants().OfType<FileListView>().Single(item => item.IsEffectivelyVisible);
                    view.FindControl<FastFileList>("FastList")!.ScrollToEntry(safari, 150);
                    await ButtonComponentsSmoke.CaptureAsync(owner, Path.Combine(output, "Safari-applications.png"));
                    rows.Add(new { safari.FullPath, safari.Size, nativePreview = true });
                }
                await model.FileList.NavigateToAsync(utilities.FullPath);
                model.FileList.IsInfoPanelVisible = true;
                foreach (var name in new[] { "Terminal.app", "Activity Monitor.app" })
                {
                    var direct = model.FileList.Entries.Single(item => item.Name == name);
                    model.FileList.SelectEntry(direct);
                    await WaitForApplicationPanelAsync(direct);
                    await ButtonComponentsSmoke.CaptureAsync(owner, Path.Combine(output, name + "-direct.png"));
                }
                checks.Add($"Expanded Utilities: {applications.Length} application icons resolved without explicit icon loading; native information-panel images and sizes verified for selected real applications");
            }
        }
        catch (Exception ex)
        {
            error = ex.ToString();
            await ButtonComponentsSmoke.CaptureAsync(owner, Path.Combine(output, "failure.png"));
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { success = error == null, checks, rows, error }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"File type smoke: {output}; success={error == null}");
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime) lifetime.Shutdown(error == null ? 0 : 1);
        }

        async Task WaitForApplicationPanelAsync(FileSystemEntry application)
        {
            await WaitAsync(() =>
            {
                var panel = owner.GetVisualDescendants().OfType<InfoPanelView>().FirstOrDefault(item => item.IsEffectivelyVisible);
                return panel != null && panel.FindControl<TextBlock>("SelectedFileName")!.Text == application.DisplayName
                    && application.IconUrl is { } source && FileListView.TryGetCachedEntryImage(source) is { } bitmap
                    && ReferenceEquals(panel.FindControl<Image>("PreviewFileIcon")!.Source, bitmap)
                    && (application.Size <= 0 || panel.FindControl<TextBlock>("InfoSize")!.Text == application.FormattedSize);
            });
            var expected = Platforms.MacOS.MacSandboxNative.ApplicationIcon(application.Package!.ApplicationPath!, 128);
            if (expected == null || !File.ReadAllBytes(application.IconUrl!).SequenceEqual(Convert.FromBase64String(expected)))
                throw new InvalidOperationException("Displayed application icon differs from NSWorkspace: " + application.Name);
        }

        static async Task WaitAsync(Func<bool> ready)
        {
            var timer = Stopwatch.StartNew();
            while (!ready())
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Native application presentation did not become ready.");
                await Task.Delay(20);
            }
        }

    }
}
#endif
