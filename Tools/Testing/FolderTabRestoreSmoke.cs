#if DEBUG
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Rendering.Composition;
using Avalonia.Platform;
using Avalonia.Media;
using Avalonia.Styling;
using MacExplorer.Services;
using MacExplorer.Controls;
using MacExplorer.Services.Impl;
using MacExplorer.Platforms.MacCatalyst.Services;
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
    private static readonly List<StartupCheck> StartupChecks = [];
    private static readonly List<Task> StartupCaptures = [];
    private static readonly List<string> StartupErrors = [];
    private static IDisposable? _startupObserver;

    private sealed class StartupCheck
    {
        public bool NativeVisibleBeforeShow { get; init; }
        public bool BackgroundReady { get; init; }
        public bool ExpectedTheme { get; init; }
        public int TabCountBeforeShow { get; init; }
        public bool? ChromeReadyAfterOpened { get; set; }
    }

    internal static void Prepare()
    {
        if (Phase == null) return;
        if (Phase == "restore-last") Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        _startupObserver = Window.WindowOpenedEvent.AddClassHandler<MainWindow>((window, _) =>
        {
            var handle = window.TryGetPlatformHandle();
            var view = handle is IMacOSTopLevelPlatformHandle mac ? mac.NSView : handle?.Handle ?? IntPtr.Zero;
            var nativeWindow = view == IntPtr.Zero ? IntPtr.Zero : Send(view, Selector("window"));
            var visible = nativeWindow != IntPtr.Zero && Send(nativeWindow, Selector("isVisible")) != IntPtr.Zero;
            var theme = WindowBackgroundTheme.GetController(window);
            var service = App.Services.GetRequiredService<BackgroundThemeService>();
            var expectedTheme = service.Options.Enabled && service.ErrorKey == null
                && (service.Options.PresetId.Length > 0 || service.Options.ImageFile.Length > 0);
            var model = (MainWindowViewModel)window.DataContext!;
            var check = new StartupCheck { NativeVisibleBeforeShow = visible, BackgroundReady = theme?.IsActive == true,
                ExpectedTheme = expectedTheme, TabCountBeforeShow = model.Tabs.Count };
            StartupChecks.Add(check);
            if (visible) StartupErrors.Add("Native activation displayed the window before its prepared Show call.");
            if (expectedTheme && theme?.IsActive != true) StartupErrors.Add("The themed window was shown without a prepared background.");
            var savedTabs = window.InitialFolderTabSession;
            if (savedTabs == null && window.RestoreFolderTabsOnOpen && window.InitialNavigationPath == null
                && FolderWindowSession.Read(App.Services.GetRequiredService<ISettingsService>(), MainWindow.CanRestoreFolder) is { } saved)
                savedTabs = saved.Windows[saved.SelectedWindowIndex];
            if (savedTabs != null && (!window.CaptureFolderTabs().Paths.SequenceEqual(savedTabs.Paths)
                || model.Tabs.IndexOf(model.SelectedTab!) != savedTabs.SelectedIndex))
                StartupErrors.Add("The first Show did not contain all saved tab headers and the saved selection.");
            var firstFrame = window.WaitForInitialFrameAsync();
            NotifyCollectionChangedEventHandler onTabsChanged = (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add && window.IsRestoringFolderTabs)
                    StartupErrors.Add("Restored tab headers were inserted after the window was shown.");
            };
            model.Tabs.CollectionChanged += onTabsChanged;
            window.Closed += (_, _) => model.Tabs.CollectionChanged -= onTabsChanged;
            window.Opened += (_, _) =>
            {
                check.ChromeReadyAfterOpened = window.FindResource("TitleBarBackgroundBrush") is LinearGradientBrush;
                if (expectedTheme && check.ChromeReadyAfterOpened != true)
                    StartupErrors.Add("Opened replaced the prepared themed title bar with an unthemed surface.");
                StartupCaptures.Add(CaptureStartupAsync(window, firstFrame, StartupCaptures.Count));
            };
        });
    }

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
            await WaitAsync(() => owner.IsVisible);
            ActivateApplication();
            owner.ShowAndActivate();
            if (Phase == "seed")
            {
                await App.Services.GetRequiredService<BackgroundThemeService>().SelectPresetAsync("animal-panda");
                var first = Directory.CreateDirectory(Path.Combine(root, "Documents", "first")).FullName;
                var second = Directory.CreateDirectory(Path.Combine(root, "Documents", "second")).FullName;
                var third = Directory.CreateDirectory(Path.Combine(root, "Documents", "third")).FullName;
                await File.WriteAllTextAsync(Path.Combine(first, "fixture.txt"), "folder tabs");
                await ((MainWindowViewModel)owner.DataContext!).FileList.NavigateToAsync(first);
                await owner.OpenNewTabAsync(second);
                await owner.OpenNewTabAsync(first);
                for (var i = 0; i < 5; i++) await owner.OpenNewTabAsync(i % 2 == 0 ? first : second);
                var model = (MainWindowViewModel)owner.DataContext!;
                model.SelectedTab = model.Tabs[1];
                var other = App.OpenNewWindow(third);
                await WaitAsync(() => ((MainWindowViewModel)other.DataContext!).FileList.CurrentPath == third);
                await other.OpenNewTabAsync(second);
                settings.Set(FolderTabSession.EnabledKey, true);
                owner.Activate();
                var windows = desktop.Windows.OfType<MainWindow>().ToArray();
                var expected = FolderWindowSession.Capture(windows.Select(w => w.CaptureFolderTabs()), Array.IndexOf(windows, owner));
                await File.WriteAllTextAsync(expectedFile, JsonSerializer.Serialize(expected));
                await CaptureSettingsAsync(owner, output);
                owner.Activate();
                // macOS can deny activation when another app is frontmost; retain the
                // intended last active window for the quit snapshot in that case.
                App.RecordActiveMainWindow(owner);
            }
            else if (Phase == "reopen-after-close")
            {
                using (var image = new SkiaSharp.SKBitmap(640, 400))
                {
                    image.Erase(SkiaSharp.SKColors.CornflowerBlue);
                    using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
                    using var stream = encoded.AsStream();
                    await App.Services.GetRequiredService<BackgroundThemeService>().ImportAsync(stream, "startup.png");
                }
                var first = Directory.CreateDirectory(Path.Combine(root, "Documents", "first")).FullName;
                var second = Directory.CreateDirectory(Path.Combine(root, "Documents", "second")).FullName;
                await ((MainWindowViewModel)owner.DataContext!).FileList.NavigateToAsync(first);
                await owner.OpenNewTabAsync(second);
                ((MainWindowViewModel)owner.DataContext!).SelectedTab = ((MainWindowViewModel)owner.DataContext!).Tabs[0];
                settings.Set(FolderTabSession.EnabledKey, true);
                var expected = owner.CaptureFolderTabs();
                owner.Close();
                await WaitAsync(() => !desktop.Windows.Contains(owner));
                var saved = FolderWindowSession.Read(settings, MainWindow.CanRestoreFolder);
                if (saved?.Windows.Length != 1 || !saved.Windows[0].Paths.SequenceEqual(expected.Paths)
                    || saved.Windows[0].SelectedIndex != expected.SelectedIndex)
                    throw new InvalidOperationException("Closing the last window did not save all folder tabs.");
                MacDockMenuService.HandleReopen(hasVisibleWindows: false);
                await WaitAsync(() => desktop.Windows.OfType<MainWindow>().Any());
                var reopened = desktop.Windows.OfType<MainWindow>().Single();
                try
                {
                    await WaitAsync(() => ((MainWindowViewModel)reopened.DataContext!).Tabs.Count == expected.Paths.Length
                        && !reopened.IsRestoringFolderTabs
                        && ((MainWindowViewModel)reopened.DataContext!).Tabs.All(t => !t.FileList.IsLoading));
                    if (!reopened.CaptureFolderTabs().Paths.SequenceEqual(expected.Paths)
                        || reopened.CaptureFolderTabs().SelectedIndex != expected.SelectedIndex)
                        throw new InvalidOperationException("Reopening from Dock did not restore the last window's folder tabs.");
                }
                finally
                {
                    if (desktop.Windows.Contains(reopened))
                    {
                        reopened.Close();
                        await WaitAsync(() => !desktop.Windows.Contains(reopened));
                    }
                }
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
                if (windows.Any(w => w != owner && w.IsActive))
                    throw new InvalidOperationException("A background window stole activation.");
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
        try { await Task.WhenAll(StartupCaptures); }
        catch (Exception ex) { error = ex.ToString(); }
        if (StartupErrors.Count > 0) error = string.Join(Environment.NewLine, StartupErrors.Distinct()) + Environment.NewLine + error;
        _startupObserver?.Dispose();
        var report = Path.Combine(output, Phase + ".json");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { success = error == null, error, owner.IsVisible, owner.IsActive, owner.ShowActivated,
                startupChecks = StartupChecks,
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

    private static async Task CaptureStartupAsync(MainWindow window, Task firstFrame, int index)
    {
        await firstFrame;
        var output = Path.Combine(RuntimePaths.TestRoot!, "folder-tabs");
        Directory.CreateDirectory(output);
        var visual = ElementComposition.GetElementVisual(window)!;
        using var bitmap = await visual.Compositor.CreateCompositionVisualSnapshot(visual, 1);
        bitmap.Save(Path.Combine(output, $"startup-{Phase}-{index}.png"));
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
