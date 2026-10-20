#if DEBUG
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using MacExplorer.Controls;
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
        string? error = null;
        try
        {
            var title = LocalizationText.Get("settings.general");
            var message = LocalizationText.Get("settings.general.description");
            var script = Path.Combine(root, "Documents", "window-smoke.sh");
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            await File.WriteAllTextAsync(script, "#!/bin/sh\n");
            var factories = new (string Name, Func<AppWindow> Create)[]
            {
                ("settings", () => new SettingsDialog { DataContext = ((MainWindowViewModel)owner.DataContext!).FileList }),
                ("confirm", () => new ConfirmDialog(title, message, LocalizationText.Get("保存"))),
                ("compress", () => new CompressDialog()),
                ("tasks", () => { var w = new TaskPanel(); w.SetTaskManager(App.Services.GetRequiredService<IBackgroundTaskManager>()); return w; }),
                ("remote", () => new RemoteConnectionDialog()),
                ("rename", () => new BatchRenameDialog()),
                ("image-conversion", () => new ImageConversionDialog()),
                ("delete-confirm", () => new DeleteConfirmDialog()),
                ("password", () => new PasswordDialog()),
                ("localsend-pin", () => new LocalSendPinDialog()),
                ("localsend-address", () => new LocalSendAddressDialog(App.Services.GetRequiredService<ILocalSendService>())),
                ("localsend-receive", () => new LocalSendReceiveDialog(new("fixture", [new("fixture", "fixture.txt", 0)], root))),
                ("home-name", () => new HomeNameDialog(title)),
                ("home-confirm", () => new HomeConfirmDialog(title, message)),
                ("script-commands", () => new HomeScriptCommandsDialog(script, App.Services.GetRequiredService<HomeWorkspaceService>()))
            };
            foreach (var (name, create) in factories)
            {
                for (var iteration = 0; iteration < 2; iteration++)
                {
                    var timer = Stopwatch.StartNew();
                    var window = create();
                    var constructMs = timer.Elapsed.TotalMilliseconds;
                    using var modal = owner.BlockModalParentInteraction();
                    try
                    {
                        var closed = window.ShowDialog(owner);
                        var showMs = timer.Elapsed.TotalMilliseconds;
                        var filled = FrameFillsWindow(window);
                        await CommitAsync(window);
                        var commitMs = timer.Elapsed.TotalMilliseconds;
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
                            }
                        }
                        window.Close();
                        await closed;
                    }
                    finally { window.Close(); }
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
                results.Add(new { name = "main", iteration, showMs, commitMs = timer.Elapsed.TotalMilliseconds, filled = FrameFillsWindow(main) });
                main.Close();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            var deliveryTimer = Stopwatch.StartNew();
            await App.Services.GetRequiredService<FileDeliveryController>().ShowPanelAsync();
            var desktop = (IClassicDesktopStyleApplicationLifetime)Avalonia.Application.Current!.ApplicationLifetime!;
            var delivery = desktop.Windows.OfType<FileDeliveryWindow>().Single();
            await CommitAsync(delivery);
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
