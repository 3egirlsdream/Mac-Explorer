using Avalonia;
using Avalonia.LogicalTree;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views.Dialogs;
using Xunit;
using MacExplorer.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tests;

[Trait("Area", "AppUpdate")]
public sealed class AppUpdateTests
{
    [Fact]
    [Trait("Kind", "Unit")]
    public async Task DetailsIncludeCurrentVersionHistoryEvenWhenNoUpdateIsAvailable()
    {
        var handler = new UpdateResponseHandler();
        using var http = new HttpClient(handler);
        var service = new AppUpdateService(http);
        var memo = "feat: 标题\n\n  - 正文\n\n" + new string('长', 5000);
        handler.Version = new VersionInfo
        {
            Version = service.CurrentVersion, Memo = memo,
            History = [new VersionInfo { Version = service.CurrentVersion, Memo = memo }]
        };
        var details = await service.GetVersionDetailsAsync(TestContext.Current.CancellationToken);
        Assert.Contains("CurrentVersion=" + Uri.EscapeDataString(service.CurrentVersion), handler.LastUri!.Query);
        Assert.Equal(memo, Assert.Single(details!.History!).Memo);
        Assert.Null(await service.CheckVersionAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("999.0.0", true)]
    [InlineData("0.0.0", false)]
    [Trait("Kind", "Unit")]
    public async Task LegacyBackendStillSupportsUpdateDetection(string latest, bool expected)
    {
        using var http = new HttpClient(new UpdateResponseHandler { Version = new VersionInfo { Version = latest, Memo = "旧接口日志" } });
        var service = new AppUpdateService(http);
        var details = await service.GetVersionDetailsAsync(TestContext.Current.CancellationToken);
        Assert.Null(details!.History);
        Assert.Equal(expected, await service.CheckVersionAsync(TestContext.Current.CancellationToken) != null);
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Kind", "UI")]
    [Trait("Area", "Localization")]
    public void SettingsLanguageListUsesCatalogAndKeepsDraftsAcrossLanguageChanges(bool dark)
    {
        var styles = new Avalonia.Styling.IStyle[]
        {
            new Avalonia.Themes.Fluent.FluentTheme(),
            (Avalonia.Styling.Styles)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/Styles.axaml")),
            (Avalonia.Styling.Styles)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml"))
        };
        foreach (var style in styles) Avalonia.Application.Current!.Styles.Add(style);
        var settings = new SettingsServiceStub();
        using var language = new LocalizationService(settings, () => AppLanguage.ChineseSimplified);
        var dialog = new SettingsDialog(new DefaultAppServiceStub(), settings, new ThemeServiceStub(),
            new TypographyServiceStub(), new OpenWithAppServiceStub(), new FailingUpdateService(), localizationService: language)
        {
            Width = 780,
            RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light
        };
        using var settingsServices = new SettingsTestServices(settings);
        // Exercise the real general-settings page without starting plugins and account services.
        dialog.Opened -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), dialog, "OnOpened");
        typeof(SettingsDialog).GetMethod("LoadSettings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(dialog, null);
        typeof(SettingsDialog).GetField("_initializing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(dialog, false);
        dialog.Show();
        try
        {
            SelectPage(dialog, "send");
            SelectPage(dialog, "openwith");
            Dispatcher.UIThread.RunJobs();
            SelectPage(dialog, "about");
            SelectPage(dialog, "general");
            var configured = new List<OpenWithApp>
            {
                new() { BundleId = BuiltInOpenWithActions.RevealInFinderBundleId, Label = "在 Finder 中显示", IsTopLevel = true },
                new() { BundleId = "custom", Label = "我的应用", IsTopLevel = false }
            };
            typeof(SettingsDialog).GetField("_openWithApps", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(dialog, configured);
            typeof(SettingsDialog).GetMethod("RefreshLanguage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(dialog, null);
            var combo = dialog.FindControl<ComboBox>("LanguageCombo")!;
            Assert.Equal(new[] { "system" }.Concat(language.AvailableLanguages.Select(x => x.Id)),
                combo.Items.OfType<ComboBoxItem>().Select(x => (string)x.Tag!));
            var draft = dialog.FindControl<TextBox>("LocalSendAliasBox")!;
            draft.Text = "unfinished draft";
            foreach (var option in language.AvailableLanguages)
            {
                var item = combo.Items.OfType<ComboBoxItem>().Single(x => Equals(x.Tag, option.Id));
                combo.SelectedItem = item;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(option.Id, language.Language);
                Assert.Equal(option.NativeName, item.Content);
                Assert.Equal("unfinished draft", draft.Text);
                Assert.Equal(language["language.system"], ((ComboBoxItem)combo.Items[0]!).Content);
                Assert.True(combo.Bounds.Width > 0);
                var labels = dialog.FindControl<StackPanel>("ConfiguredAppsPanel")!.GetLogicalDescendants().OfType<TextBlock>()
                    .Select(text => text.Text).ToArray();
                Assert.Contains(configured[0].DisplayLabel, labels);
                Assert.Contains("我的应用", labels);
                Assert.Contains(Services.Impl.LocalizationText.Get("收进「打开方式」子菜单"), labels);
                Assert.Equal(PrivacyPolicy.Status, dialog.FindControl<TextBlock>("PrivacyPolicyStatus")!.Text);
            }
            language.SetLanguage(AppLanguage.System);
            Assert.Equal("system", ((ComboBoxItem)combo.SelectedItem!).Tag);
        }
        finally
        {
            dialog.Close();
            foreach (var style in styles) Avalonia.Application.Current!.Styles.Remove(style);
        }
    }

    [AvaloniaFact(Explicit = true)]
    [Trait("Kind", "UI")]
    public void AlreadyLatestShowsCurrentReleaseMemoWithoutOfferingInstallation()
    {
        using var language = new LocalizationService(new SettingsServiceStub(), () => AppLanguage.ChineseSimplified);
        var service = new FailingUpdateService();
        service.Details = new VersionInfo
        {
            Version = service.CurrentVersion,
            History = [new VersionInfo { Version = service.CurrentVersion, Memo = "完整当前版本日志\n\n  - 详细内容" }]
        };
        var dialog = new SettingsDialog(new DefaultAppServiceStub(), new SettingsServiceStub(), new ThemeServiceStub(),
            new TypographyServiceStub(), new OpenWithAppServiceStub(), service, localizationService: language);
        dialog.ShowAbout();
        var button = dialog.FindControl<Button>("UpdateButton")!;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("检查更新", button.Content);
        Assert.Contains("当前已是最新版本", dialog.FindControl<TextBlock>("UpdateStatus")!.Text);
        Assert.Contains(service.Details.History[0].Memo, dialog.FindControl<TextBlock>("ChangelogText")!.Text);
        Assert.True(dialog.FindControl<Border>("ChangelogBorder")!.IsVisible);
        Assert.Equal(0, service.InstallAttempts);
    }

    [AvaloniaFact(Explicit = true)]
    [Trait("Kind", "UI")]
    public void StartupUpdateDisplaysAllReleaseSectionsAndUntruncatedBodies()
    {
        using var language = new LocalizationService(new SettingsServiceStub(), () => AppLanguage.ChineseSimplified);
        var service = new FailingUpdateService();
        var dialog = new SettingsDialog(new DefaultAppServiceStub(), new SettingsServiceStub(), new ThemeServiceStub(),
            new TypographyServiceStub(), new OpenWithAppServiceStub(), service, localizationService: language);
        var memo = "详细正文\n\n" + new string('长', 5000);
        dialog.ShowAvailableUpdate(new VersionInfo
        {
            Version = "1.0.20",
            History = [new VersionInfo { Version = "1.0.20", Memo = memo },
                new VersionInfo { Version = "1.0.19", Memo = "中间版本" },
                new VersionInfo { Version = "1.0.18", Memo = "已安装版本" }]
        });
        var text = dialog.FindControl<TextBlock>("ChangelogText")!.Text!;
        Assert.Contains(memo, text);
        Assert.True(text.IndexOf("1.0.20", StringComparison.Ordinal) < text.IndexOf("1.0.19", StringComparison.Ordinal));
        Assert.Contains("版本 1.0.18（当前版本）", text);
        Assert.Equal("立即更新", dialog.FindControl<Button>("UpdateButton")!.Content);
        Assert.Equal(0, service.InstallAttempts);
    }

    [AvaloniaFact(Explicit = true)]
    [Trait("Kind", "UI")]
    public void StartupUpdateOpensAboutPageWithoutDownloading()
    {
        using var language = new LocalizationService(new SettingsServiceStub(), () => AppLanguage.ChineseSimplified);
        var updateService = new FailingUpdateService();
        var dialog = new SettingsDialog(
            new DefaultAppServiceStub(), new SettingsServiceStub(), new ThemeServiceStub(),
            new TypographyServiceStub(), new OpenWithAppServiceStub(), updateService, localizationService: language);
        dialog.ShowAvailableUpdate(new VersionInfo { Version = "2.0.0", Memo = "更新内容" });

        Assert.Same(dialog.FindControl<TabItem>("AboutTab"), dialog.FindControl<TabControl>("SettingsTabs")!.SelectedItem);
        Assert.Equal("about", Assert.Single(dialog.FindControl<TabControl>("SettingsTabs")!.Items
            .OfType<LazyTabItem>().Where(page => page.IsContentLoaded)).PageKey);
        Assert.Equal("立即更新", dialog.FindControl<Button>("UpdateButton")!.Content);
        Assert.Contains("2.0.0", dialog.FindControl<TextBlock>("UpdateStatus")!.Text);
        Assert.Equal("更新内容", dialog.FindControl<TextBlock>("ChangelogText")!.Text);
        Assert.Equal(0, updateService.InstallAttempts);
    }

    [Fact(Explicit = true)]
    [Trait("Kind", "Integration")]
    public async Task ExtractUpdateArchivePreservesMacOsExtendedAttributes()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Requires macOS.");

        var root = Path.Combine(Path.GetTempPath(), $"MacExplorer_UpdateTest_{Guid.NewGuid():N}");
        var source = Path.Combine(root, "Payload");
        var archive = Path.Combine(root, "update.zip");
        var extracted = Path.Combine(root, "extracted");
        var sourceFile = Path.Combine(source, "signed-component.dll");

        try
        {
            var ct = TestContext.Current.CancellationToken;
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(sourceFile, "test component", ct);
            await RunProcessAsync(
                "/usr/bin/xattr",
                ["-w", "com.macexplorer.update-test", "preserved", sourceFile],
                ct);
            await RunProcessAsync(
                "/usr/bin/ditto",
                ["-c", "-k", "--sequesterRsrc", "--keepParent", source, archive],
                ct);

            await AppUpdateService.ExtractUpdateArchiveAsync(archive, extracted, ct);

            var extractedFile = Path.Combine(extracted, "Payload", "signed-component.dll");
            var attribute = await RunProcessAsync(
                "/usr/bin/xattr",
                ["-p", "com.macexplorer.update-test", extractedFile],
                ct);
            Assert.Equal("preserved", attribute);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaFact(Explicit = true)]
    [Trait("Kind", "UI")]
    public void FailedValidationCannotBeOverwrittenByQueuedProgress()
    {
        using var language = new LocalizationService(new SettingsServiceStub(), () => AppLanguage.ChineseSimplified);
        var updateService = new FailingUpdateService();
        var dialog = new SettingsDialog(
            new DefaultAppServiceStub(),
            new SettingsServiceStub(),
            new ThemeServiceStub(),
            new TypographyServiceStub(),
            new OpenWithAppServiceStub(),
            updateService, localizationService: language);
        dialog.ShowAbout();
        var updateButton = dialog.FindControl<Button>("UpdateButton")!;
        var updateStatus = dialog.FindControl<TextBlock>("UpdateStatus")!;

        updateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("立即更新", updateButton.Content);

        updateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("重试更新", updateButton.Content);
        Assert.True(updateButton.IsEnabled);
        Assert.Contains("模拟签名校验失败", updateStatus.Text);

        updateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, updateService.InstallAttempts);
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Kind", "UI")]
    [Trait("Area", "Window")]
    public void SettingsPagesAreBuiltOnDemandAndKeepDraftsAndControls(bool dark)
    {
        var settings = new SettingsServiceStub();
        using var services = new SettingsTestServices(settings);
        using var language = new LocalizationService(settings, () => AppLanguage.ChineseSimplified);
        var dialog = new SettingsDialog(new DefaultAppServiceStub(), settings, new ThemeServiceStub(),
            new TypographyServiceStub(), new OpenWithAppServiceStub(), new FailingUpdateService(), localizationService: language)
            { Width = 780, Height = 540,
                RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light };
        var theme = new Avalonia.Themes.Fluent.FluentTheme();
        Avalonia.Application.Current!.Styles.Insert(0, theme);
        var tabs = dialog.FindControl<TabControl>("SettingsTabs")!;
        var pages = tabs.Items.OfType<LazyTabItem>().ToArray();
        Assert.Equal(11, pages.Length);
        Assert.All(pages, page => Assert.False(page.IsContentLoaded));
        Assert.Null(dialog.FindControl<ContentControl>("ShortcutSettingsHost"));
        dialog.Show();
        try
        {
            Assert.Equal("general", Assert.Single(pages.Where(p => p.IsContentLoaded)).PageKey);
            SelectPage(dialog, "send");
            var draft = dialog.FindControl<TextBox>("LocalSendAliasBox")!;
            draft.Text = "unfinished alias";
            SelectPage(dialog, "general");
            SelectPage(dialog, "send");
            Assert.Same(draft, dialog.FindControl<TextBox>("LocalSendAliasBox"));
            Assert.Equal("unfinished alias", draft.Text);
            Assert.Equal(2, pages.Count(p => p.IsContentLoaded));
            Assert.False(pages.Single(p => p.PageKey == "plugins").IsContentLoaded);
            Assert.False(pages.Single(p => p.PageKey == "shortcuts").IsContentLoaded);
            dialog.UpdateLayout();
            var body = Assert.IsAssignableFrom<Control>(tabs.SelectedContent);
            var origin = body.TranslatePoint(default, dialog)!.Value;
            Assert.True(body.Bounds.Width > 0 && body.Bounds.Height > 0);
            Assert.InRange(origin.X + body.Bounds.Width, 1, dialog.ClientSize.Width);
            Assert.InRange(origin.Y + body.Bounds.Height, 1, dialog.ClientSize.Height);
            var lastHeader = pages[^1];
            Assert.True(lastHeader.TranslatePoint(default, dialog)!.Value.Y + lastHeader.Bounds.Height <= dialog.ClientSize.Height);
        }
        finally { dialog.Close(); Avalonia.Application.Current!.Styles.Remove(theme); }
    }

    private static void SelectPage(SettingsDialog dialog, string key) =>
        dialog.FindControl<TabControl>("SettingsTabs")!.SelectedItem =
            dialog.FindControl<TabControl>("SettingsTabs")!.Items.OfType<LazyTabItem>().Single(p => p.PageKey == key);

    private sealed class SettingsTestServices : IDisposable
    {
        private readonly IServiceProvider _previous = App.Services;
        private readonly ServiceProvider _provider;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "settings-lazy-" + Guid.NewGuid().ToString("N"));
        public SettingsTestServices(ISettingsService settings)
        {
            _provider = new ServiceCollection().AddSingleton(new PhotoLocationConsent(settings, Path.Combine(_root, "consent"))).BuildServiceProvider();
            typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, _provider);
        }
        public void Dispose()
        {
            typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, _previous);
            _provider.Dispose();
            Directory.Delete(_root, true);
        }
    }

    private static async Task<string> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        Assert.True(process.Start());
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, error);
        return output.Trim();
    }

    private sealed class FailingUpdateService : IAppUpdateService
    {
        public int InstallAttempts { get; private set; }

        public string CurrentVersion => "1.0.18";

        public Task<VersionInfo?> GetVersionDetailsAsync(CancellationToken ct = default) => CheckVersionAsync(ct);

        public VersionInfo Details { get; set; } = new VersionInfo
            {
                Version = "1.0.19",
                Path = "https://example.com/update.zip",
            };

        public Task<VersionInfo?> CheckVersionAsync(CancellationToken ct = default) =>
            Task.FromResult<VersionInfo?>(Details);

        public Task DownloadAndInstallAsync(
            VersionInfo versionInfo,
            IProgress<(double Progress, string Status)>? progress = null,
            CancellationToken ct = default)
        {
            InstallAttempts++;
            progress?.Report((100, "正在校验更新包..."));
            return Task.FromException(new InvalidOperationException("模拟签名校验失败"));
        }
    }

    private sealed class UpdateResponseHandler : HttpMessageHandler
    {
        public VersionInfo Version { get; set; } = new();
        public Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new VersionCheckResponse { Success = true, Data = Version })
            });
        }
    }

    private sealed class DefaultAppServiceStub : IDefaultAppService
    {
        public bool IsDefaultFolderHandler() => false;

        public (bool Success, string Message) SetAsDefaultFolderHandler() =>
            (true, string.Empty);

        public (bool Success, string Message) ResetDefaultFolderHandler() =>
            (true, string.Empty);
    }

    private sealed class SettingsServiceStub : ISettingsService
    {
        public event Action<string>? SettingChanged { add { } remove { } }
        public string? Get(string key) => null;

        public T Get<T>(string key, T defaultValue) => defaultValue;

        public void Set(string key, string value)
        {
        }

        public void Set<T>(string key, T value)
        {
        }

        public Dictionary<string, string> GetAll() => [];
    }

    private sealed class ThemeServiceStub : IThemeService
    {
        public bool IsDarkMode => false;

        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged
        {
            add { }
            remove { }
        }

        public void Initialize()
        {
        }

        public void SetThemeMode(string mode)
        {
        }

        public string GetThemeMode() => "system";
    }

    private sealed class TypographyServiceStub : ITypographyService
    {
        public FontSizePreset CurrentPreset => FontSizePreset.Standard;

        public event EventHandler<TypographyChangedEventArgs>? TypographyChanged
        {
            add { }
            remove { }
        }

        public void Initialize()
        {
        }

        public void SetPreset(FontSizePreset preset)
        {
        }
    }

    private sealed class OpenWithAppServiceStub : IOpenWithAppService
    {
        public Task<List<OpenWithApp>> GetAllAsync() => Task.FromResult<List<OpenWithApp>>([]);

        public Task<List<OpenWithApp>> GetTopLevelAppsAsync() => Task.FromResult<List<OpenWithApp>>([]);

        public Task<List<OpenWithApp>> GetSubmenuAppsAsync() => Task.FromResult<List<OpenWithApp>>([]);

        public Task<string?> GetAppIconBase64Async(string bundleId) => Task.FromResult<string?>(null);

        public Task<string?> GetAppIconBase64ByPathAsync(string appPath) => Task.FromResult<string?>(null);

        public Task AddAsync(string bundleId, string label, bool isTopLevel, string? iconBase64 = null) =>
            Task.CompletedTask;

        public Task UpdateAsync(int id, string? label, bool? isTopLevel, int? sortOrder) =>
            Task.CompletedTask;

        public Task RemoveAsync(int id) => Task.CompletedTask;

        public Task<int> RemoveUnavailableAppsAsync() => Task.FromResult(0);

        public Task<List<AppListItem>> GetInstalledAppsAsync() => Task.FromResult<List<AppListItem>>([]);
    }

}
