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
    internal const long PeriodicHealthyIntervalMs = 300_000;
    private readonly long _id;
    private readonly AppWindow _window;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Func<long>? _clockMs;
    private readonly Func<JsonElement?>? _readNative;
    private readonly Func<bool>? _isNativeOccluded;
    private readonly DispatcherTimer _settled = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private byte[]? _buffer;
    private long _geometryAt, _lastLogAt;
    private int _settleStep, _sequence, _mismatchSamples;
    private string _lastMismatch = "";
    private bool _hasLogged, _disposed;

    internal WindowRenderDiagnostics(
        AppWindow window,
        Func<long>? clockMs = null,
        Func<JsonElement?>? readNative = null,
        Func<bool>? isNativeOccluded = null)
    {
        _id = window.DiagnosticId;
        _window = window;
        _clockMs = clockMs;
        _readNative = readNative;
        _isNativeOccluded = isNativeOccluded;
        window.PropertyChanged += OnPropertyChanged;
        window.ScalingChanged += OnScalingChanged;
        window.Activated += OnActivated;
        _settled.Tick += OnSettled;
        Capture("opened");
        ScheduleSettled();
    }

    private long ElapsedMs => _clockMs?.Invoke() ?? _clock.ElapsedMilliseconds;

    internal void RecordRequest(WindowState requested, string trigger)
    {
        Capture("state-request", new WindowStateRequest(requested.ToString(), trigger));
        ScheduleSettled();
    }

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            ScheduleSettled();
            Capture("state-changed", new WindowStateChange(e.OldValue?.ToString(), e.NewValue?.ToString()));
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
        if (!MemoryDiagnostics.IsEnabled) { _settled.Stop(); return; }
        _geometryAt = ElapsedMs;
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
            var elapsed = ElapsedMs;
            if (reason == "periodic")
            {
                if (!_window.IsVisible || _window.WindowState == WindowState.Minimized) return;
                if (_mismatchSamples == 0 && !Different(_window.ClientSize, _window.Bounds.Size))
                {
                    // Skip native AppKit/CoreAnimation JSON capture during the 5-minute healthy window or while occluded.
                    if (_hasLogged && elapsed - _lastLogAt < PeriodicHealthyIntervalMs) return;
                    if (IsNativeOccluded()) return;
                }
            }
            var native = ReadNative();
            var mismatches = _window.IsVisible && _window.WindowState != WindowState.Minimized
                ? FindMismatches(_window.ClientSize, _window.Bounds.Size, _window.RenderScaling, native)
                : [];
            var signature = mismatches.Length == 0 ? "" : string.Join(",", mismatches);
            _mismatchSamples = signature.Length == 0 ? 0 : signature == _lastMismatch ? _mismatchSamples + 1 : 1;
            _lastMismatch = signature;
            var persistent = _mismatchSamples >= 2 && elapsed - _geometryAt >= 1000;
            if (reason == "periodic" && mismatches.Length == 0)
            {
                if (_hasLogged && elapsed - _lastLogAt < PeriodicHealthyIntervalMs) return;
                if (native is { } n && n.TryGetProperty("occluded", out var occluded) && occluded.ValueKind == JsonValueKind.True)
                    return;
            }
            _hasLogged = true;
            _lastLogAt = elapsed;
            MemoryDiagnostics.Record(persistent ? "window.render_mismatch" : "window.render", new WindowRenderReport(1,
                _id, _window.GetType().Name, ++_sequence, reason, request, elapsed, elapsed - _geometryAt,
                _window.WindowState.ToString(), _window.IsVisible, _window.IsActive, SizeArray(_window.ClientSize),
                SizeArray(_window.Bounds.Size),
                _window.Content is Control content ? SizeArray(content.Bounds.Size) : null, _window.RenderScaling,
                _window.DesktopScaling, _window.IsMeasureValid, _window.IsArrangeValid, TransformArray(_window),
                _window.Content is Visual visual ? TransformArray(visual) : null,
                native.HasValue ? "ok" : "unavailable", native, mismatches, _mismatchSamples, persistent));
        }
        catch (Exception ex)
        {
            MemoryDiagnostics.Record("window.render_sample_failed", new WindowRenderFailure(_id, ex.GetType().Name));
        }
    }

    private IntPtr GetNativeView()
    {
        if (!OperatingSystem.IsMacOS()) return IntPtr.Zero;
        var handle = _window.TryGetPlatformHandle();
        return handle is IMacOSTopLevelPlatformHandle mac ? mac.NSView
            : handle?.HandleDescriptor == "NSView" ? handle.Handle : IntPtr.Zero;
    }

    private bool IsNativeOccluded()
    {
        if (_isNativeOccluded != null) return _isNativeOccluded();
        var view = GetNativeView();
        if (view == IntPtr.Zero) return false;
        try { return NativeIsOccluded(view) == 1; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private JsonElement? ReadNative()
    {
        if (_readNative != null) return _readNative();
        var view = GetNativeView();
        if (view == IntPtr.Zero) return null;
        try
        {
            _buffer ??= new byte[4096];
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

    [DllImport("libMacExplorerNativeDrag", EntryPoint = "me_window_is_occluded")]
    private static extern int NativeIsOccluded(IntPtr view);

    private static bool Different(Size a, Size b, double tolerance = 1) =>
        Math.Abs(a.Width - b.Width) > tolerance || Math.Abs(a.Height - b.Height) > tolerance;

    private static double[] SizeArray(Size size) => [size.Width, size.Height];
    private static double[] TransformArray(Visual visual)
    {
        var m = visual.RenderTransform?.Value ?? Matrix.Identity;
        return [m.M11, m.M12, m.M21, m.M22, m.M31, m.M32];
    }

    internal static string[] FindMismatches(Size client, Size bounds, double scale, JsonElement? native)
    {
        List<string>? result = null;
        void Add(string mismatch) => (result ??= new List<string>(2)).Add(mismatch);
        if (Different(client, bounds)) Add("managed-layout-size");
        if (native is not { } n) return result?.ToArray() ?? [];
        Size ReadSize(string key) => new(n.GetProperty(key)[0].GetDouble(), n.GetProperty(key)[1].GetDouble());
        if (Different(client, ReadSize("viewSize"))) Add("native-view-size");
        if (Different(ReadSize("viewSize"), ReadSize("viewBoundsSize"))) Add("view-coordinate-scale");
        if (Math.Abs(scale - n.GetProperty("windowScale").GetDouble()) > 0.01) Add("backing-scale");
        if (n.GetProperty("layerKind").GetString() != "none"
            && Different(ReadSize("viewBoundsSize"), ReadSize("layerSize"))) Add("layer-size");
        if (n.GetProperty("drawableSize").ValueKind == JsonValueKind.Array
            && Different(ReadSize("backingSize"), ReadSize("drawableSize"), 2)) Add("metal-drawable-size");
        return result?.ToArray() ?? [];
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
