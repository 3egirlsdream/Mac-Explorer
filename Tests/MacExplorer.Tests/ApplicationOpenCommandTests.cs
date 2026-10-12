using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Area", "ApplicationOpenCommand")]
public sealed class ApplicationOpenCommandViewModelTests : FileListViewModelTestBase
{
    [Theory]
    [InlineData(" 以 VSCode 打开 ", "VSCode")]
    [InlineData("以终端打开", "终端")]
    [InlineData("以 com.microsoft.VSCode 打开", "com.microsoft.VSCode")]
    [Trait("Kind", "Unit")]
    public void ApplicationCommandParsesNames(string input, string expected)
    {
        Assert.True(ApplicationOpenCommandService.TryParse(input, out var application));
        Assert.Equal(expected, application);
    }

    [Theory]
    [InlineData("以打开")]
    [InlineData("以  打开")]
    [InlineData("终端")]
    [InlineData("/tmp/以终端打开")]
    [InlineData("以终端打开文件")]
    [Trait("Kind", "Unit")]
    public void ApplicationCommandDoesNotConsumeOrdinaryInput(string input)
        => Assert.False(ApplicationOpenCommandService.TryParse(input, out _));

    [Theory]
    [InlineData("终端", "com.apple.Terminal")]
    [InlineData("TERMINAL", "com.apple.Terminal")]
    [InlineData("vscode", "com.microsoft.VSCode")]
    [InlineData("code", "com.microsoft.VSCode")]
    [InlineData("VSC", "com.microsoft.VSCode")]
    [InlineData("com.microsoft.vscode", "com.microsoft.VSCode")]
    [InlineData("文本编辑", "com.apple.TextEdit")]
    [InlineData("TextEdit", "com.apple.TextEdit")]
    [InlineData("Terminal.app", "com.apple.Terminal")]
    [Trait("Kind", "Unit")]
    public void ApplicationCommandMatchesInstalledNamesAndAliases(string input, string bundle)
        => Assert.Equal(bundle, ApplicationOpenCommandService.Match(CommandApps, input)[0].BundleId);

    [Fact]
    [Trait("Kind", "Unit")]
    public void ApplicationCommandKeepsAmbiguousCandidatesAndRejectsUnknownApp()
    {
        Assert.Equal(2, ApplicationOpenCommandService.Match(CommandApps, "vscode").Count);
        Assert.Empty(ApplicationOpenCommandService.Match(CommandApps, "missing-app"));
        Assert.Equal("com.microsoft.VSCode", Assert.Single(
            ApplicationOpenCommandService.Match(CommandApps, "COM.MICROSOFT.VSCODE")).BundleId);
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData("com.apple.Terminal", "terminal")]
    [InlineData("com.microsoft.VSCode", "editor")]
    [InlineData("com.apple.TextEdit", "app")]
    [Trait("Kind", "UI")]
    public async Task ApplicationCommandOpensCurrentDirectoryInsteadOfSelectedFile(string bundle, string operation)
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_AppCommand_空格 # $ ");
        try
        {
            using var vm = CreateViewModel(new FakeFileService(root.FullName));
            vm.SelectedEntries.Add(new FileSystemEntry { Name = "file.txt", FullPath = Path.Combine(root.FullName, "file.txt") });
            var launcher = new CommandLauncher();
            var service = new ApplicationOpenCommandService(() => Task.FromResult(CommandApps.ToList()), launcher);
            await service.ExecuteAsync(vm, CommandApps.Single(app => app.BundleId == bundle));
            Assert.Equal((operation, root.FullName), Assert.Single(launcher.Calls));
            Assert.Equal(root.FullName, vm.CurrentPath);
            vm.GoHome();
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(vm, CommandApps[0]));
            Assert.Single(launcher.Calls);
        }
        finally { root.Delete(true); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Kind", "UI")]
    public async Task SearchResultContainingDirectorySelectsFileOrFolder(bool directory)
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_Reveal_");
        try
        {
            var parent = Directory.CreateDirectory(Path.Combine(root.FullName, "nested"));
            var path = Path.Combine(parent.FullName, "target");
            if (directory) Directory.CreateDirectory(path);
            else await File.WriteAllTextAsync(path, "test", TestContext.Current.CancellationToken);
            var entry = new FileSystemEntry { FullPath = path, Name = "target", IsDirectory = directory };
            var files = new FakeFileService(root.FullName);
            files.Seed(entry);
            var navigation = new MacExplorer.ViewModels.NavigationViewModel(files) { CurrentPath = root.FullName, IsSearchMode = true };
            using var vm = CreateViewModel(files, navigation: navigation);
            vm.SearchScopePath = root.FullName;
            var scrolled = false;
            vm.ScrollToSelectionRequested += () => scrolled = true;
            var menu = (await vm.LoadCompleteFileContextMenuAsync(entry)).ToList();
            var copyIndex = menu.FindIndex(action => action.Label == LocalizationText.Get("复制路径"));
            var reveal = menu[copyIndex + 1];
            Assert.Equal(LocalizationText.Get("打开文件所在目录"), reveal.Label);
            await reveal.Execute!();
            Assert.Equal(parent.FullName, vm.CurrentPath);
            Assert.Equal(path, Assert.Single(vm.SelectedEntries).FullPath);
            Assert.False(vm.IsSearchMode);
            Assert.True(scrolled);
            Assert.DoesNotContain(await vm.LoadCompleteFileContextMenuAsync(entry),
                action => action.Label == LocalizationText.Get("打开文件所在目录"));
        }
        finally { root.Delete(true); }
    }

    [AvaloniaFact(Explicit = true)]
    [Trait("Kind", "UI")]
    public async Task ContainingDirectoryMenuExcludesDirectAndVirtualResultsAndMissingItemDoesNotNavigate()
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_Reveal_");
        try
        {
            var files = new FakeFileService(root.FullName);
            var navigation = new MacExplorer.ViewModels.NavigationViewModel(files) { CurrentPath = root.FullName, IsSearchMode = true };
            using var vm = CreateViewModel(files, navigation: navigation);
            vm.SearchScopePath = root.FullName;
            foreach (var entry in new[]
            {
                new FileSystemEntry { FullPath = Path.Combine(root.FullName, "direct.txt"), Name = "direct.txt" },
                new FileSystemEntry { FullPath = Path.Combine(root.FullName, "nested", "virtual.txt"), Name = "virtual.txt", IsVirtual = true },
                new FileSystemEntry { FullPath = Path.Combine(root.FullName + "-other", "other.txt"), Name = "other.txt" }
            })
                Assert.DoesNotContain(await vm.LoadCompleteFileContextMenuAsync(entry),
                    action => action.Label == LocalizationText.Get("打开文件所在目录"));
            await vm.RevealEntryInParentAsync(new FileSystemEntry { FullPath = Path.Combine(root.FullName, "missing.txt"), Name = "missing.txt" });
            Assert.True(vm.IsSearchMode);
            Assert.Empty(vm.SelectedEntries);
            Assert.Equal(LocalizationText.Get("项目或所在目录已不存在"), vm.StatusText);
        }
        finally { root.Delete(true); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData("以 vscode 打开", false, false)]
    [InlineData("vscode", false, false)]
    [InlineData("terminal", true, false)]
    [InlineData("终端", true, false)]
    [InlineData("code", false, false)]
    [InlineData("以终端打开", true, false)]
    [InlineData("以 com.microsoft.VSCode 打开", true, false)]
    [InlineData("以不存在的应用打开", false, false)]
    [InlineData("以终端打开", false, true)]
    [Trait("Kind", "UI")]
    public async Task BreadcrumbApplicationCommandRequiresChoiceAndPreservesInputOnFailure(string text, bool opens, bool fails)
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_BreadcrumbCommand_");
        Window? window = null;
        try
        {
            using var vm = CreateViewModel(new FakeFileService(root.FullName));
            var launcher = new CommandLauncher { Fail = fails };
            var commands = new ApplicationOpenCommandService(() => Task.FromResult(CommandApps.ToList()), launcher);
            var view = new BreadcrumbBar { DataContext = vm, ApplicationCommands = commands };
            window = new Window { Width = 800, Height = 400, Content = view };
            window.Show();
            view.FocusPathInput();
            var input = view.FindControl<TextBox>("PathInput")!;
            input.Text = text;
            Dispatcher.UIThread.RunJobs();
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(opens, launcher.Calls.Count > 0);
            Assert.False(vm.IsSearchMode);
            Assert.Equal(root.FullName, vm.CurrentPath);
            Assert.Equal(!opens, input.IsVisible);
            if (!opens) Assert.Equal(text, input.Text);
            if (text is "以 vscode 打开" or "vscode" or "code")
            {
                Assert.Equal(2, view.FindControl<ListBox>("PathSuggestionList")!.ItemCount);
                input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
                input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(("editor", root.FullName), Assert.Single(launcher.Calls));
                Assert.False(input.IsVisible);
            }
        }
        finally { window?.Close(); root.Delete(true); }
    }

    [AvaloniaFact(Explicit = true)]
    [Trait("Kind", "UI")]
    public async Task BreadcrumbApplicationSuggestionsDiscardCancelledResults()
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_CommandCancellation_");
        Window? window = null;
        try
        {
            using var vm = CreateViewModel(new FakeFileService(root.FullName));
            var pending = new TaskCompletionSource<List<AppListItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var commands = new ApplicationOpenCommandService(() => pending.Task, new CommandLauncher());
            var view = new BreadcrumbBar { DataContext = vm, ApplicationCommands = commands };
            window = new Window { Width = 800, Height = 400, Content = view };
            window.Show();
            view.FocusPathInput();
            var input = view.FindControl<TextBox>("PathInput")!;
            input.Text = "以终端打开";
            Dispatcher.UIThread.RunJobs();
            input.Text = "以文本编辑打开";
            Dispatcher.UIThread.RunJobs();
            pending.SetResult(CommandApps.ToList());
            var list = view.FindControl<ListBox>("PathSuggestionList")!;
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (list.ItemCount == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.Equal("com.apple.TextEdit", Assert.IsType<OmniboxSuggestion>(Assert.Single(list.Items)).Value);
        }
        finally { window?.Close(); root.Delete(true); }
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public void ApplicationDiscoveryIncludesUtilitiesAndLocalizationsButStopsAtAppBundles()
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_AppDiscovery_");
        try
        {
            static void WriteApp(string directory, string bundle, string name)
            {
                Directory.CreateDirectory(Path.Combine(directory, "Contents"));
                File.WriteAllText(Path.Combine(directory, "Contents", "Info.plist"),
                    $"<plist version=\"1.0\"><dict><key>CFBundleIdentifier</key><string>{bundle}</string><key>CFBundleName</key><string>{name}</string></dict></plist>");
            }
            var app = Path.Combine(root.FullName, "Utilities", "Terminal.app");
            WriteApp(app, "test.terminal", "Terminal");
            WriteApp(Path.Combine(app, "Contents", "Helper.app"), "test.helper", "Helper");
            var locale = Path.Combine(app, "Contents", "Resources", "zh_CN.lproj");
            Directory.CreateDirectory(locale);
            File.WriteAllText(Path.Combine(locale, "InfoPlist.strings"), "\"CFBundleDisplayName\" = \"终端\";", System.Text.Encoding.Unicode);
            var discovered = Assert.Single(OpenWithAppService.DiscoverInstalledApps([root.FullName]));
            Assert.Equal("test.terminal", discovered.BundleId);
            Assert.Contains("终端", discovered.Names);
            Assert.Contains("Terminal", discovered.Names);
        }
        finally { root.Delete(true); }
    }

    [AvaloniaFact(Explicit = true)]
    [Trait("Kind", "UI")]
    public async Task ApplicationSuggestionsUseApplicationNameAndCachedNativeImageInMatchOrder()
    {
        using var bitmap = new SkiaSharp.SKBitmap(4, 4);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var icon = Convert.ToBase64String(encoded.ToArray());
        using var vm = CreateViewModel(new FakeFileService("/tmp/FKFinder_AppIcons"));
        using var commands = new ApplicationOpenCommandService(() => Task.FromResult(CommandApps.Reverse().ToList()),
            new CommandLauncher(), _ => Task.FromResult<string?>(icon));
        var suggestions = await commands.GetSuggestionsAsync(vm, "以 vscode 打开", TestContext.Current.CancellationToken);
        Assert.Equal("com.microsoft.VSCode", suggestions[0].Value);
        Assert.Equal(LocalizationText.Get("在 {0} 中打开", "Visual Studio Code"), suggestions[0].Title);
        Assert.All(suggestions, suggestion => Assert.True(suggestion.HasIconImage));
        var repeated = await commands.GetSuggestionsAsync(vm, "以 vscode 打开", TestContext.Current.CancellationToken);
        Assert.Same(suggestions[0].IconImage, repeated[0].IconImage);

        var view = new BreadcrumbBar { DataContext = vm, ApplicationCommands = commands };
        var list = view.FindControl<ListBox>("PathSuggestionList")!;
        var row = list.ItemTemplate!.Build(suggestions[0])!;
        row.DataContext = suggestions[0];
        var window = new Window { Width = 800, Height = 100, Content = row };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(row.GetVisualDescendants().OfType<Image>(),
                control => control.IsVisible && ReferenceEquals(control.Source, suggestions[0].IconImage));
            Assert.DoesNotContain(row.GetVisualDescendants().OfType<PathIcon>(), control => control.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(null)]
    [InlineData("invalid-image")]
    [Trait("Kind", "UI")]
    public async Task ApplicationSuggestionsRemainUsableWhenIconIsUnavailable(string? icon)
    {
        using var vm = CreateViewModel(new FakeFileService("/tmp/FKFinder_AppIcons"));
        using var commands = new ApplicationOpenCommandService(() => Task.FromResult(CommandApps.ToList()),
            new CommandLauncher(), _ => Task.FromResult(icon));
        var suggestion = Assert.Single(await commands.GetSuggestionsAsync(vm, "以终端打开", TestContext.Current.CancellationToken));
        Assert.False(suggestion.HasIconImage);
        Assert.Equal(Assets.Icons.Apps, suggestion.IconData);
        Assert.Equal(LocalizationText.Get("在 {0} 中打开", "Terminal"), suggestion.Title);
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData("terminal", "com.apple.Terminal")]
    [InlineData("终端", "com.apple.Terminal")]
    [InlineData("vscode", "com.microsoft.VSCode")]
    [InlineData("code", "com.microsoft.VSCode")]
    [InlineData("com.microsoft.VSCode", "com.microsoft.VSCode")]
    [Trait("Kind", "UI")]
    public async Task BreadcrumbPlainApplicationNamesAppearBeforeFileSuggestions(string input, string bundle)
    {
        using var vm = CreateViewModel(new FakeFileService("/tmp/FKFinder_PlainAppSearch"));
        using var commands = new ApplicationOpenCommandService(() => Task.FromResult(CommandApps.ToList()), new CommandLauncher());
        var view = new BreadcrumbBar { DataContext = vm, ApplicationCommands = commands };
        OmniboxService.RegisterProvider(new ApplicationNameFileProvider());
        try
        {
            var suggestions = await view.GetInputSuggestionsAsync(input, TestContext.Current.CancellationToken);
            Assert.Equal(OmniboxSuggestionKind.Application, suggestions[0].Kind);
            Assert.Equal(bundle, suggestions[0].Value);
            Assert.Equal(OmniboxSuggestionKind.Result, suggestions[^1].Kind);
            Assert.Equal(input + ".txt", suggestions[^1].Title);
        }
        finally { OmniboxService.ClearProviders(); }
    }

    [AvaloniaFact(Explicit = true)]
    [Trait("Kind", "UI")]
    public async Task BreadcrumbExistingPathsAndUnmatchedQueriesKeepFileSuggestions()
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_AppSearchPath_");
        try
        {
            using var vm = CreateViewModel(new FakeFileService(root.FullName));
            using var commands = new ApplicationOpenCommandService(() => Task.FromResult(CommandApps.ToList()), new CommandLauncher());
            Assert.Empty(await commands.GetSuggestionsAsync(vm, root.FullName, TestContext.Current.CancellationToken));
            Assert.Empty(await commands.GetSuggestionsAsync(vm, "", TestContext.Current.CancellationToken));
            var view = new BreadcrumbBar { DataContext = vm, ApplicationCommands = commands };
            OmniboxService.RegisterProvider(new ApplicationNameFileProvider());
            try
            {
                Assert.Equal(OmniboxSuggestionKind.Result, Assert.Single(
                    await view.GetInputSuggestionsAsync("unmatched", TestContext.Current.CancellationToken)).Kind);
            }
            finally { OmniboxService.ClearProviders(); }
        }
        finally { root.Delete(true); }
    }

}

public abstract partial class FileListViewModelTestBase
{
    protected static readonly AppListItem[] CommandApps =
    [
        new() { Name = "Terminal", BundleId = "com.apple.Terminal", AppPath = "/System/Applications/Utilities/Terminal.app", Names = ["终端"] },
        new() { Name = "Visual Studio Code", BundleId = "com.microsoft.VSCode", AppPath = "/Applications/Visual Studio Code.app" },
        new() { Name = "Visual Studio Code Insiders", BundleId = "com.microsoft.VSCodeInsiders", AppPath = "/Applications/Visual Studio Code Insiders.app" },
        new() { Name = "TextEdit", BundleId = "com.apple.TextEdit", AppPath = "/System/Applications/TextEdit.app", Names = ["文本编辑"] }
    ];

    protected sealed class ApplicationNameFileProvider : IOmniboxProvider
    {
        public string Name => "Test application name files";
        public int Priority => 0;
        public Task<IReadOnlyList<OmniboxSuggestion>> GetSuggestionsAsync(
            MacExplorer.ViewModels.FileListViewModel viewModel, string input, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<OmniboxSuggestion>>([
                new(OmniboxSuggestionKind.Result, input + ".txt", "", "/fake/" + input + ".txt", Assets.Icons.File, "#7C8798")
            ]);
    }

    protected sealed class CommandLauncher : IApplicationLauncherService
    {
        public List<(string Operation, string Path)> Calls { get; } = [];
        public bool Fail { get; init; }
        private Task Record(string operation, string path)
        {
            if (Fail) throw new IOException("test failure");
            Calls.Add((operation, path));
            return Task.CompletedTask;
        }
        public Task OpenFileAsync(string filePath) => Record("file", filePath);
        public Task OpenFileWithAppAsync(string filePath, string bundleIdentifier) => Record("app", filePath);
        public Task OpenInTerminalAsync(string directoryPath) => Record("terminal", directoryPath);
        public Task OpenInEditorAsync(string path, string cliName, string bundleId) => Record("editor", path);
        public Task RevealInFinderAsync(string path) => Record("reveal", path);
    }

}
