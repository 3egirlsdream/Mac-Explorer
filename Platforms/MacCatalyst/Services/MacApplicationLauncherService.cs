using System.Diagnostics;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacApplicationLauncherService : IApplicationLauncherService
{
    public Task OpenFileAsync(string filePath) => RunAsync("/usr/bin/open", filePath);

    public Task OpenFileWithAppAsync(string filePath, string bundleIdentifier)
        => RunAsync("/usr/bin/open", "-b", bundleIdentifier, filePath);

    public Task OpenInTerminalAsync(string directoryPath)
        => RunAsync("/usr/bin/open", "-a", "Terminal", directoryPath);

    public Task RevealInFinderAsync(string filePath)
        => RunAsync("/usr/bin/open", "-R", filePath);

    public async Task OpenInEditorAsync(string path, string cliName, string bundleId)
    {
        if (!string.IsNullOrWhiteSpace(cliName))
        {
            var cliPath = new[] { "/opt/homebrew/bin", "/usr/local/bin" }
                .Select(directory => Path.Combine(directory, cliName))
                .FirstOrDefault(File.Exists);
            if (cliPath != null)
            {
                await RunAsync(cliPath, path);
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
