using System.Diagnostics;
using System.Text;
using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

public sealed class HomeScriptRunner
{
    internal static string BuildTerminalScript(string path, HomeScriptCommand command)
    {
        path = HomeWorkspaceService.NormalizePath(path);
        command = command.Validate();
        if (!File.Exists(path)) throw new FileNotFoundException("脚本不存在或已被移动。", path);
        var parent = Path.GetDirectoryName(path)!;
        var directory = string.IsNullOrWhiteSpace(command.WorkingDirectory) ? parent : command.WorkingDirectory;
        if (directory == "~" || directory.StartsWith("~/", StringComparison.Ordinal))
            directory = RuntimePaths.HomeDirectory + directory[1..];
        directory = HomeWorkspaceService.NormalizePath(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"工作目录不存在：{directory}");
        if (!File.Exists(command.Shell)) throw new FileNotFoundException("所选 Shell 不存在。", command.Shell);

        // Quote each value separately; only the user's command is interpreted by their chosen shell.
        // The terminal owns stdin/stdout and the process lifetime after Launch Services hands off the file.
        return $"""
            #!/bin/sh
            /bin/rm -f -- "$0"
            export SCRIPT={Quote(path)}
            export SCRIPT_DIR={Quote(parent)}
            export PATH="/opt/homebrew/bin:/usr/local/bin:$PATH"
            cd {Quote(directory)} || exit 1
            exec {Quote(command.Shell)} -c {Quote(command.Command)}

            """;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    internal static async Task<string> CreateCommandFileAsync(string path, HomeScriptCommand command, string launchDirectory)
    {
        var script = BuildTerminalScript(path, command);
        Directory.CreateDirectory(launchDirectory);
        var launchPath = Path.Combine(launchDirectory, $"MacExplorer-{Guid.NewGuid():N}.command");
        try
        {
            await using var stream = new FileStream(launchPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            });
            await stream.WriteAsync(Encoding.UTF8.GetBytes(script));
            return launchPath;
        }
        catch
        {
            File.Delete(launchPath);
            throw;
        }
    }

    internal static ProcessStartInfo BuildTerminalStartInfo(string launchPath)
    {
        var start = new ProcessStartInfo("/usr/bin/open")
        {
            UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true
        };
        // Let Launch Services choose the user's default .command handler instead of forcing Terminal.app.
        start.ArgumentList.Add(launchPath);
        return start;
    }

    public async Task RunAsync(string path, HomeScriptCommand command)
    {
        var launchPath = await CreateCommandFileAsync(path, command, RuntimePaths.TestRoot ?? Path.GetTempPath());
        try
        {
            using var process = Process.Start(BuildTerminalStartInfo(launchPath))
                ?? throw new InvalidOperationException("无法打开默认终端。");
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var error = await errorTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? "无法在默认终端中执行命令。" : error.Trim());
            // The terminal reads the file asynchronously; the wrapper removes itself when it starts.
        }
        catch
        {
            File.Delete(launchPath);
            throw;
        }
    }
}
