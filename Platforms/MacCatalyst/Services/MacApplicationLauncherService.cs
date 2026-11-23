using System.Diagnostics;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacApplicationLauncherService : IApplicationLauncherService
{
    private readonly HomeWorkspaceService? _homeWorkspace;

    public MacApplicationLauncherService(HomeWorkspaceService? homeWorkspace = null)
        => _homeWorkspace = homeWorkspace;

    public Task OpenFileAsync(string filePath)
        => OpenFilesAsync([filePath]);

    public async Task OpenFilesAsync(IReadOnlyList<string> filePaths)
    {
        var validPaths = filePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToArray();
        if (validPaths.Length == 0) return;
        foreach (var path in validPaths)
        {
            DirectoryAccess.Current.EnsureAccess(path);
            _ = _homeWorkspace?.RecordUseAsync(path);
        }

        if (DistributionChannel.IsAppStore)
        {
            Platforms.MacOS.MacSandboxNative.OpenFiles(validPaths);
        }
        else
        {
            await RunAsync("/usr/bin/open", validPaths);
        }
    }

    public Task OpenFileWithAppAsync(string filePath, string bundleIdentifier)
        => OpenFilesWithAppAsync([filePath], bundleIdentifier);

    public async Task OpenFilesWithAppAsync(IReadOnlyList<string> filePaths, string bundleIdentifier)
    {
        var validPaths = filePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToArray();
        if (validPaths.Length == 0) return;
        foreach (var path in validPaths)
        {
            DirectoryAccess.Current.EnsureAccess(path);
            _ = _homeWorkspace?.RecordUseAsync(path);
        }

        if (DistributionChannel.IsAppStore)
        {
            Platforms.MacOS.MacSandboxNative.OpenFiles(validPaths, bundleIdentifier);
        }
        else
        {
            var args = new string[validPaths.Length + 2];
            args[0] = "-b";
            args[1] = bundleIdentifier;
            Array.Copy(validPaths, 0, args, 2, validPaths.Length);
            await RunAsync("/usr/bin/open", args);
        }
    }

    public Task OpenInTerminalAsync(string directoryPath)
    {
        DistributionChannel.RequireWebsite("打开终端");
        return RunAsync("/usr/bin/open", "-a", "Terminal", directoryPath);
    }

    public Task RevealInFinderAsync(string filePath)
        => RevealInFinderAsync([filePath]);

    public Task RevealInFinderAsync(IReadOnlyList<string> filePaths)
    {
        var validPaths = filePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToArray();
        if (validPaths.Length == 0) return Task.CompletedTask;
        foreach (var path in validPaths)
            DirectoryAccess.Current.EnsureAccess(path);

        if (!DistributionChannel.IsAppStore)
        {
            var args = new string[validPaths.Length + 1];
            args[0] = "-R";
            Array.Copy(validPaths, 0, args, 1, validPaths.Length);
            return RunAsync("/usr/bin/open", args);
        }

        Platforms.MacOS.MacSandboxNative.RevealFiles(validPaths);
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
