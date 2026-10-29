using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using MacExplorer.Platforms.MacOS;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Area", "Localization")]
public sealed class AppBrandingTests
{
    [AvaloniaFact]
    [Trait("Kind", "UI")]
    [Trait("Smoke", "UI")]
    public void ApplicationMenuUsesProductAboutAndRetainsQuitActionAcrossLanguages()
    {
        using var language = new LocalizationService(new PluginTestEnvironment.MemorySettings(), () => AppLanguage.ChineseSimplified);
        var aboutCalls = 0;
        var showWindowCalls = 0;
        var menu = ApplicationMenu.Create(language, () => aboutCalls++, () => showWindowCalls++);
        var about = Assert.IsType<NativeMenuItem>(menu.Items[0]);
        Assert.IsType<NativeMenuItemSeparator>(menu.Items[1]);
        var showWindow = Assert.IsType<NativeMenuItem>(menu.Items[2]);
        Assert.Equal("显示主窗口", showWindow.Header);
        Assert.Equal(new KeyGesture(Key.D0, KeyModifiers.Meta), showWindow.Gesture);
        ((INativeMenuItemExporterEventsImplBridge)showWindow).RaiseClicked();
        Assert.Equal(1, showWindowCalls);
        Assert.Equal("关于 " + AppBranding.Name, about.Header);
        ((INativeMenuItemExporterEventsImplBridge)about).RaiseClicked();
        Assert.Equal(1, aboutCalls);

        var quitCalls = 0;
        var quit = new NativeMenuItem("Quit") { Gesture = new KeyGesture(Key.Q, KeyModifiers.Meta) };
        quit.Click += (_, _) => quitCalls++;
        menu.Add(quit);
        // The native exporter adds Quit after application initialization.
        typeof(INativeMenuExporterEventsImplBridge).GetMethod("RaiseNeedsUpdate")!.Invoke(menu, null);
        Assert.Equal("退出 " + AppBranding.Name, quit.Header);
        language.SetLanguage(AppLanguage.English);
        typeof(INativeMenuExporterEventsImplBridge).GetMethod("RaiseNeedsUpdate")!.Invoke(menu, null);
        Assert.Equal("About " + AppBranding.Name, about.Header);
        Assert.Equal("Show Main Window", showWindow.Header);
        Assert.Equal("Quit " + AppBranding.Name, quit.Header);
        Assert.DoesNotContain(menu.Items.OfType<NativeMenuItem>(), item => item.Header?.Contains("Avalonia") == true);
        ((INativeMenuItemExporterEventsImplBridge)quit).RaiseClicked();
        Assert.Equal(1, quitCalls);
        Assert.Equal(DistributionChannel.IsAppStore ? "文件资源管理器" : "Mac Explorer", AppBranding.Name);
        language.SetLanguage(AppLanguage.ChineseSimplified);
        Assert.Equal(DistributionChannel.IsAppStore ? "文件资源管理器" : "Mac Explorer", AppBranding.Name);
    }

    [AvaloniaFact]
    [Trait("Kind", "UI")]
    [Trait("Smoke", "UI")]
    public void ShowMainWindowCreatesAfterCloseAndRestoresAnExistingWindow()
    {
        var windows = new System.Collections.Generic.List<Window>();
        var created = 0;
        Window Create()
        {
            created++;
            var window = new Window();
            windows.Add(window);
            window.Closed += (_, _) => windows.Remove(window);
            return window;
        }
        try
        {
            var first = App.ShowOrCreateWindow(windows, Create);
            Assert.True(first.IsVisible);
            Assert.Same(first, App.ShowOrCreateWindow(windows, Create));
            first.WindowState = WindowState.Minimized;
            Assert.Same(first, App.ShowOrCreateWindow(windows, Create));
            Assert.Equal(WindowState.Normal, first.WindowState);
            first.Hide();
            Assert.Same(first, App.ShowOrCreateWindow(windows, Create));
            Assert.True(first.IsVisible);
            Assert.Equal(1, created);
            first.Close();
            Assert.Empty(windows);
            var reopened = App.ShowOrCreateWindow(windows, Create);
            Assert.NotSame(first, reopened);
            Assert.True(reopened.IsVisible);
            Assert.Equal(2, created);
        }
        finally { foreach (var window in windows.ToArray()) window.Close(); }
    }
}
