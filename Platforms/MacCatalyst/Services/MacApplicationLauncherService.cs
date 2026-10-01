using System.Diagnostics;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacApplicationLauncherService : IApplicationLauncherService
{
    private readonly HomeWorkspaceService? _homeWorkspace;

    public MacApplicationLauncherService(HomeWorkspaceService? homeWorkspace = null)
        => _homeWorkspace = homeWorkspace;

    public async Task OpenFileAsync(string filePath)
    {
        DirectoryAccess.Current.EnsureAccess(filePath);
        if (DistributionChannel.IsAppStore) Platforms.MacOS.MacSandboxNative.OpenFile(filePath);
        else await RunAsync("/usr/bin/open", filePath);
        _ = _homeWorkspace?.RecordUseAsync(filePath);
    }

    public async Task OpenFileWithAppAsync(string filePath, string bundleIdentifier)
    {
        DirectoryAccess.Current.EnsureAccess(filePath);
        if (DistributionChannel.IsAppStore) Platforms.MacOS.MacSandboxNative.OpenFile(filePath, bundleIdentifier);
        else await RunAsync("/usr/bin/open", "-b", bundleIdentifier, filePath);
        _ = _homeWorkspace?.RecordUseAsync(filePath);
    }

    public Task OpenInTerminalAsync(string directoryPath)
    {
        DistributionChannel.RequireWebsite("打开终端");
        return RunAsync("/usr/bin/open", "-a", "Terminal", directoryPath);
    }

    public Task RevealInFinderAsync(string filePath)
    {
        DirectoryAccess.Current.EnsureAccess(filePath);
        if (!DistributionChannel.IsAppStore) return RunAsync("/usr/bin/open", "-R", filePath);
        Platforms.MacOS.MacSandboxNative.RevealFile(filePath);
        return Task.CompletedTask;
    }

    public async Task OpenInEditorAsync(string path, string cliName, string bundleId)
    {
        DirectoryAccess.Current.EnsureAccess(path);
        if (DistributionChannel.SupportsSystemIntegration && !string.IsNullOrWhiteSpace(cliName))
        {
            var cliPath = new[] { "/opt/homebrew/bin", "/usr/local/bin" }
                .Select(directory => Path.Combine(directory, cliName))
                .FirstOrDefault(File.Exists);
            if (cliPath != null)
            {
                await RunAsync(cliPath, path);
                _ = _homeWorkspace?.RecordUseAsync(path);
                return;
            }
        }

        await OpenFileWithAppAsync(path, bundleId);
    }

    private static async Task RunAsync(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动 {fileName}");
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"{fileName} 退出代码 {process.ExitCode}"
                : error.Trim());
    }
}
