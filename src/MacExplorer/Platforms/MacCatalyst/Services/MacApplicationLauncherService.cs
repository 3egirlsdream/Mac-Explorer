using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

/// <summary>
/// Mac Catalyst implementation of IApplicationLauncherService.
/// Uses NSWorkspace-equivalent APIs via Process to launch applications.
/// </summary>
public class MacApplicationLauncherService : IApplicationLauncherService
{
    public async Task OpenFileAsync(string filePath)
    {
        await RunProcessAsync("open", $"\"{filePath}\"");
    }

    public async Task OpenFileWithAppAsync(string filePath, string bundleIdentifier)
    {
        await RunProcessAsync("open", $"-b \"{bundleIdentifier}\" \"{filePath}\"");
    }

    public async Task OpenInTerminalAsync(string directoryPath)
    {
        // Use osascript via stdin to open Terminal and cd to the directory
        var escapedPath = directoryPath.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var script = $"tell application \"Terminal\"\nactivate\ndo script \"cd \" & quoted form of \"{escapedPath}\"\nend tell";
        await RunOsascriptAsync(script);
    }

    public async Task OpenInVsCodeAsync(string directoryPath)
    {
        await OpenInEditorAsync(directoryPath, "code", "com.microsoft.VSCode");
    }

    public async Task OpenInEditorAsync(string path, string cliName, string bundleId)
    {
        var cliPath = $"/usr/local/bin/{cliName}";
        if (!File.Exists(cliPath))
            cliPath = $"/opt/homebrew/bin/{cliName}";

        if (File.Exists(cliPath))
        {
            await RunProcessAsync(cliPath, $"\"{path}\"");
        }
        else
        {
            await RunProcessAsync("open", $"-b {bundleId} \"{path}\"");
        }
    }

    public async Task RevealInFinderAsync(string path)
    {
        await RunProcessAsync("open", $"-R \"{path}\"");
    }

    private static async Task RunOsascriptAsync(string script)
    {
        await Task.Run(() =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "osascript",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    process.StandardInput.Write(script);
                    process.StandardInput.Close();
                    process.WaitForExit(5000);
                }
            }
            catch { }
        });
    }

    private static async Task RunProcessAsync(string fileName, string arguments)
    {
        await Task.Run(() =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                var process = System.Diagnostics.Process.Start(psi);
                process?.WaitForExit(5000);
            }
            catch { }
        });
    }
}
