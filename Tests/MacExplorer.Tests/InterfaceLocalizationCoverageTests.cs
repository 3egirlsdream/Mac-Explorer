using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data;
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

[Trait("Kind", "UI")]
[Trait("Area", "Localization")]
public sealed class InterfaceLocalizationCoverageViewModelTests : FileListViewModelTestBase
{
    [AvaloniaFact(Explicit = true)]
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
        Assert.Equal("Workbench", tab.Title);
        language.SetLanguage(AppLanguage.ChineseSimplified);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("桌面", vm.DesktopName);
        Assert.Equal("工作台", tab.Title);
        Assert.Equal("红色", FileTagCatalog.FinderColors[0].DisplayName);
    }

}

public abstract partial class FileListViewModelTestBase
{
    protected sealed class ChineseSystemDisplayNames : IDisplayNameService
    {
        public string GetDisplayName(string path) => path.EndsWith("/Desktop") ? "桌面" : System.IO.Path.GetFileName(path);
        public string GetUserName() => "测试账户";
        public void RecordRename(string oldPath, string newPath) { }
    }

}

[Trait("Area", "Localization")]
public sealed class InterfaceLocalizationCoverageTests
{
    [AvaloniaFact(Explicit = true)]
    [Trait("Kind", "UI")]
    public void HomeMarkupAndFormattedBindingsRefreshInBothDirections()
    {
        using var language = new LocalizationService(new InMemorySettingsService(), () => AppLanguage.ChineseSimplified);
        var home = new HomeDashboard();
        var source = new FormatSource();
        var label = new TextBlock { DataContext = source };
        var format = new LocFormatExtension
        {
            Text = "搜索：{0}",
            Argument = new CompiledBinding(PropertyBinding.Path(nameof(FormatSource.Name), static (FormatSource value) => value.Name))
        };
        label.Bind(TextBlock.TextProperty, (MultiBinding)format.ProvideValue(null!));
        var window = new Window { Content = new StackPanel { Children = { home, label } } };
        try
        {
            window.Show();
            language.SetLanguage(AppLanguage.English);
            Dispatcher.UIThread.RunJobs();
            var labels = home.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray();
            Assert.Contains("Recent Files", labels);
            Assert.Contains("Collections & Tags", labels);
            Assert.Equal("Search: 我的项目", label.Text);
            source.SetName("新项目");
            Assert.Equal("Search: 新项目", label.Text);
            Assert.Equal("20 min ago", HomeItemActions.FormatAddedTime(DateTime.UtcNow.AddMinutes(-20), DateTime.UtcNow));
            var server = new RemoteServerInfo();
            Assert.Equal("Disconnected", server.StatusText);
            server.IsConnected = true;
            Assert.Equal("Connected", server.StatusText);
            language.SetLanguage("fr");
            Assert.Equal("Recherche : 新项目", label.Text);
            language.SetLanguage("zh-Hant");
            Assert.Equal("搜尋：新项目", label.Text);
            language.SetLanguage(AppLanguage.ChineseSimplified);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("搜索：新项目", label.Text);
            Assert.Contains(home.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "刚刚处理");
            Assert.Contains(home.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "收藏与标签");
        }
        finally { window.Close(); }
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public void BuiltInLocationsDoNotTranslateUnrelatedFolders()
    {
        using var language = new LocalizationService(new InMemorySettingsService(), () => AppLanguage.English);
        Assert.Equal("Users", InterfaceLocationNames.Get("/Users", "/tmp/home"));
        Assert.Equal("Users", InterfaceLocationNames.Get("/Users/", "/tmp/home"));
        Assert.Null(InterfaceLocationNames.Get("/tmp/Users", "/tmp/home"));
        Assert.Equal("Desktop", InterfaceLocationNames.Get("/tmp/home/Desktop", "/tmp/home"));
        Assert.Null(InterfaceLocationNames.Get("/tmp/home/Projects/Desktop", "/tmp/home"));
        Assert.Null(InterfaceLocationNames.Get("/tmp/我的文件", "/tmp/home"));
    }


    private sealed class FormatSource : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Name { get; private set; } = "我的项目";

        public void SetName(string name)
        {
            Name = name;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

}
