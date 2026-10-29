using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace MacExplorer.Services;

internal readonly record struct MemoryDiagnosticState(string Kind, int Entries, long Bytes = 0, int Pending = 0, int Owners = 1);
internal interface IMemoryDiagnosticSource { MemoryDiagnosticState GetMemoryState(); }

/// <summary>Bounded, local diagnostics. Never collects file names, search text or file contents.</summary>
internal sealed class MemoryDiagnostics : IDisposable
{
    private static readonly List<WeakReference<IMemoryDiagnosticSource>> Sources = [];
    private static MemoryDiagnostics? _current;
    private readonly Channel<string> _records = Channel.CreateBounded<string>(new BoundedChannelOptions(128)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Timer _timer;
    private readonly Task _writer;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private int _sampling, _uiPending;
    private long _lastFootprint, _dropped;
    internal static bool IsEnabled => Volatile.Read(ref _current) != null;

    private MemoryDiagnostics(string directory)
    {
        // A process-wide timer/writer must never root a file task through AsyncLocal.
        using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
        _writer = Task.Run(async () =>
        {
            try
            {
                using var log = new RotatingMemoryLog(directory);
                await foreach (var record in _records.Reader.ReadAllAsync()) log.Write(record);
            }
            catch { /* Diagnostics must not stop the application, including on a full disk. */ }
        });
        _timer = new Timer(_ => Sample(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
    }

    internal static MemoryDiagnostics Start(string directory)
    {
        var diagnostics = new MemoryDiagnostics(directory);
        Interlocked.Exchange(ref _current, diagnostics)?.Dispose();
        Record("session.start", new { version = typeof(App).Assembly.GetName().Version?.ToString(),
            avalonia = typeof(Avalonia.Controls.Window).Assembly.GetName().Version?.ToString(),
            channel = DistributionChannel.IsAppStore ? "AppStore" : "GitHub", runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), pid = Environment.ProcessId });
        return diagnostics;
    }

    internal static void Register(IMemoryDiagnosticSource source)
    {
        lock (Sources)
        {
            Sources.RemoveAll(reference => !reference.TryGetTarget(out _));
            Sources.Add(new(source));
        }
    }

    internal static void Record(string name, object data)
    {
        var current = Volatile.Read(ref _current);
        if (current == null) return;
        try
        {
            var record = JsonSerializer.Serialize(new { utc = DateTime.UtcNow, uptimeSeconds = (long)current._uptime.Elapsed.TotalSeconds, name, data });
            if (record.Length > 8192 || !current._records.Writer.TryWrite(record)) Interlocked.Increment(ref current._dropped);
        }
        catch { }
    }

    internal void Sample()
    {
        if (Interlocked.Exchange(ref _sampling, 1) != 0) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            var gc = GC.GetGCMemoryInfo();
            var footprint = ReadPhysicalFootprint();
            var growth = footprint is { } value && _lastFootprint > 0 ? value - _lastFootprint : 0;
            if (footprint is { } next) _lastFootprint = next;
            Record("memory.sample", new { rssBytes = process.WorkingSet64, physicalFootprintBytes = footprint,
                managedBytes = GC.GetTotalMemory(false), heapBytes = gc.HeapSizeBytes, fragmentedBytes = gc.FragmentedBytes,
                committedBytes = gc.TotalCommittedBytes, allocatedBytes = GC.GetTotalAllocatedBytes(),
                gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2),
                threads = process.Threads.Count, footprintGrowthBytes = growth,
                highMemory = footprint >= 2L * 1024 * 1024 * 1024 || growth >= 256L * 1024 * 1024,
                droppedEvents = Interlocked.Read(ref _dropped), imageDecode = BoundedImageDecoder.Statistics });
            var uiQueuedAt = Stopwatch.GetTimestamp();
            if (Application.Current != null && Interlocked.Exchange(ref _uiPending, 1) == 0)
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        var states = ReadSources();
                        var windows = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows;
                        Record("memory.ui", new { dispatcherDelayMs = Stopwatch.GetElapsedTime(uiQueuedAt).TotalMilliseconds,
                            windows = windows?.Count, tabs = windows?.OfType<Views.MainWindow>()
                            .Sum(window => (window.DataContext as ViewModels.MainWindowViewModel)?.Tabs.Count ?? 0),
                            nativeAccessibilityFix = Platforms.MacOS.MacAutomationLifetime.NativeChildrenFixActive, sources = states });
                        if (windows != null)
                            foreach (var window in windows.OfType<Controls.AppWindow>())
                                if (window.IsVisible) window.RenderDiagnostics?.Capture("periodic");
                    }
                    finally { Volatile.Write(ref _uiPending, 0); }
                }, DispatcherPriority.Background);
        }
        catch (Exception ex) { Record("memory.sample_failed", new { exception = ex.GetType().Name }); }
        finally { Volatile.Write(ref _sampling, 0); }
    }

    internal static MemoryDiagnosticState[] ReadSources()
    {
        IMemoryDiagnosticSource[] live;
        lock (Sources)
        {
            Sources.RemoveAll(reference => !reference.TryGetTarget(out _));
            live = Sources.Select(reference => reference.TryGetTarget(out var source) ? source : null).OfType<IMemoryDiagnosticSource>().ToArray();
        }
        var states = new List<MemoryDiagnosticState>();
        foreach (var source in live)
        {
            try { states.Add(source.GetMemoryState()); }
            catch (Exception ex) { Record("memory.source_failed", new { source = source.GetType().Name, exception = ex.GetType().Name }); }
        }
        return states.GroupBy(state => state.Kind)
            .Select(group => new MemoryDiagnosticState(group.Key, group.Sum(s => s.Entries), group.Sum(s => s.Bytes), group.Sum(s => s.Pending), group.Sum(s => s.Owners))).ToArray();
    }

    [DllImport("libMacExplorerNativeDrag", EntryPoint = "me_memory_footprint")]
    private static extern long NativeFootprint();
    internal static long? ReadPhysicalFootprint()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try { var bytes = NativeFootprint(); return bytes < 0 ? null : bytes; }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    public void Dispose()
    {
        if (ReferenceEquals(Volatile.Read(ref _current), this))
        {
            Record("session.stop", new { droppedEvents = Interlocked.Read(ref _dropped) });
            Interlocked.CompareExchange(ref _current, null, this);
        }
        _timer.Dispose();
        _records.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }
}

internal sealed class RotatingMemoryLog(string directory, int maxBytes = 2 * 1024 * 1024, int maxFiles = 8) : IDisposable
{
    private readonly string _session = $"{DateTime.UtcNow:yyyyMMddTHHmmssfffffff}-{Environment.ProcessId}";
    private FileStream? _file;
    private int _sequence;

    public void Write(string record)
    {
        var bytes = Encoding.UTF8.GetBytes(record + "\n");
        if (bytes.Length > maxBytes) return;
        if (_file == null || _file.Length + bytes.Length > maxBytes)
        {
            _file?.Dispose();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"memory-{_session}-{_sequence++:D4}.jsonl");
            _file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            foreach (var old in Directory.EnumerateFiles(directory, "memory-*.jsonl").OrderByDescending(Path.GetFileName).Skip(maxFiles))
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        _file.Write(bytes);
        _file.Flush();
    }

    public void Dispose() => _file?.Dispose();
}
