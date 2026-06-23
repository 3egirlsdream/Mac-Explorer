using System.Diagnostics;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacQuickLookService : IQuickLookService
{
    private Process? _previewProcess;

    public Task PreviewFileAsync(string filePath)
    {
        if (!File.Exists(filePath) && !Directory.Exists(filePath))
            return Task.CompletedTask;

        try
        {
            if (_previewProcess is { HasExited: false })
                _previewProcess.Kill();
            _previewProcess?.Dispose();

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/qlmanage",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    ArgumentList = { "-p", filePath }
                },
                EnableRaisingEvents = true
            };
            process.Exited += (_, _) =>
            {
                process.Dispose();
                if (ReferenceEquals(_previewProcess, process))
                    _previewProcess = null;
            };
            process.Start();
            _previewProcess = process;
        }
        catch
        {
            _previewProcess?.Dispose();
            _previewProcess = null;
        }
        return Task.CompletedTask;
    }
}
