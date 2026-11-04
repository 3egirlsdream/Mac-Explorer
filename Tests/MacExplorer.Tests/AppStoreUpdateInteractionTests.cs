using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views.Dialogs;
using Xunit;
using static MacExplorer.Tests.AppUpdateTests;

namespace MacExplorer.Tests;

[Trait("Area", "AppUpdate")]
[Trait("Area", "AppStoreUpdate")]
public sealed class AppStoreUpdateInteractionTests
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData("zh-CN", true)]
    [InlineData("zh-Hant", true)]
    [InlineData("en", true)]
    [InlineData("fr", true)]
    [InlineData("zh-CN", false)]
    [Trait("Kind", "UI")]
    public void StoreUpdateOpensAppStoreAndNeverDownloads(string languageId, bool opens)
    {
        var settings = new SettingsServiceStub();
        using var language = new LocalizationService(settings, () => languageId);
        var service = new StoreUpdateService
        {
            UpdatePageUri = new Uri("macappstore://itunes.apple.com/app/id6820338798?mt=12")
        };
        var launcher = new RecordingLauncher { Opens = opens };
        var dialog = new SettingsDialog(new DefaultAppServiceStub(), settings, new ThemeServiceStub(),
            new TypographyServiceStub(), new OpenWithAppServiceStub(), service,
            localizationService: language, launcher: launcher);
        dialog.ShowAbout();
        dialog.Show();
        try
        {
            var button = dialog.FindControl<Button>("UpdateButton")!;
            Assert.True(button.IsVisible);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(language["updates.open_store"], button.Content);
            Assert.Null(launcher.LastUri);
            Assert.Equal(0, service.InstallAttempts);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(service.UpdatePageUri, launcher.LastUri);
            Assert.Equal(language[opens ? "updates.store_opened" : "updates.store_open_failed"],
                dialog.FindControl<TextBlock>("UpdateStatus")!.Text);
            Assert.True(button.IsEnabled);
            Assert.False(dialog.FindControl<ProgressBar>("UpdateProgress")!.IsVisible);
            Assert.Equal(0, service.InstallAttempts);
            language.SetLanguage(languageId == "en" ? "zh-CN" : "en");
            Assert.Equal(language["updates.open_store"], button.Content);
            Assert.Equal(0, service.InstallAttempts);
        }
        finally { dialog.Close(); }
    }

    private sealed class RecordingLauncher : ILauncher
    {
        public bool Opens { get; init; }
        public Uri? LastUri { get; private set; }
        public Task<bool> LaunchUriAsync(Uri uri) { LastUri = uri; return Task.FromResult(Opens); }
        public Task<bool> LaunchFileAsync(IStorageItem file) => throw new NotSupportedException();
    }

    private sealed class StoreUpdateService : IAppUpdateService
    {
        public string CurrentVersion => "1.0.18";
        public Uri? UpdatePageUri { get; init; }
        public int InstallAttempts { get; private set; }
        public Task<VersionInfo?> GetVersionDetailsAsync(CancellationToken ct = default)
            => Task.FromResult<VersionInfo?>(new VersionInfo { Version = "1.0.19", Memo = "商店更新内容" });
        public Task<VersionInfo?> CheckVersionAsync(CancellationToken ct = default) => GetVersionDetailsAsync(ct);
        public Task DownloadAndInstallAsync(VersionInfo versionInfo,
            IProgress<(double Progress, string Status)>? progress = null, CancellationToken ct = default)
        {
            InstallAttempts++;
            throw new InvalidOperationException("Store updates must not download packages.");
        }
    }
}
