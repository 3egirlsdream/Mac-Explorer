#if DEBUG
using System.Text.Json;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using MacExplorer.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.Testing;

internal static class FolderTabRestoreSmoke
{
    internal static string? Phase => RuntimePaths.TestRoot == null ? null
        : Environment.GetEnvironmentVariable("MACEXPLORER_FOLDER_TABS_SMOKE");
    internal static bool UseSavedSession => Phase is "restore-all" or "restore-last" or "disabled";

    internal static async Task RunAsync(MainWindow owner)
    {
        var root = RuntimePaths.TestRoot!;
        var output = Path.Combine(root, "folder-tabs");
        Directory.CreateDirectory(output);
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var settings = App.Services.GetRequiredService<ISettingsService>();
        var expectedFile = Path.Combine(output, "expected.json");
        string? error = null;
        try
        {
            ActivateApplication();
            owner.Activate();
            await WaitAsync(() => owner.IsActive);
            if (Phase == "seed")
            {
                var first = Directory.CreateDirectory(Path.Combine(root, "Documents", "first")).FullName;
                var second = Directory.CreateDirectory(Path.Combine(root, "Documents", "second")).FullName;
                var third = Directory.CreateDirectory(Path.Combine(root, "Documents", "third")).FullName;
                await File.WriteAllTextAsync(Path.Combine(first, "fixture.txt"), "folder tabs");
                await ((MainWindowViewModel)owner.DataContext!).FileList.NavigateToAsync(first);
                await owner.OpenNewTabAsync(second);
                await owner.OpenNewTabAsync(first);
                var model = (MainWindowViewModel)owner.DataContext!;
                model.SelectedTab = model.Tabs[1];
                var other = App.OpenNewWindow(third);
                await WaitAsync(() => ((MainWindowViewModel)other.DataContext!).FileList.CurrentPath == third);
                await other.OpenNewTabAsync(second);
                settings.Set(FolderTabSession.EnabledKey, true);
                owner.Activate();
                await WaitAsync(() => owner.IsActive);
                var windows = desktop.Windows.OfType<MainWindow>().ToArray();
                var expected = FolderWindowSession.Capture(windows.Select(w => w.CaptureFolderTabs()), Array.IndexOf(windows, owner));
                await File.WriteAllTextAsync(expectedFile, JsonSerializer.Serialize(expected));
                await CaptureSettingsAsync(owner, output);
                owner.Activate();
                await WaitAsync(() => owner.IsActive);
            }
            else if (Phase is "restore-all" or "restore-last")
            {
                var expected = JsonSerializer.Deserialize<FolderWindowSession>(await File.ReadAllTextAsync(expectedFile))!;
                await WaitAsync(() => desktop.Windows.OfType<MainWindow>().Count() == expected.Windows.Length
                    && desktop.Windows.OfType<MainWindow>().All(w => !w.IsRestoringFolderTabs
                        && ((MainWindowViewModel)w.DataContext!).Tabs.All(t => !t.FileList.IsLoading)));
                var windows = desktop.Windows.OfType<MainWindow>().ToArray();
                foreach (var saved in expected.Windows)
                {
                    var window = windows.Single(w => w.CaptureFolderTabs().Paths.SequenceEqual(saved.Paths));
                    if (window.CaptureFolderTabs().SelectedIndex != saved.SelectedIndex)
                        throw new InvalidOperationException("The selected folder tab changed during restore.");
                }
                if (!owner.CaptureFolderTabs().Paths.SequenceEqual(expected.Windows[expected.SelectedWindowIndex].Paths))
                    throw new InvalidOperationException("The saved active window was not restored as the primary window.");
                if (!owner.IsActive) throw new InvalidOperationException("A background window stole activation.");
                if (Phase == "restore-all")
                {
                    // Individual closes must replace the group with only the last window.
                    var last = windows.Last();
                    foreach (var window in windows.Where(w => w != last))
                    {
                        window.Close();
                        await WaitAsync(() => !desktop.Windows.Contains(window));
                    }
                    var single = FolderWindowSession.Capture([last.CaptureFolderTabs()], 0);
                    last.Close();
                    await WaitAsync(() => !desktop.Windows.Contains(last));
                    var saved = FolderWindowSession.Read(settings, MainWindow.CanRestoreFolder)!;
                    AssertSession(single, saved);
                    await File.WriteAllTextAsync(expectedFile, JsonSerializer.Serialize(single));
                }
            }
            else if (Phase == "explicit")
            {
                if (desktop.Windows.OfType<MainWindow>().Count() != 1
                    || ((MainWindowViewModel)owner.DataContext!).Tabs.Count != 1
                    || ((MainWindowViewModel)owner.DataContext!).FileList.CurrentPath != root)
                    throw new InvalidOperationException("Explicit startup location did not bypass the saved windows.");
            }
            else if (Phase == "disabled")
            {
                if (desktop.Windows.OfType<MainWindow>().Count() != 1
                    || ((MainWindowViewModel)owner.DataContext!).Tabs.Count != 1)
                    throw new InvalidOperationException("Disabled restoration created saved windows.");
            }
        }
        catch (Exception ex) { error = ex.ToString(); }
        var report = Path.Combine(output, Phase + ".json");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { success = error == null, error, owner.IsVisible, owner.IsActive, owner.ShowActivated,
                windows = desktop.Windows.OfType<MainWindow>().Select(w => new { w.IsVisible, w.IsActive, w.ShowActivated,
                    session = w.CaptureFolderTabs() }).ToArray() },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.Error.WriteLine($"Folder tabs smoke: {report}; success={error == null}");
        if (error != null) { desktop.Shutdown(1); return; }
        if (Phase == "restore-last") settings.Set(FolderTabSession.EnabledKey, false);
        if (Phase == "disabled") settings.Set(FolderTabSession.EnabledKey, true);
        // Dock clicks deactivate the app before requesting Quit. Retain the last active main window.
        if (Phase == "seed")
        {
            var app = Send(GetClass("NSApplication"), Selector("sharedApplication"));
            SendVoid(app, Selector("deactivate"));
            await WaitAsync(() => desktop.Windows.OfType<MainWindow>().All(w => !w.IsActive));
        }
        desktop.TryShutdown();
    }

    internal static void ActivateApplication()
    {
        var app = Send(GetClass("NSApplication"), Selector("sharedApplication"));
        SendOptions(app, Selector("setActivationPolicy:"), 0);
        SendBool(app, Selector("activateIgnoringOtherApps:"), true);
        var running = Send(GetClass("NSRunningApplication"), Selector("currentApplication"));
        SendOptions(running, Selector("activateWithOptions:"), 3);
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr Selector(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr target, IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr target, IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendOptions(IntPtr target, IntPtr selector, nuint options);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendBool(IntPtr target, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);

    private static void AssertSession(FolderWindowSession expected, FolderWindowSession actual)
    {
        if (expected.Windows.Length != actual.Windows.Length || expected.SelectedWindowIndex != actual.SelectedWindowIndex
            || expected.Windows.Where((window, i) => !window.Paths.SequenceEqual(actual.Windows[i].Paths)
                || window.SelectedIndex != actual.Windows[i].SelectedIndex).Any())
            throw new InvalidOperationException("The last closed window did not replace the saved window group.");
    }

    private static async Task CaptureSettingsAsync(MainWindow owner, string output)
    {
        var language = App.Services.GetRequiredService<ILocalizationService>();
        using var block = owner.BlockModalParentInteraction();
        var dialog = new SettingsDialog { DataContext = ((MainWindowViewModel)owner.DataContext!).FileList };
        var closed = dialog.ShowDialog(owner);
        try
        {
            foreach (var name in new[] { "zh-CN", "zh-Hant", "en", "fr" })
            {
                language.SetLanguage(name);
                foreach (var dark in new[] { false, true })
                {
                    dialog.MinWidth = 0;
                    dialog.Width = dark ? 640 : 1200;
                    dialog.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                    dialog.UpdateLayout();
                    var visual = ElementComposition.GetElementVisual(dialog)!;
                    await visual.Compositor.RequestCommitAsync();
                    using var bitmap = await visual.Compositor.CreateCompositionVisualSnapshot(visual, 1);
                    bitmap.Save(Path.Combine(output, $"settings-{name}-{(dark ? "dark-narrow" : "light-wide")}.png"));
                }
            }
        }
        finally { dialog.Close(); await closed; }
    }

    private static async Task WaitAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!ready()) await Task.Delay(25, timeout.Token);
    }
}
#endif
