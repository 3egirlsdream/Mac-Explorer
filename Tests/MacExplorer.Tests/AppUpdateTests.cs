using System.Diagnostics;
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
    public void SettingsDialogDoesNotExposeUpdateControls()
    {
        var updateService = new FailingUpdateService();
        var dialog = new SettingsDialog(
            new DefaultAppServiceStub(),
            new SettingsServiceStub(),
            new ThemeServiceStub(),
            new TypographyServiceStub(),
            new OpenWithAppServiceStub(),
            updateService);

        Assert.Null(dialog.FindControl<Button>("UpdateButton"));
        Assert.Null(dialog.FindControl<ProgressBar>("UpdateProgress"));
        Assert.Equal(0, updateService.InstallAttempts);
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

        public Task<VersionInfo?> CheckVersionAsync(CancellationToken ct = default) =>
            Task.FromResult<VersionInfo?>(new VersionInfo
            {
                Version = "1.0.19",
                Path = "https://example.com/update.zip",
            });

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
