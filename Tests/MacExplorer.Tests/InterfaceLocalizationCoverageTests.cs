using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views;
using MacExplorer.Controls;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaFact]
    public void ExistingSidebarTabsAndBreadcrumbsFollowAppLanguageAndKeepUserNames()
    {
        using var language = new LocalizationService(new StartupSettings(), () => AppLanguage.ChineseSimplified);
        var files = new FakeFileService("/tmp/FKFinderTests");
        var navigation = new MacExplorer.ViewModels.NavigationViewModel(files, displayNameService: new ChineseSystemDisplayNames())
        { CurrentPath = files.HomeDirectory + "/Desktop", IsHomePage = false };
        using var vm = CreateViewModel(files, navigation: navigation);
        navigation.UpdateBreadcrumbs();
        using var tab = new MacExplorer.ViewModels.ExplorerTabViewModel(vm);
        Assert.Equal("桌面", vm.DesktopName);
        language.SetLanguage(AppLanguage.English);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Desktop", vm.DesktopName);
        Assert.Equal("Documents", vm.DocumentsName);
        Assert.Equal("Applications", vm.ApplicationsName);
        Assert.Equal("Trash", vm.TrashName);
        Assert.Equal("Desktop", navigation.Breadcrumbs.Last().DisplayName);
        Assert.Equal("Desktop", tab.Title);
        navigation.CurrentPath = "/Users";
        navigation.UpdateBreadcrumbs();
        Assert.Equal("Users", navigation.Breadcrumbs.Last().DisplayName);
        language.SetLanguage(AppLanguage.ChineseSimplified);
        Assert.Equal("用户", navigation.Breadcrumbs.Last().DisplayName);
        language.SetLanguage(AppLanguage.English);
        Assert.Equal("Users", navigation.Breadcrumbs.Last().DisplayName);
        Assert.Equal("Red", FileTagCatalog.FinderColors[0].DisplayName);
        Assert.Equal("红色", FileTagCatalog.FinderColors[0].Name);
        Assert.Equal("红色", new FileTag("红色", "#999999", FileTagKind.Custom).DisplayName);
        navigation.IsHomePage = true;
        navigation.CurrentPath = VirtualPath.Home;
        navigation.UpdateBreadcrumbs();
        Assert.Equal("Home", tab.Title);
        language.SetLanguage(AppLanguage.ChineseSimplified);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("桌面", vm.DesktopName);
        Assert.Equal("首页", tab.Title);
        Assert.Equal("红色", FileTagCatalog.FinderColors[0].DisplayName);
    }

    private sealed class ChineseSystemDisplayNames : IDisplayNameService
    {
        public string GetDisplayName(string path) => path.EndsWith("/Desktop") ? "桌面" : System.IO.Path.GetFileName(path);
        public string GetUserName() => "测试账户";
        public void RecordRename(string oldPath, string newPath) { }
    }
}

public sealed class InterfaceLocalizationCoverageTests
{
    [AvaloniaFact]
    public void HomeMarkupAndFormattedBindingsRefreshInBothDirections()
    {
        using var language = new LocalizationService(new MemorySettings(), () => AppLanguage.ChineseSimplified);
        var home = new HomeDashboard();
        var label = new TextBlock { DataContext = new { Name = "我的项目" } };
        label.Bind(TextBlock.TextProperty, (Avalonia.Data.MultiBinding)new LocFormatExtension { Path = "Name", Text = "搜索：{0}" }.ProvideValue(null!));
        var window = new Window { Content = new StackPanel { Children = { home, label } } };
        try
        {
            window.Show();
            language.SetLanguage(AppLanguage.English);
            Dispatcher.UIThread.RunJobs();
            var labels = home.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray();
            Assert.Contains("Recently used", labels);
            Assert.Contains("Collections", labels);
            Assert.Equal("Search: 我的项目", label.Text);
            Assert.Equal("20 min ago", HomeItemActions.FormatAddedTime(DateTime.UtcNow.AddMinutes(-20), DateTime.UtcNow));
            var server = new RemoteServerInfo();
            Assert.Equal("Disconnected", server.StatusText);
            server.IsConnected = true;
            Assert.Equal("Connected", server.StatusText);
            language.SetLanguage(AppLanguage.ChineseSimplified);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("搜索：我的项目", label.Text);
            Assert.Contains(home.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "最近使用");
        }
        finally { window.Close(); }
    }

    [Fact]
    public void BuiltInLocationsDoNotTranslateUnrelatedFolders()
    {
        using var language = new LocalizationService(new MemorySettings(), () => AppLanguage.English);
        Assert.Equal("Users", InterfaceLocationNames.Get("/Users", "/tmp/home"));
        Assert.Equal("Users", InterfaceLocationNames.Get("/Users/", "/tmp/home"));
        Assert.Null(InterfaceLocationNames.Get("/tmp/Users", "/tmp/home"));
        Assert.Equal("Desktop", InterfaceLocationNames.Get("/tmp/home/Desktop", "/tmp/home"));
        Assert.Null(InterfaceLocationNames.Get("/tmp/home/Projects/Desktop", "/tmp/home"));
        Assert.Null(InterfaceLocationNames.Get("/tmp/我的文件", "/tmp/home"));
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new();
        public event Action<string>? SettingChanged;
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public T Get<T>(string key, T fallback) => fallback;
        public void Set(string key, string value) { _values[key] = value; SettingChanged?.Invoke(key); }
        public void Set<T>(string key, T value) => Set(key, value?.ToString() ?? "");
        public Dictionary<string, string> GetAll() => new(_values);
    }
}
