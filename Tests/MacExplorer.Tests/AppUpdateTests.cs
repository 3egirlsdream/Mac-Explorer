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

namespace MacExplorer.Tests;

public class AppUpdateTests
{
    [Fact]
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
    public async Task LegacyBackendStillSupportsUpdateDetection(string latest, bool expected)
    {
        using var http = new HttpClient(new UpdateResponseHandler { Version = new VersionInfo { Version = latest, Memo = "旧接口日志" } });
        var service = new AppUpdateService(http);
        var details = await service.GetVersionDetailsAsync(TestContext.Current.CancellationToken);
        Assert.Null(details!.History);
        Assert.Equal(expected, await service.CheckVersionAsync(TestContext.Current.CancellationToken) != null);
    }

    [AvaloniaFact]
    public void AlreadyLatestShowsCurrentReleaseMemoWithoutOfferingInstallation()
    {
        var service = new FailingUpdateService();
        service.Details = new VersionInfo
        {
            Version = service.CurrentVersion,
            History = [new VersionInfo { Version = service.CurrentVersion, Memo = "完整当前版本日志\n\n  - 详细内容" }]
        };
        var dialog = new SettingsDialog(new DefaultAppServiceStub(), new SettingsServiceStub(), new ThemeServiceStub(),
            new TypographyServiceStub(), new OpenWithAppServiceStub(), service);
        var button = dialog.FindControl<Button>("UpdateButton")!;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("检查更新", button.Content);
        Assert.Contains("当前已是最新版本", dialog.FindControl<TextBlock>("UpdateStatus")!.Text);
        Assert.Contains(service.Details.History[0].Memo, dialog.FindControl<TextBlock>("ChangelogText")!.Text);
        Assert.True(dialog.FindControl<Border>("ChangelogBorder")!.IsVisible);
        Assert.Equal(0, service.InstallAttempts);
    }

    [AvaloniaFact]
    public void StartupUpdateDisplaysAllReleaseSectionsAndUntruncatedBodies()
    {
        var service = new FailingUpdateService();
        var dialog = new SettingsDialog(new DefaultAppServiceStub(), new SettingsServiceStub(), new ThemeServiceStub(),
            new TypographyServiceStub(), new OpenWithAppServiceStub(), service);
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

    [AvaloniaFact]
    public void StartupUpdateOpensAboutPageWithoutDownloading()
    {
        var updateService = new FailingUpdateService();
        var dialog = new SettingsDialog(
            new DefaultAppServiceStub(), new SettingsServiceStub(), new ThemeServiceStub(),
            new TypographyServiceStub(), new OpenWithAppServiceStub(), updateService);
        dialog.ShowAvailableUpdate(new VersionInfo { Version = "2.0.0", Memo = "更新内容" });

        Assert.Same(dialog.FindControl<TabItem>("AboutTab"), dialog.FindControl<TabControl>("SettingsTabs")!.SelectedItem);
        Assert.Equal("立即更新", dialog.FindControl<Button>("UpdateButton")!.Content);
        Assert.Contains("2.0.0", dialog.FindControl<TextBlock>("UpdateStatus")!.Text);
        Assert.Equal("更新内容", dialog.FindControl<TextBlock>("ChangelogText")!.Text);
        Assert.Equal(0, updateService.InstallAttempts);
    }

    [Fact]
    public async Task ExtractUpdateArchivePreservesMacOsExtendedAttributes()
    {
        if (!OperatingSystem.IsMacOS())
            return;

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

    [AvaloniaFact]
    public void FailedValidationCannotBeOverwrittenByQueuedProgress()
    {
        var updateService = new FailingUpdateService();
        var dialog = new SettingsDialog(
            new DefaultAppServiceStub(),
            new SettingsServiceStub(),
            new ThemeServiceStub(),
            new TypographyServiceStub(),
            new OpenWithAppServiceStub(),
            updateService);
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
