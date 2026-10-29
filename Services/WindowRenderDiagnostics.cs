using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using MacExplorer.Controls;

namespace MacExplorer.Services;

/// <summary>Observes resize races without invalidating layout or touching the render loop.</summary>
internal sealed class WindowRenderDiagnostics : IDisposable
{
    private static int _nextId;
    private readonly int _id = Interlocked.Increment(ref _nextId);
    private readonly AppWindow _window;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly byte[] _buffer = new byte[4096];
    private readonly DispatcherTimer _settled = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private long _geometryAt, _lastLogAt;
    private int _settleStep, _sequence, _mismatchSamples;
    private string _lastMismatch = "";
    private bool _disposed;

    internal WindowRenderDiagnostics(AppWindow window)
    {
        _window = window;
        window.PropertyChanged += OnPropertyChanged;
        window.ScalingChanged += OnScalingChanged;
        window.Activated += OnActivated;
        _settled.Tick += OnSettled;
        Capture("opened");
        ScheduleSettled();
    }

    internal void RecordRequest(WindowState requested, string trigger)
    {
        Capture("state-request", new { requested = requested.ToString(), trigger });
        ScheduleSettled();
    }

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            ScheduleSettled();
            Capture("state-changed", new { previous = e.OldValue?.ToString(), current = e.NewValue?.ToString() });
        }
        else if (e.Property == TopLevel.ClientSizeProperty || e.Property == Visual.BoundsProperty)
            ScheduleSettled();
        else if (e.Property == Visual.IsVisibleProperty)
        {
            Capture("visibility-changed");
            if (_window.IsVisible) ScheduleSettled();
            else _settled.Stop();
        }
    }

    private void OnScalingChanged(object? sender, EventArgs e) { ScheduleSettled(); Capture("scaling-changed"); }
    private void OnActivated(object? sender, EventArgs e) { Capture("activated"); ScheduleSettled(); }

    private void ScheduleSettled()
    {
        _geometryAt = _clock.ElapsedMilliseconds;
        _mismatchSamples = 0;
        _lastMismatch = "";
        _settleStep = 0;
        _settled.Stop();
        _settled.Interval = TimeSpan.FromMilliseconds(250);
        if (_window.IsVisible && !_disposed) _settled.Start();
    }

    private void OnSettled(object? sender, EventArgs e)
    {
        _settled.Stop();
        Capture("settled-" + ++_settleStep);
        if (_settleStep < 3 && !_disposed && _window.IsVisible)
        {
            // Samples at approximately 250 ms, 1 s and 3 s after the last geometry change.
            _settled.Interval = TimeSpan.FromMilliseconds(_settleStep == 1 ? 750 : 2000);
            _settled.Start();
        }
    }

    internal void Capture(string reason, object? request = null)
    {
        if (_disposed || !MemoryDiagnostics.IsEnabled) return;
        try
        {
            var elapsed = _clock.ElapsedMilliseconds;
            var native = ReadNative();
            var mismatches = _window.IsVisible && _window.WindowState != WindowState.Minimized
                ? FindMismatches(_window.ClientSize, _window.Bounds.Size, _window.RenderScaling, native)
                : [];
            var signature = string.Join(",", mismatches);
            _mismatchSamples = signature.Length == 0 ? 0 : signature == _lastMismatch ? _mismatchSamples + 1 : 1;
            _lastMismatch = signature;
            var persistent = _mismatchSamples >= 2 && elapsed - _geometryAt >= 1000;
            // Inspect every 30 s, but retain unchanged healthy windows only every five minutes.
            if (reason == "periodic" && mismatches.Length == 0 && elapsed - _lastLogAt < 300_000) return;
            _lastLogAt = elapsed;
            MemoryDiagnostics.Record(persistent ? "window.render_mismatch" : "window.render", new
            {
                schema = 1, windowId = _id, windowType = _window.GetType().Name, sequence = ++_sequence,
                reason, request, windowElapsedMs = elapsed, geometryAgeMs = elapsed - _geometryAt,
                state = _window.WindowState.ToString(), visible = _window.IsVisible, active = _window.IsActive,
                clientSize = SizeArray(_window.ClientSize), boundsSize = SizeArray(_window.Bounds.Size),
                contentSize = _window.Content is Control content ? SizeArray(content.Bounds.Size) : null,
                renderScaling = _window.RenderScaling, desktopScaling = _window.DesktopScaling,
                measureValid = _window.IsMeasureValid, arrangeValid = _window.IsArrangeValid,
                rootTransform = TransformArray(_window),
                contentTransform = _window.Content is Visual visual ? TransformArray(visual) : null,
                nativeStatus = native.HasValue ? "ok" : "unavailable", native,
                mismatches, mismatchSamples = _mismatchSamples, persistent
            });
        }
        catch (Exception ex)
        {
            MemoryDiagnostics.Record("window.render_sample_failed", new { windowId = _id, exception = ex.GetType().Name });
        }
    }

    private JsonElement? ReadNative()
    {
        var handle = _window.TryGetPlatformHandle();
        var view = handle is IMacOSTopLevelPlatformHandle mac ? mac.NSView
            : handle?.HandleDescriptor == "NSView" ? handle.Handle : IntPtr.Zero;
        if (!OperatingSystem.IsMacOS() || view == IntPtr.Zero) return null;
        try
        {
            var count = NativeSnapshot(view, _buffer, _buffer.Length);
            if (count <= 0) return null;
            using var json = JsonDocument.Parse(_buffer.AsMemory(0, count));
            return json.RootElement.Clone();
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    [DllImport("libMacExplorerNativeDrag", EntryPoint = "me_window_render_snapshot")]
    private static extern int NativeSnapshot(IntPtr view, [Out] byte[] buffer, int capacity);

    private static double[] SizeArray(Size size) => [size.Width, size.Height];
    private static double[] TransformArray(Visual visual)
    {
        var m = visual.RenderTransform?.Value ?? Matrix.Identity;
        return [m.M11, m.M12, m.M21, m.M22, m.M31, m.M32];
    }

    internal static string[] FindMismatches(Size client, Size bounds, double scale, JsonElement? native)
    {
        var result = new List<string>();
        static bool Different(Size a, Size b, double tolerance = 1) =>
            Math.Abs(a.Width - b.Width) > tolerance || Math.Abs(a.Height - b.Height) > tolerance;
        if (Different(client, bounds)) result.Add("managed-layout-size");
        if (native is not { } n) return result.ToArray();
        Size ReadSize(string key) => new(n.GetProperty(key)[0].GetDouble(), n.GetProperty(key)[1].GetDouble());
        if (Different(client, ReadSize("viewSize"))) result.Add("native-view-size");
        if (Different(ReadSize("viewSize"), ReadSize("viewBoundsSize"))) result.Add("view-coordinate-scale");
        if (Math.Abs(scale - n.GetProperty("windowScale").GetDouble()) > 0.01) result.Add("backing-scale");
        if (n.GetProperty("layerKind").GetString() != "none"
            && Different(ReadSize("viewBoundsSize"), ReadSize("layerSize"))) result.Add("layer-size");
        if (n.GetProperty("drawableSize").ValueKind == JsonValueKind.Array
            && Different(ReadSize("backingSize"), ReadSize("drawableSize"), 2)) result.Add("metal-drawable-size");
        return result.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Capture("closed");
        _disposed = true;
        _settled.Stop();
        _settled.Tick -= OnSettled;
        _window.PropertyChanged -= OnPropertyChanged;
        _window.ScalingChanged -= OnScalingChanged;
        _window.Activated -= OnActivated;
    }
}
