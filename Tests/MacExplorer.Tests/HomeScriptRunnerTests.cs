using System.Diagnostics;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

public sealed class HomeScriptRunnerTests
{
    [Theory]
    [InlineData("/bin/sh")]
    [InlineData("/bin/bash")]
    [InlineData("/bin/zsh")]
    public async Task TerminalWrapperPreservesPathsWorkingDirectoryInputAndExitCode(string shell)
    {
        var directory = Directory.CreateTempSubdirectory("MacExplorer_HomeTerminal_");
        try
        {
            var path = Path.Combine(directory.FullName, "script ' $(echo INJECTED) 中文.sh");
            var workingDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "work ' $(echo NO)\nnext"));
            await File.WriteAllTextAsync(path, """
                read -r answer
                printf 'path:%s\nparent:%s\nwork:%s\ninput:%s\n' "$SCRIPT" "$SCRIPT_DIR" "$PWD" "$answer"
                printf 'error-output\n' >&2
                exit 7
                """);
            var command = HomeScriptCommand.Create(path) with
            {
                Shell = shell, WorkingDirectory = workingDirectory.FullName,
                Command = "printf '%s\\n' \"custom ' command\"\n/bin/sh \"$SCRIPT\""
            };
            var launchPath = await HomeScriptRunner.CreateCommandFileAsync(path, command, directory.FullName);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(launchPath));
            var start = new ProcessStartInfo(launchPath)
            {
                UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var process = Process.Start(start)!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                var outputTask = process.StandardOutput.ReadToEndAsync(deadline.Token);
                var errorTask = process.StandardError.ReadToEndAsync(deadline.Token);
                await process.StandardInput.WriteLineAsync("interactive input");
                process.StandardInput.Close();
                await process.WaitForExitAsync(deadline.Token);
                var output = await outputTask;
                Assert.Equal(7, process.ExitCode);
                Assert.Contains("custom ' command", output);
                Assert.Contains("path:" + path, output);
                Assert.Contains("parent:" + directory.FullName, output);
                Assert.Contains("work:" + workingDirectory.FullName, output);
                Assert.Contains("input:interactive input", output);
                Assert.Contains("error-output", await errorTask);
                Assert.False(File.Exists(launchPath));
                Assert.True(File.Exists(path));
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void LaunchUsesTheDefaultCommandHandlerWithoutCapturingTerminalInputOrOutput()
    {
        var start = HomeScriptRunner.BuildTerminalStartInfo("/tmp/a ' command.command");
        Assert.Equal("/usr/bin/open", start.FileName);
        Assert.Equal("/tmp/a ' command.command", Assert.Single(start.ArgumentList));
        Assert.False(start.RedirectStandardInput);
        Assert.False(start.RedirectStandardOutput);
    }

    [Fact]
    public void InvalidCommandOrWorkingDirectoryIsRejectedBeforeProcessStart()
    {
        var directory = Directory.CreateTempSubdirectory("MacExplorer_HomeValidation_");
        try
        {
            var path = Path.Combine(directory.FullName, "test.sh");
            File.WriteAllText(path, "echo unused");
            var valid = HomeScriptCommand.Create(path) with { Shell = "/bin/sh" };
            Assert.Throws<ArgumentException>(() => HomeScriptRunner.BuildTerminalScript(path, valid with { Shell = "/bin/unknown" }));
            Assert.Throws<ArgumentException>(() => HomeScriptRunner.BuildTerminalScript(path, valid with { Command = "\0" }));
            Assert.Throws<DirectoryNotFoundException>(() => HomeScriptRunner.BuildTerminalScript(path,
                valid with { WorkingDirectory = Path.Combine(directory.FullName, "missing") }));
            Assert.Throws<FileNotFoundException>(() => HomeScriptRunner.BuildTerminalScript(path + ".missing", valid));
        }
        finally { directory.Delete(recursive: true); }
    }
}
