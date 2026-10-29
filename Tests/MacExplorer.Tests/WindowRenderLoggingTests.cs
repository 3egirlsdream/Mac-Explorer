using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Services;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Window")]
public sealed class WindowRenderLoggingTests
{
    [AvaloniaFact]
    public void StateTransitionsAreRecordedWithoutWindowTitlesAndStopAfterClose()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var root = Path.Combine(Path.GetTempPath(), "window-render-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var log = MemoryDiagnostics.Start(root))
            {
                var window = new AppWindow { Width = 640, Height = 480, Title = "private-filename.txt" };
                window.Show();
                var probe = Assert.IsType<WindowRenderDiagnostics>(window.RenderDiagnostics);
                probe.RecordRequest(WindowState.Maximized, "titlebar-double-click");
                window.WindowState = WindowState.Maximized;
                window.Hide();
                window.Show();
                Assert.Same(probe, window.RenderDiagnostics);
                window.WindowState = WindowState.Normal;
                window.Close();
                Assert.Null(window.RenderDiagnostics);
                probe.Capture("must-not-appear");
                Dispatcher.UIThread.RunJobs();
            }
            var text = string.Join("\n", Directory.GetFiles(root, "memory-*.jsonl").Select(File.ReadAllText));
            Assert.DoesNotContain("private-filename.txt", text);
            Assert.DoesNotContain("must-not-appear", text);
            var records = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .Where(e => e.GetProperty("name").GetString() == "window.render").ToArray();
            Assert.Single(records, e => e.GetProperty("data").GetProperty("reason").GetString() == "opened");
            Assert.Contains(records, e => e.GetProperty("data").GetProperty("reason").GetString() == "state-request");
            Assert.Contains(records, e => e.GetProperty("data").GetProperty("reason").GetString() == "state-changed");
            Assert.Equal("closed", records.Last().GetProperty("data").GetProperty("reason").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
