using System.Diagnostics;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacQuickLookService : IQuickLookService, IDisposable
{
    private Process? _previewProcess;
    public bool IsOpen => DistributionChannel.IsAppStore ? Platforms.MacOS.MacSandboxNative.QuickLookVisible : _previewProcess != null;

    public Task PreviewFileAsync(string filePath)
    {
        MacExplorer.Services.DirectoryAccess.Current.EnsureAccess(filePath);
        if (!File.Exists(filePath) && !Directory.Exists(filePath))
            return Task.CompletedTask;

        if (DistributionChannel.IsAppStore)
            return Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => Platforms.MacOS.MacSandboxNative.QuickLook(filePath)).GetTask();

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
        if (DistributionChannel.IsAppStore) { Avalonia.Threading.Dispatcher.UIThread.Post(Platforms.MacOS.MacSandboxNative.CloseQuickLook); return; }
        var process = _previewProcess;
        _previewProcess = null;
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }
}
