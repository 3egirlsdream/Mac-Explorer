#if DEBUG
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using MacExplorer.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.Testing;

internal static class WindowOpenSmoke
{
    public static async Task RunAsync(MainWindow owner)
    {
        var root = RuntimePaths.TestRoot ?? throw new InvalidOperationException("An isolated test profile is required.");
        var results = new List<object>();
        var auditButtons = Environment.GetEnvironmentVariable("MACEXPLORER_BUTTON_AUDIT_SMOKE") == "1";
        var buttonOutput = Path.Combine(root, "button-components");
        if (auditButtons) Directory.CreateDirectory(buttonOutput);
        string? error = null;
        try
        {
            if (auditButtons) await ButtonComponentsSmoke.CaptureGalleryAsync(owner, buttonOutput);
            var docsDir = Path.Combine(root, "Documents");
            var script = Path.Combine(docsDir, "window-smoke.sh");
            var sampleNote = Path.Combine(docsDir, "report-draft.md");
            Directory.CreateDirectory(docsDir);
            await File.WriteAllTextAsync(script, "#!/bin/sh\n");
            await File.WriteAllTextAsync(sampleNote, "# Sample\n");
            var renameEntries = new[]
            {
                new FileSystemEntry { Name = "window-smoke.sh", FullPath = script, Size = 10, LastModified = DateTime.Now },
                new FileSystemEntry { Name = "report-draft.md", FullPath = sampleNote, Size = 9, LastModified = DateTime.Now }
            };
            var factories = new (string Name, Func<AppWindow> Create)[]
            {
                ("settings", () => new SettingsDialog { DataContext = ((MainWindowViewModel)owner.DataContext!).FileList }),
                ("confirm", () => new ConfirmDialog("ui.374",
                    LocalizationText.Get("confirm.trash_message", LocalizationText.Get("confirm.item_quote", "window-smoke.sh")),
                    "ui.006", isDanger: true)),
                ("file-conflict", () => new FileConflictDialog(new(
                    new FileSystemEntry { Name = "report-draft.md", FullPath = sampleNote, Size = 1024, LastModified = DateTime.Now },
                    new FileSystemEntry { Name = "report-draft.md", FullPath = Path.Combine(root, "Downloads", "report-draft.md"), Size = 512, LastModified = DateTime.Now.AddMinutes(-10) },
                    false, false))),
                ("file-operation", () =>
                {
                    var display = new FileOperationDisplay(FileOperationPresentation.Copy)
                    {
                        Preparing = false, TotalItems = 12, DoneItems = 5, TotalBytes = 128_000_000, DoneBytes = 52_000_000
                    };
                    var req = new FileTaskRequest(BackgroundTaskKind.Copy, "", [sampleNote], docsDir) { Operation = display };
                    var task = new BackgroundTaskInfo { Request = req, Kind = BackgroundTaskKind.Copy, CurrentFile = "report-draft.md" };
                    return new FileOperationWindow(App.Services.GetRequiredService<IBackgroundTaskManager>(), task);
                }),
                ("compress", () =>
                {
                    var d = new CompressDialog();
                    d.Configure(new CompressOptions { ArchiveName = "window-smoke", Format = ArchiveFormat.Zip, Level = CompressionLevel.Standard, OutputDirectory = docsDir, SourcePaths = [script] });
                    return d;
                }),
                ("remote", () => new RemoteConnectionDialog()),
                ("rename", () =>
                {
                    var d = new BatchRenameDialog();
                    d.Configure(
                        App.Services.GetRequiredService<IBatchRenameService>(),
                        App.Services.GetRequiredService<BatchRenameOperationService>(),
                        App.Services.GetService<ISettingsService>(),
                        App.Services.GetService<IFileOperationHistoryService>(),
                        new BatchRenameRequest { Entries = renameEntries, Rules = [new BatchRenameRule { Type = BatchRenameRuleType.AddPrefix, PrefixText = "2026_" }] });
                    return d;
                }),
                ("image-conversion", () => new ImageConversionDialog(new ConversionImageSize(1920, 1080), FileConversionFormat.Png)),
                ("password", () => new PasswordDialog()),
                ("localsend-pin", () => new LocalSendPinDialog()),
                ("localsend-address", () => new LocalSendAddressDialog(App.Services.GetRequiredService<ILocalSendService>())),
                ("localsend-receive", () => new LocalSendReceiveDialog(new("fixture", [new("fixture", "fixture.txt", 1024)], root))),
                ("home-name", () => new HomeNameDialog(LocalizationText.Get("重命名标签"), "工作资料")),
                ("tab-group-name", () => new TabGroupNameDialog("开发工作组")),
                ("script-commands", () => new HomeScriptCommandsDialog(script, App.Services.GetRequiredService<HomeWorkspaceService>()))
            };
            foreach (var (name, create) in factories)
            {
                for (var iteration = 0; iteration < 2; iteration++)
                {
                    var timer = Stopwatch.StartNew();
                    if (auditButtons)
                        App.Services.GetRequiredService<ILocalizationService>().SetLanguage(iteration == 0 ? "zh-CN" : "en");
                    var window = create();
                    if (auditButtons)
                    {
                        window.RequestedThemeVariant = iteration == 0 ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
                        if (iteration == 1) window.Width = Math.Max(window.MinWidth, Math.Min(window.Width, 430));
                    }
                    var constructMs = timer.Elapsed.TotalMilliseconds;
                    using var modal = owner.BlockModalParentInteraction();
                    try
                    {
                        var closed = window.ShowDialog(owner);
                        var showMs = timer.Elapsed.TotalMilliseconds;
                        var filled = FrameFillsWindow(window);
                        await CommitAsync(window);
                        var commitMs = timer.Elapsed.TotalMilliseconds;
                        if (auditButtons) await ButtonComponentsSmoke.CaptureAsync(window, Path.Combine(buttonOutput, $"{name}-{iteration}.png"));
                        var frame = window.GetVisualDescendants().OfType<Border>().Single(c => c.Name == "WindowFrame");
                        results.Add(new { name, iteration, constructMs, showMs, commitMs, filled,
                            clientWidth = window.ClientSize.Width, clientHeight = window.ClientSize.Height,
                            frameWidth = frame.Bounds.Width, frameHeight = frame.Bounds.Height,
                            loadedPages = window is SettingsDialog s ? s.FindControl<TabControl>("SettingsTabs")!.Items.OfType<LazyTabItem>().Count(p => p.IsContentLoaded) : 0 });
                        if (!filled) throw new InvalidOperationException($"Incomplete initial layout: {name}.");
                        if (window is SettingsDialog settings && iteration == 0)
                        {
                            var tabs = settings.FindControl<TabControl>("SettingsTabs")!;
                            if (tabs.Items.OfType<LazyTabItem>().Count(p => p.IsContentLoaded) != 1)
                                throw new InvalidOperationException("Settings eagerly loaded hidden pages.");
                            foreach (var page in tabs.Items.OfType<LazyTabItem>())
                            {
                                var pageTimer = Stopwatch.StartNew();
                                tabs.SelectedItem = page;
                                var readyMs = pageTimer.Elapsed.TotalMilliseconds;
                                settings.UpdateLayout();
                                var layoutMs = pageTimer.Elapsed.TotalMilliseconds;
                                await CommitAsync(settings);
                                results.Add(new { page = page.PageKey, readyMs, layoutMs, commitMs = pageTimer.Elapsed.TotalMilliseconds,
                                    loaded = page.IsContentLoaded });
                                if (auditButtons) await ButtonComponentsSmoke.CaptureAsync(settings, Path.Combine(buttonOutput, $"settings-{page.PageKey}.png"));
                            }
                        }
                        if (window is FileOperationWindow op) op.DismissAndClose(); else window.Close();
                        await closed;
                    }
                    finally { if (window is FileOperationWindow op) op.DismissAndClose(); else window.Close(); }
                }
            }
            for (var iteration = 0; iteration < 2; iteration++)
            {
                var timer = Stopwatch.StartNew();
                var main = App.OpenNewWindow(root);
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                main.Closed += (_, _) => closed.TrySetResult();
                var showMs = timer.Elapsed.TotalMilliseconds;
                await CommitAsync(main);
                if (auditButtons) await ButtonComponentsSmoke.CaptureAsync(main, Path.Combine(buttonOutput, $"main-{iteration}.png"));
                results.Add(new { name = "main", iteration, showMs, commitMs = timer.Elapsed.TotalMilliseconds, filled = FrameFillsWindow(main) });
                main.Close();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            var deliveryTimer = Stopwatch.StartNew();
            await App.Services.GetRequiredService<FileDeliveryController>().ShowPanelAsync();
            var desktop = (IClassicDesktopStyleApplicationLifetime)Avalonia.Application.Current!.ApplicationLifetime!;
            var delivery = desktop.Windows.OfType<FileDeliveryWindow>().Single();
            await CommitAsync(delivery);
            if (auditButtons) await ButtonComponentsSmoke.CaptureAsync(delivery, Path.Combine(buttonOutput, "file-delivery.png"));
            results.Add(new { name = "file-delivery", commitMs = deliveryTimer.Elapsed.TotalMilliseconds, filled = FrameFillsWindow(delivery) });
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            var path = Path.Combine(root, "window-open-smoke.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { results, error }, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine($"Window open smoke: {path}; error={error}");
            ((IClassicDesktopStyleApplicationLifetime)Avalonia.Application.Current!.ApplicationLifetime!).Shutdown(error == null ? 0 : 1);
        }
    }

    private static async Task CommitAsync(Window window)
    {
        window.UpdateLayout();
        var compositor = ElementComposition.GetElementVisual(window)?.Compositor
            ?? throw new InvalidOperationException("Missing window compositor.");
        await compositor.RequestCommitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static bool FrameFillsWindow(Window window)
    {
        var frame = window.GetVisualDescendants().OfType<Border>().Single(c => c.Name == "WindowFrame");
        return Math.Abs(frame.Bounds.Width - window.ClientSize.Width) < 1
            && Math.Abs(frame.Bounds.Height - window.ClientSize.Height) < 1;
    }
}
#endif
