using System.Diagnostics;
using FKFinder.Services;

namespace FKFinder.Platforms.MacCatalyst.Services;

public class MacQuickLookService : IQuickLookService
{
    private Process? _qlProcess;

    public Task PreviewFileAsync(string filePath)
    {
        if (!File.Exists(filePath) && !Directory.Exists(filePath))
            return Task.CompletedTask;

        try
        {
            // Kill previous qlmanage process if still running
            if (_qlProcess is { HasExited: false })
            {
                _qlProcess.Kill();
                _qlProcess.Dispose();
                _qlProcess = null;
            }

            _qlProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/qlmanage",
                    Arguments = $"-p \"{filePath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            _qlProcess.Exited += (_, _) =>
            {
                _qlProcess?.Dispose();
                _qlProcess = null;
            };

            _qlProcess.Start();
        }
        catch { }

        return Task.CompletedTask;
    }
}
