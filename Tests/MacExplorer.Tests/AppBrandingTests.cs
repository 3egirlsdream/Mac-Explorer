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
        var menu = ApplicationMenu.Create(language, () => aboutCalls++);
        var about = Assert.IsType<NativeMenuItem>(Assert.Single(menu.Items));
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
        Assert.Equal("Quit " + AppBranding.Name, quit.Header);
        Assert.DoesNotContain(menu.Items.OfType<NativeMenuItem>(), item => item.Header?.Contains("Avalonia") == true);
        ((INativeMenuItemExporterEventsImplBridge)quit).RaiseClicked();
        Assert.Equal(1, quitCalls);
        Assert.Equal(DistributionChannel.IsAppStore ? "文件资源管理器" : "Mac Explorer", AppBranding.Name);
        language.SetLanguage(AppLanguage.ChineseSimplified);
        Assert.Equal(DistributionChannel.IsAppStore ? "文件资源管理器" : "Mac Explorer", AppBranding.Name);
    }
}
