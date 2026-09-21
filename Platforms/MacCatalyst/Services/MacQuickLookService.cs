using System.Diagnostics;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacQuickLookService : IQuickLookService, IDisposable
{
    private Process? _previewProcess;
    public bool IsOpen => _previewProcess != null;

    public Task PreviewFileAsync(string filePath)
    {
        if (!File.Exists(filePath) && !Directory.Exists(filePath))
            return Task.CompletedTask;

        try
        {
            Dispose();

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
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_previewProcess, process)) _previewProcess = null;
                    process.Dispose();
                });
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

    public void Dispose()
    {
        var process = _previewProcess;
        _previewProcess = null;
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }
}
