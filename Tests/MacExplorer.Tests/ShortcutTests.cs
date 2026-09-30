using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Platforms.MacOS;
using MacExplorer.Views;
using MacExplorer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

public sealed class ShortcutTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void HintUsesSharedRoundedGlassSurfaceAndContentInsets(bool dark)
    {
        using var service = new ShortcutService(new ShortcutMemorySettings());
        var hint = new ShortcutHintView(service) { Width = 520, MaxHeight = 400 };
        var window = InputAppearanceTests.CreateWindow(hint, dark, 640);
        try
        {
            Assert.Equal(new Avalonia.Thickness(16), hint.Padding);
            Assert.Equal(new Avalonia.Thickness(12), hint.Margin);
            Assert.Equal(new Avalonia.CornerRadius(10), hint.CornerRadius);
            var glass = Assert.Single(hint.GetVisualDescendants().OfType<LiquidGlassAvaloniaUI.LiquidGlassSurface>());
            Assert.Equal(hint.CornerRadius, glass.CornerRadius);
            Assert.Equal(8, glass.BlurRadius);
            Assert.Equal(Avalonia.Media.Color.Parse(dark ? "#F230302F" : "#F2FFFFFF"), glass.SurfaceColor);
            Assert.True(glass.CaptureForeground);
            Assert.False(glass.IsHitTestVisible);
            var scroll = Assert.Single(hint.GetVisualDescendants().OfType<ScrollViewer>());
            var inset = scroll.TranslatePoint(default, hint)!.Value;
            Assert.Equal(16, inset.X);
            Assert.Equal(16, inset.Y);
            var names = hint.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("shortcut-name")).ToArray();
            Assert.DoesNotContain(names, name => name.Text!.Contains("Markdown"));
            Assert.Contains(names, name => name.Text == "复制文件");
            Assert.Contains(names, name => name.Text == "复制文字 · 输入框／编辑器");
            Assert.NotNull(names[0].Foreground);
            Assert.All(names, name =>
            {
                Assert.Equal(1, Assert.IsType<Grid>(name.Parent).Opacity);
                Assert.Equal(names[0].Foreground, name.Foreground);
            });
            var scrollbar = hint.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
                .Single(b => b.Name == "PART_VerticalScrollBar");
            var scrollbarLeft = scrollbar.TranslatePoint(default, hint)!.Value.X;
            Assert.All(hint.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("shortcut-keys")), key =>
            {
                Assert.Equal(names[0].Foreground, key.Foreground);
                Assert.True(key.TranslatePoint(new Point(key.Bounds.Width, 0), hint)!.Value.X <= scrollbarLeft);
            });
        }
        finally { window.Close(); }
    }

    [Fact]
    public void DefaultsPreserveAliasesAndKeepHomeSeparateFromNativeHide()
    {
        using var service = new ShortcutService(new ShortcutMemorySettings());
        Assert.Equal("⇧⌘H", service.GetDisplay(ShortcutIds.Home));
        Assert.False(service.Matches(ShortcutIds.Home, KeyArgs(Key.H, KeyModifiers.Meta)));
        Assert.True(service.Matches(ShortcutIds.GlobalSearch, KeyArgs(Key.F, KeyModifiers.Meta | KeyModifiers.Shift)));
        Assert.True(service.Matches(ShortcutIds.Copy, KeyArgs(Key.C, KeyModifiers.Control)));
        Assert.False(service.Matches(ShortcutIds.Copy, KeyArgs(Key.C, KeyModifiers.Meta | KeyModifiers.Alt)));
        Assert.True(service.HintsEnabled);
    }

    [Fact]
    public void ReassignmentPersistsAndReplacesEveryOldAlias()
    {
        var settings = new ShortcutMemorySettings();
        using (var service = new ShortcutService(settings))
        {
            Assert.True(service.TrySet(ShortcutIds.Refresh, new(Key.P, KeyModifiers.Meta), out _));
            Assert.True(service.TrySet(ShortcutIds.Copy, new(Key.R, KeyModifiers.Meta), out _));
            Assert.False(service.Matches(ShortcutIds.Refresh, KeyArgs(Key.R, KeyModifiers.Meta)));
            Assert.False(service.Matches(ShortcutIds.Copy, KeyArgs(Key.C, KeyModifiers.Control)));
        }
        using var reloaded = new ShortcutService(settings);
        Assert.Equal("⌘P", reloaded.GetDisplay(ShortcutIds.Refresh));
        Assert.Equal("⌘R", reloaded.GetDisplay(ShortcutIds.Copy));
        Assert.False(reloaded.TryReset(ShortcutIds.Refresh, out var error));
        Assert.Contains("复制文件", error);
        reloaded.ResetAll();
        Assert.Equal("⌘R", reloaded.GetDisplay(ShortcutIds.Refresh));
        Assert.True(reloaded.Matches(ShortcutIds.Copy, KeyArgs(Key.C, KeyModifiers.Control)));
    }

    [Theory]
    [InlineData(Key.None, KeyModifiers.Meta)]
    [InlineData(Key.LWin, KeyModifiers.Meta)]
    [InlineData(Key.P, KeyModifiers.Control)]
    [InlineData(Key.Tab, KeyModifiers.Meta)]
    [InlineData(Key.H, KeyModifiers.Meta)]
    [InlineData(Key.Q, KeyModifiers.Meta)]
    [InlineData(Key.K, KeyModifiers.Meta)]
    [InlineData(Key.F, KeyModifiers.Meta | KeyModifiers.Control)]
    [InlineData(Key.Space, KeyModifiers.Meta | KeyModifiers.Alt)]
    [InlineData(Key.Q, KeyModifiers.Meta | KeyModifiers.Control)]
    [InlineData(Key.Q, KeyModifiers.Meta | KeyModifiers.Shift)]
    [InlineData(Key.Q, KeyModifiers.Meta | KeyModifiers.Shift | KeyModifiers.Alt)]
    [InlineData(Key.D, KeyModifiers.Meta | KeyModifiers.Alt)]
    [InlineData(Key.D6, KeyModifiers.Meta | KeyModifiers.Shift)]
    public void InvalidOrOverlappingBindingsLeaveExistingConfiguration(Key key, KeyModifiers modifiers)
    {
        using var service = new ShortcutService(new ShortcutMemorySettings());
        Assert.False(service.TrySet(ShortcutIds.Refresh, new(key, modifiers), out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Equal("⌘R", service.GetDisplay(ShortcutIds.Refresh));
    }

    [Fact]
    public void SettingsChangesReachEveryServiceAndMalformedDataFallsBack()
    {
        var settings = new ShortcutMemorySettings();
        using var first = new ShortcutService(settings);
        using var second = new ShortcutService(settings);
        var changed = 0; second.Changed += () => changed++;
        Assert.True(first.TrySet(ShortcutIds.GlobalSearch, new(Key.J, KeyModifiers.Meta), out _));
        Assert.Equal("⌘J", second.GetDisplay(ShortcutIds.GlobalSearch));
        Assert.False(second.Matches(ShortcutIds.GlobalSearch, KeyArgs(Key.F, KeyModifiers.Meta | KeyModifiers.Shift)));
        first.HintsEnabled = false;
        Assert.False(second.HintsEnabled);
        Assert.Equal(2, changed);
        settings.Set(ShortcutService.BindingsKey, "broken");
        Assert.Equal("⌘K", second.GetDisplay(ShortcutIds.GlobalSearch));
        settings.Set(ShortcutService.BindingsKey, JsonSerializer.Serialize(new Dictionary<string, ShortcutBinding>
        { [ShortcutIds.Refresh] = new(Key.C, KeyModifiers.Meta), [ShortcutIds.Copy] = new((Key)9999, KeyModifiers.Meta) }));
        Assert.Equal("⌘R", second.GetDisplay(ShortcutIds.Refresh));
        Assert.Equal("⌘C", second.GetDisplay(ShortcutIds.Copy));
    }

    [Fact]
    public void HoldStateSuppressesShortcutsAndDoesNotReopenUntilCommandIsReleased()
    {
        var state = new ShortcutHoldState();
        state.ModifiersChanged(KeyModifiers.Meta, 0); state.Advance(699);
        Assert.False(state.ShouldShow);
        state.Advance(700); Assert.True(state.ShouldShow);
        state.Interrupt(); state.Advance(3000); Assert.False(state.ShouldShow);
        // Another modifier event while Cmd remains down must not restart the timer.
        state.ModifiersChanged(KeyModifiers.Meta, 3000); state.Advance(5000); Assert.False(state.ShouldShow);
        state.ModifiersChanged(0, 5000); state.ModifiersChanged(KeyModifiers.Meta, 5001);
        state.Advance(5701); Assert.True(state.ShouldShow);
        state.Reset(); state.Advance(9000); Assert.False(state.ShouldShow);
    }

    [AvaloniaFact]
    public void HintAnimationInterruptionsRecordingAndShutdownLeaveNoOverlay()
    {
        using var service = new ShortcutService(new ShortcutMemorySettings());
        var content = new Button { Content = "Keep focus" };
        var window = new AppWindow { Width = 960, Height = 700, Content = content, Shortcuts = service };
        window.Show();
        // The headless platform doesn't send activation notifications on Show.
        typeof(WindowBase).GetMethod("HandleActivated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, null);
        long now = 0;
        using var controller = new ShortcutHintController(window, service, () => now);
        try
        {
            content.Focus();
            var focused = window.FocusManager!.GetFocusedElement();
            controller.HandleActivity(KeyboardActivity.Modifiers, Key.LWin, KeyModifiers.Meta, false);
            now = 699; controller.AdvanceAnimation(); Assert.False(controller.IsVisible);
            now = 700; controller.AdvanceAnimation();
            var hint = Assert.IsType<ShortcutHintView>(Assert.Single(window.WindowOverlayHost!.Children));
            Assert.Equal(0, hint.Opacity); Assert.Equal(6, hint.Offset.Y);
            Assert.Same(focused, window.FocusManager.GetFocusedElement());
            now = 780; controller.AdvanceAnimation(); Assert.InRange(hint.Opacity, .8, .9);
            controller.HandleActivity(KeyboardActivity.KeyDown, Key.F, KeyModifiers.Meta, false);
            now = 900; controller.AdvanceAnimation(); Assert.False(controller.IsVisible);
            now = 2000; controller.AdvanceAnimation(); Assert.Empty(window.WindowOverlayHost.Children);

            controller.HandleActivity(KeyboardActivity.Modifiers, Key.LWin, 0, false);
            controller.HandleActivity(KeyboardActivity.Modifiers, Key.RWin, KeyModifiers.Meta, false);
            now = 2700; controller.AdvanceAnimation(); Assert.True(controller.IsVisible);
            using (service.BeginRecording((_, _) => { })) Assert.False(controller.IsVisible);
            now = 4000; controller.AdvanceAnimation(); Assert.Empty(window.WindowOverlayHost.Children);

            controller.HandleActivity(KeyboardActivity.Modifiers, Key.RWin, KeyModifiers.Meta, false);
            controller.HandleActivity(KeyboardActivity.PointerDown, Key.None, KeyModifiers.Meta, false);
            now = 5000; controller.AdvanceAnimation(); Assert.False(controller.IsVisible);
            controller.HandleActivity(KeyboardActivity.Modifiers, Key.RWin, 0, false);
            controller.HandleActivity(KeyboardActivity.Modifiers, Key.RWin, KeyModifiers.Meta, false);
            now = 5700; controller.AdvanceAnimation(); Assert.True(controller.IsVisible);
            service.HintsEnabled = false; Assert.False(controller.IsVisible);
            Assert.True(controller.HandleActivity(KeyboardActivity.KeyDown, Key.F, KeyModifiers.Meta | KeyModifiers.Control, true));
            service.HintsEnabled = true;
            controller.HandleActivity(KeyboardActivity.Modifiers, Key.LWin, KeyModifiers.Meta, false);
            controller.HandleActivity(KeyboardActivity.Deactivated, Key.None, 0, false);
            now = 7000; controller.AdvanceAnimation(); Assert.Empty(window.WindowOverlayHost.Children);
            controller.HandleActivity(KeyboardActivity.Modifiers, Key.LWin, KeyModifiers.Meta, false);
            controller.Dispose(); now = 8000; controller.AdvanceAnimation();
            Assert.Empty(window.WindowOverlayHost.Children);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SettingsRecorderConsumesCommandsAndEscapeWithoutClosingWindow()
    {
        using var theme = new FastListTestTheme();
        using var service = new ShortcutService(new ShortcutMemorySettings());
        var panel = new ShortcutSettingsView(service);
        var window = new DialogWindow { Width = 700, Height = 600, Content = panel, Shortcuts = service };
        window.Styles.Add((Avalonia.Styling.Styles)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        window.Show();
        try
        {
            var key = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ShortcutBinding_navigation_refresh");
            key.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(service.IsRecording);
            window.KeyPress(Key.Q, RawInputModifiers.Meta, PhysicalKey.Q, "q");
            Assert.True(service.IsRecording); Assert.Equal("⌘R", service.GetDisplay(ShortcutIds.Refresh));
            window.KeyRelease(Key.Q, RawInputModifiers.Meta, PhysicalKey.Q, "q");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(service.IsRecording); Assert.True(window.IsVisible);
            key.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.KeyPress(Key.J, RawInputModifiers.Meta, PhysicalKey.J, "j");
            Assert.False(service.IsRecording); Assert.Equal("⌘J", service.GetDisplay(ShortcutIds.Refresh));
            Assert.Equal("J", key.Content);
            var option = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ShortcutModifier_navigation_refresh_Option");
            option.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("⌥⌘J", service.GetDisplay(ShortcutIds.Refresh));
            var homeShift = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ShortcutModifier_navigation_home_Shift");
            homeShift.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("⇧⌘H", service.GetDisplay(ShortcutIds.Home));
            Assert.Contains("active", homeShift.Classes);
            key.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.KeyPress(Key.U, RawInputModifiers.None, PhysicalKey.U, "u");
            Assert.Equal("⌥⌘U", service.GetDisplay(ShortcutIds.Refresh));
            key.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Close(); Assert.False(service.IsRecording);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ChangedBindingsUpdateTooltipsAndRouteInAllOpenWindows()
    {
        using var service = new ShortcutService(new ShortcutMemorySettings());
        var firstButton = new Button { Focusable = true }; var secondButton = new Button { Focusable = true };
        ShortcutTip.SetCommand(firstButton, ShortcutIds.FullScreen);
        ShortcutTip.SetCommand(secondButton, ShortcutIds.FullScreen);
        var first = new AppWindow { Width = 640, Height = 480, Content = firstButton, Shortcuts = service };
        var second = new AppWindow { Width = 640, Height = 480, Content = secondButton, Shortcuts = service };
        first.Show(); second.Show();
        try
        {
            Assert.True(service.TrySet(ShortcutIds.FullScreen, new(Key.J, KeyModifiers.Meta), out _));
            Assert.Equal("切换全屏 ⌘J", ToolTip.GetTip(firstButton));
            Assert.Equal(ToolTip.GetTip(firstButton), ToolTip.GetTip(secondButton));
            first.RaiseEvent(KeyArgs(Key.F, KeyModifiers.Control | KeyModifiers.Meta));
            Assert.Equal(WindowState.Normal, first.WindowState);
            first.RaiseEvent(KeyArgs(Key.J, KeyModifiers.Meta));
            Assert.Equal(WindowState.FullScreen, first.WindowState);
            Assert.Equal(WindowState.Normal, second.WindowState);
            second.RaiseEvent(KeyArgs(Key.J, KeyModifiers.Meta));
            Assert.Equal(WindowState.FullScreen, second.WindowState);
        }
        finally { first.Close(); second.Close(); }
    }

    internal static KeyEventArgs KeyArgs(Key key, KeyModifiers modifiers) => new()
        { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
}

internal sealed class ShortcutMemorySettings : ISettingsService
{
    public event Action<string>? SettingChanged;
    private readonly Dictionary<string, string> _values = [];
    public string? Get(string key) => _values.GetValueOrDefault(key);
    public T Get<T>(string key, T fallback) => Get(key) is { } value ? (T)Convert.ChangeType(value, typeof(T)) : fallback;
    public void Set(string key, string value)
    {
        if (Get(key) == value) return;
        _values[key] = value; SettingChanged?.Invoke(key);
    }
    public void Set<T>(string key, T value) => Set(key, value?.ToString() ?? "");
    public Dictionary<string, string> GetAll() => new(_values);
}

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TabNavigationUsesRealInputWrapsAndRespectsRecordingAndModalState(bool native)
    {
        using var theme = new FastListTestTheme();
        using var service = new ShortcutService(new ShortcutMemorySettings());
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 2,
            configureServices: services => services.AddSingleton<IShortcutService>(service));
        var window = fixture.Window;
        var first = fixture.Model.SelectedTab!;
        var second = fixture.AddTab();
        var third = fixture.AddTab();
        window.Shortcuts = service;
        service.HintsEnabled = false;
        typeof(WindowBase).GetMethod("HandleActivated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, null);
        using var controller = new ShortcutHintController(window, service);
        Assert.True(fixture.Workspaces[first].FileListView.FindControl<FastFileList>("FastList")!.Focus());
        void PressTab(bool previous = false)
        {
            var modifiers = RawInputModifiers.Control | (previous ? RawInputModifiers.Shift : RawInputModifiers.None);
            if (native)
                Assert.True(controller.HandleActivity(KeyboardActivity.KeyDown, Key.Tab,
                    KeyModifiers.Control | (previous ? KeyModifiers.Shift : KeyModifiers.None), false));
            else
            {
                window.KeyPress(Key.Tab, modifiers, PhysicalKey.Tab, "\t");
                window.KeyRelease(Key.Tab, modifiers, PhysicalKey.Tab, "\t");
            }
            Dispatcher.UIThread.RunJobs();
        }
        PressTab(); Assert.Same(second, fixture.Model.SelectedTab);
        PressTab(); Assert.Same(third, fixture.Model.SelectedTab);
        PressTab(); Assert.Same(first, fixture.Model.SelectedTab);
        PressTab(previous: true); Assert.Same(third, fixture.Model.SelectedTab);
        PressTab(previous: true); Assert.Same(second, fixture.Model.SelectedTab);
        var search = fixture.Workspaces[second].FindControl<TextBox>("SearchBox")!;
        Assert.True(search.Focus());
        PressTab(); Assert.Same(third, fixture.Model.SelectedTab);
        using (service.BeginRecording((_, _) => { }))
        {
            PressTab(); Assert.Same(third, fixture.Model.SelectedTab);
        }
        using (window.BlockModalParentInteraction())
        {
            PressTab(); Assert.Same(third, fixture.Model.SelectedTab);
        }
    }

    [AvaloniaFact]
    public async Task ConfiguredApplicationShortcutsReplaceAliasesAndExecuteOnlyOnce()
    {
        using var theme = new FastListTestTheme();
        using var service = new ShortcutService(new ShortcutMemorySettings());
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0, configureServices: services =>
            services.AddSingleton<IShortcutService>(service)
                .AddTransient<FileListViewModel>(_ => CreateViewModel(new FakeFileService("/tmp/ShortcutCreatedTab"))));
        var window = fixture.Window;
        window.Shortcuts = service;
        var overlay = window.FindControl<Border>("GlobalSearchOverlay")!;
        Assert.True(service.TrySet(ShortcutIds.GlobalSearch, new(Key.J, KeyModifiers.Meta), out _));
        window.Focus();
        window.KeyPress(Key.K, RawInputModifiers.Meta, PhysicalKey.K, null); Assert.False(overlay.IsVisible);
        window.KeyPress(Key.F, RawInputModifiers.Meta | RawInputModifiers.Shift, PhysicalKey.F, null); Assert.False(overlay.IsVisible);
        window.KeyPress(Key.J, RawInputModifiers.Meta, PhysicalKey.J, null); Assert.True(overlay.IsVisible);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Assert.False(overlay.IsVisible);
        Assert.True(service.TryReset(ShortcutIds.GlobalSearch, out _));
        window.KeyPress(Key.F, RawInputModifiers.Meta | RawInputModifiers.Shift, PhysicalKey.F, null); Assert.True(overlay.IsVisible);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.True(service.TrySet(ShortcutIds.NewTab, new(Key.Y, KeyModifiers.Meta), out _));
        var count = fixture.Model.Tabs.Count;
        window.KeyPress(Key.T, RawInputModifiers.Meta, PhysicalKey.T, null); Assert.Equal(count, fixture.Model.Tabs.Count);
        window.KeyPress(Key.Y, RawInputModifiers.Meta, PhysicalKey.Y, null); Assert.Equal(count + 1, fixture.Model.Tabs.Count);
    }

    [AvaloniaFact]
    public void ConfiguredFileShortcutUsesRealInputAndNeverLeaksIntoTextOrBrowseMode()
    {
        using var theme = new FastListTestTheme();
        var files = new FakeFileService("/tmp/ShortcutTests");
        files.Seed(new() { Name = "one.txt", FullPath = "/tmp/ShortcutTests/one.txt" });
        files.Seed(new() { Name = "two.txt", FullPath = "/tmp/ShortcutTests/two.txt" });
        using var vm = CreateViewModel(files);
        vm.Entries = new System.Collections.ObjectModel.ObservableCollection<Models.FileSystemEntry>(new[]
        {
            new Models.FileSystemEntry { Name = "one.txt", FullPath = "/tmp/ShortcutTests/one.txt" },
            new Models.FileSystemEntry { Name = "two.txt", FullPath = "/tmp/ShortcutTests/two.txt" }
        });
        using var service = new ShortcutService(new ShortcutMemorySettings());
        Assert.True(service.TrySet(ShortcutIds.SelectAll, new(Key.J, KeyModifiers.Meta), out _));
        var view = new FileListView { DataContext = vm, Shortcuts = service };
        var window = new AppWindow { Width = 700, Height = 500, Content = view, Shortcuts = service };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<FastFileList>("FastList")!.Focus());
            window.KeyPress(Key.J, RawInputModifiers.Meta, PhysicalKey.J, "j");
            Assert.Equal(2, vm.SelectedEntries.Count);
            vm.ClearSelection();
            Assert.False(view.TryHandleFileShortcut(ShortcutTests.KeyArgs(Key.A, KeyModifiers.Meta)));
            Assert.Empty(vm.SelectedEntries);
            var text = new TextBox { Text = "keep", SelectionStart = 0, SelectionEnd = 4 };
            var args = ShortcutTests.KeyArgs(Key.J, KeyModifiers.Meta); args.Source = text;
            Assert.False(view.TryHandleFileShortcut(args)); Assert.Empty(vm.SelectedEntries);
            Assert.True(service.TrySet(ShortcutIds.Trash, new(Key.D, KeyModifiers.Meta), out _));
            using var browseVm = CreateViewModel(files, browseOnly: true);
            var browse = new FileListView { DataContext = browseVm, Shortcuts = service };
            Assert.True(browse.TryHandleFileShortcut(ShortcutTests.KeyArgs(Key.D, KeyModifiers.Meta)));
            Assert.False(browseVm.IsDeleteConfirmDialogVisible);
        }
        finally { window.Close(); }
    }
}
