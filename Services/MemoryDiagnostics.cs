using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using SkiaSharp;

namespace MacExplorer.Services;

internal readonly record struct MemoryDiagnosticState(string Kind, int Entries, long Bytes = 0, int Pending = 0, int Owners = 1, int Skipped = 0);
// Called on the UI thread: bounded counter reads only, no I/O, decoding, or
// waiting for business locks. Busy sources must report Skipped instead.
internal interface IMemoryDiagnosticSource { MemoryDiagnosticState GetMemoryState(); }

/// <summary>Bounded, local diagnostics. Never collects file names, search text or file contents.</summary>
internal sealed class MemoryDiagnostics : IDisposable
{
    private static readonly List<WeakReference<IMemoryDiagnosticSource>> Sources = [];
    private static MemoryDiagnostics? _current;
    internal const int QueueCapacity = 128, EventCapacity = 96, EventRateLimit = 128, SourceCapacity = 512;
    private readonly record struct PendingRecord(string Json, bool Summary);
    private readonly Channel<PendingRecord> _records = Channel.CreateBounded<PendingRecord>(new BoundedChannelOptions(QueueCapacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Timer _timer;
    private readonly Task _writer;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly MemoryGcDiagnostics _gcDiagnostics = new();
    private readonly object _admission = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly object _context;
    private RotatingMemoryLog? _events, _trends;
    private int _sampling, _uiPending, _disabled, _disposed, _pendingEvents, _eventCount;
    private long _eventWindow = Stopwatch.GetTimestamp(), _rateLimited, _queueFull, _oversized, _serializationFailed;
    private static long _sourcesEvicted;
    private long _lastFootprint, _dropped, _sequence, _lastAllocated, _lastSampleAt, _sampleId;
    internal static MemoryDiagnosticLifetimes Lifetimes { get; } = new();
    internal static bool IsEnabled => Volatile.Read(ref _current) is { } current && Volatile.Read(ref current._disabled) == 0;

    internal static MemoryDiagnosticLifetime? Track(object owner, DiagnosticObjectKind kind, long relatedId = 0)
        => IsEnabled ? Lifetimes.Track(owner, kind, relatedId) : null;

    internal static void Operation(DiagnosticAction action, long objectId, long relatedId = 0, int? count = null, bool? visible = null, int? mode = null)
    {
        if (!IsEnabled) return;
        Record("operation", new { action = action.ToString(), objectId, relatedId, count, visible, mode });
    }

    internal static MemoryDiagnosticOperation? BeginOperation(DiagnosticAction action, long objectId, long relatedId = 0)
        => IsEnabled ? new(action, objectId, relatedId) : null;

    private MemoryDiagnostics(string directory)
    {
        _context = new { diagnosticSchema = 3, version = typeof(App).Assembly.GetName().Version?.ToString(),
            avalonia = typeof(Avalonia.Controls.Window).Assembly.GetName().Version?.ToString(),
            channel = DistributionChannel.IsAppStore ? "AppStore" : "GitHub", runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), pid = Environment.ProcessId,
            serverGc = GCSettings.IsServerGC, latencyMode = GCSettings.LatencyMode.ToString(),
            gcConfiguration = GC.GetConfigurationVariables().Where(p => p.Value is bool or int or uint or long or ulong or double).ToDictionary(),
            policy = new { sampleSeconds = 30, queueCapacity = QueueCapacity, eventRateLimit = EventRateLimit,
                maxRecordCharacters = 8192, retentionDays = 7, fileBytes = 2 * 1024 * 1024, eventFiles = 6, trendFiles = 2,
                sourceCapacity = SourceCapacity, lifetimeCapacity = MemoryDiagnosticLifetimes.Capacity, endedLifetimeHours = 24 } };
        // A process-wide timer/writer must never root a file task through AsyncLocal.
        using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
        _timer = new Timer(_ => Sample(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        _writer = Task.Run(async () =>
        {
            try
            {
                using var events = new RotatingMemoryLog(directory, maxFiles: 6,
                    headerFactory: () => Serialize("session.context", _context));
                using var trends = new RotatingMemoryLog(directory, maxFiles: 2, prefix: "memory-trend-",
                    headerFactory: () => Serialize("session.context", _context));
                _events = events; _trends = trends;
                while (await _records.Reader.WaitToReadAsync())
                {
                    while (_records.Reader.TryRead(out var record))
                    {
                        if (!record.Summary) Interlocked.Decrement(ref _pendingEvents);
                        (record.Summary ? trends : events).Write(record.Json);
                    }
                    events.Flush(); trends.Flush();
                }
            }
            catch
            {
                Volatile.Write(ref _disabled, 1);
                _records.Writer.TryComplete();
                try { _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
                while (_records.Reader.TryRead(out var record))
                    if (!record.Summary) Interlocked.Decrement(ref _pendingEvents);
            }
        });
    }

    internal static MemoryDiagnostics Start(string directory)
    {
        var diagnostics = new MemoryDiagnostics(directory);
        Interlocked.Exchange(ref _current, diagnostics)?.Dispose();
        Record("session.start", diagnostics._context);
        return diagnostics;
    }

    internal static void Register(IMemoryDiagnosticSource source)
    {
        lock (Sources)
        {
            Sources.RemoveAll(reference => !reference.TryGetTarget(out _));
            if (Sources.Count >= SourceCapacity) { Sources.RemoveAt(0); Interlocked.Increment(ref _sourcesEvicted); }
            Sources.Add(new(source));
        }
    }

    internal static void Record(string name, object data)
    {
        var current = Volatile.Read(ref _current);
        if (current == null || Volatile.Read(ref current._disabled) != 0) return;
        current.WriteRecord(name, data);
    }

    private static bool IsSummary(string name) => name is "session.start" or "session.stop" or "memory.sample"
        or "memory.gc" or "memory.ui" or "memory.observer" or "lifetime.sample" or "memory.sample_failed" or "memory.ui_failed";

    private string Serialize(string name, object data) => JsonSerializer.Serialize(new
    {
        utc = DateTime.UtcNow, uptimeSeconds = (long)_uptime.Elapsed.TotalSeconds, sessionId = _sessionId,
        sequence = Interlocked.Increment(ref _sequence), name, data
    });

    private void Drop(ref long reason) { Interlocked.Increment(ref reason); Interlocked.Increment(ref _dropped); }

    private void WriteRecord(string name, object data)
    {
        if (Volatile.Read(ref _disabled) != 0) return;
        var summary = IsSummary(name);
        if (!summary)
        {
            lock (_admission)
            {
                if (Stopwatch.GetElapsedTime(_eventWindow).TotalSeconds >= 1)
                { _eventWindow = Stopwatch.GetTimestamp(); _eventCount = 0; }
                if (_eventCount >= EventRateLimit) { Drop(ref _rateLimited); return; }
                if (Volatile.Read(ref _pendingEvents) >= EventCapacity) { Drop(ref _queueFull); return; }
                _eventCount++;
                Interlocked.Increment(ref _pendingEvents);
            }
        }
        var queued = false;
        try
        {
            var record = Serialize(name, data);
            if (record.Length > 8192) Drop(ref _oversized);
            else if (!(queued = _records.Writer.TryWrite(new(record, summary)))) Drop(ref _queueFull);
        }
        catch { Drop(ref _serializationFailed); }
        finally { if (!summary && !queued) Interlocked.Decrement(ref _pendingEvents); }
    }

    internal void Sample()
    {
        if (!ReferenceEquals(Volatile.Read(ref _current), this) || Volatile.Read(ref _disabled) != 0) return;
        if (Interlocked.Exchange(ref _sampling, 1) != 0) return;
        try
        {
            var sampleId = ++_sampleId;
            using var process = Process.GetCurrentProcess();
            var gc = GC.GetGCMemoryInfo();
            var allocated = GC.GetTotalAllocatedBytes();
            var sampleAt = Stopwatch.GetTimestamp();
            var elapsed = _lastSampleAt == 0 ? 0 : Stopwatch.GetElapsedTime(_lastSampleAt, sampleAt).TotalSeconds;
            var allocationRate = elapsed > 0 ? (allocated - _lastAllocated) / elapsed : (double?)null;
            _lastAllocated = allocated;
            _lastSampleAt = sampleAt;
            var footprint = ReadPhysicalFootprint();
            var growth = footprint is { } value && _lastFootprint > 0 ? value - _lastFootprint : 0;
            if (footprint is { } next) _lastFootprint = next;
            Record("memory.sample", new { sampleId, rssBytes = process.WorkingSet64, physicalFootprintBytes = footprint,
                managedBytes = GC.GetTotalMemory(false), heapBytes = gc.HeapSizeBytes, fragmentedBytes = gc.FragmentedBytes,
                liveAfterGcBytes = gc.HeapSizeBytes - gc.FragmentedBytes,
                committedBytes = gc.TotalCommittedBytes, allocatedBytes = allocated,
                allocationRateBytesPerSecond = allocationRate, gcIndex = gc.Index,
                gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2),
                threads = process.Threads.Count, footprintGrowthBytes = growth,
                jitCompiledIlBytes = JitInfo.GetCompiledILBytes(), jitCompiledMethods = JitInfo.GetCompiledMethodCount(),
                skiaCpuResourceBytes = SKGraphics.GetResourceCacheTotalBytesUsed(), skiaCpuFontBytes = SKGraphics.GetFontCacheUsed(),
                diagnosticHealth = new { queued = _records.Reader.Count, eventQueueCapacity = EventCapacity,
                    rateLimited = Interlocked.Read(ref _rateLimited), queueFull = Interlocked.Read(ref _queueFull),
                    oversized = Interlocked.Read(ref _oversized), serializationFailed = Interlocked.Read(ref _serializationFailed),
                    sourcesEvicted = Interlocked.Read(ref _sourcesEvicted),
                    deletedLogs = (_events?.DeletedFiles ?? 0) + (_trends?.DeletedFiles ?? 0),
                    deletionFailures = (_events?.DeletionFailures ?? 0) + (_trends?.DeletionFailures ?? 0) },
                highMemory = footprint >= 2L * 1024 * 1024 * 1024 || growth >= 256L * 1024 * 1024,
                droppedEvents = Interlocked.Read(ref _dropped), imageDecode = BoundedImageDecoder.Statistics });
            _gcDiagnostics.Sample(sampleId);
            Lifetimes.Sample(sampleId);
            var workerMs = Stopwatch.GetElapsedTime(sampleAt).TotalMilliseconds;
            var uiQueuedAt = Stopwatch.GetTimestamp();
            if (Application.Current != null && Interlocked.Exchange(ref _uiPending, 1) == 0)
                Dispatcher.UIThread.Post(() =>
                {
                    var uiStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        if (!ReferenceEquals(Volatile.Read(ref _current), this) || Volatile.Read(ref _disabled) != 0) return;
                        var states = ReadSources();
                        var windows = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows;
                        Record("memory.ui", new { sampleId, dispatcherDelayMs = Stopwatch.GetElapsedTime(uiQueuedAt).TotalMilliseconds,
                            windows = windows?.Count, tabs = windows?.OfType<Views.MainWindow>()
                            .Sum(window => (window.DataContext as ViewModels.MainWindowViewModel)?.Tabs.Count ?? 0),
                            nativeAccessibilityFix = Platforms.MacOS.MacAutomationLifetime.NativeChildrenFixActive,
                            fontTablesPoh = TextShaping.PohTextShaping.IsActive, sources = states });
                        if (windows != null)
                            foreach (var window in windows.OfType<Controls.AppWindow>().Where(w => w.IsVisible).Take(8))
                                if (window.IsVisible) window.RenderDiagnostics?.Capture("periodic");
                    }
                    catch (Exception ex) { Record("memory.ui_failed", new { exception = ex.GetType().Name }); }
                    finally
                    {
                        if (ReferenceEquals(Volatile.Read(ref _current), this) && Volatile.Read(ref _disabled) == 0)
                            WriteRecord("memory.observer", new { sampleId, workerMs,
                                uiMs = Stopwatch.GetElapsedTime(uiStarted).TotalMilliseconds });
                        Volatile.Write(ref _uiPending, 0);
                    }
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
            .Select(group => new MemoryDiagnosticState(group.Key, group.Sum(s => s.Entries), group.Sum(s => s.Bytes),
                group.Sum(s => s.Pending), group.Sum(s => s.Owners), group.Sum(s => s.Skipped))).ToArray();
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (ReferenceEquals(Volatile.Read(ref _current), this))
        {
            Record("session.stop", new { droppedEvents = Interlocked.Read(ref _dropped) });
            Interlocked.CompareExchange(ref _current, null, this);
        }
        _timer.Dispose();
        Volatile.Write(ref _disabled, 1);
        _records.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }
}

internal sealed class RotatingMemoryLog : IDisposable
{
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private readonly string _directory, _prefix, _session;
    private readonly int _maxBytes, _maxFiles;
    private readonly TimeProvider _clock;
    private readonly Func<string>? _headerFactory;
    private FileStream? _file;
    private string? _path;
    private DateTimeOffset _openedAt, _cleanedAt;
    private long _deleted, _deletionFailures;
    private int _sequence;

    internal long DeletedFiles => Interlocked.Read(ref _deleted);
    internal long DeletionFailures => Interlocked.Read(ref _deletionFailures);

    internal RotatingMemoryLog(string directory, int maxBytes = 2 * 1024 * 1024, int maxFiles = 8,
        TimeProvider? clock = null, string prefix = "memory-", Func<string>? headerFactory = null)
    {
        _directory = Path.GetFullPath(directory); _maxBytes = maxBytes; _maxFiles = maxFiles;
        _clock = clock ?? TimeProvider.System; _prefix = prefix; _headerFactory = headerFactory;
        _session = FormattableString.Invariant($"{_clock.GetUtcNow():yyyyMMddTHHmmssfffffff}-{Environment.ProcessId}");
        Directory.CreateDirectory(directory);
        Prune(_clock.GetUtcNow(), maxFiles);
    }

    public void Write(string record)
    {
        var bytes = Encoding.UTF8.GetBytes(record + "\n");
        if (bytes.Length > _maxBytes) return;
        var now = _clock.GetUtcNow();
        if (_file == null || _file.Length + bytes.Length > _maxBytes || now - _openedAt >= TimeSpan.FromDays(1))
        {
            _file?.Dispose();
            _file = null; _path = null;
            Prune(now, _maxFiles - 1);
            var header = _headerFactory == null ? [] : Encoding.UTF8.GetBytes(_headerFactory() + "\n");
            if (header.Length + bytes.Length > _maxBytes) throw new IOException("Diagnostic record exceeds the file budget.");
            _path = Path.Combine(_directory, $"{_prefix}{_session}-{_sequence++:D4}.jsonl");
            _file = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            _openedAt = now;
            _file.Write(header);
        }
        else if (now - _cleanedAt >= TimeSpan.FromHours(1)) Prune(now, _maxFiles);
        _file.Write(bytes);
    }

    private bool IsOwned(string name)
    {
        if (!name.StartsWith(_prefix, StringComparison.Ordinal) || !name.EndsWith(".jsonl", StringComparison.Ordinal)) return false;
        var parts = name[_prefix.Length..^6].Split('-');
        return parts.Length == 3 && parts[0].Length == 22
            && DateTime.TryParseExact(parts[0], "yyyyMMdd'T'HHmmssfffffff", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _)
            && parts[1].All(char.IsAsciiDigit) && int.TryParse(parts[1], out var pid) && pid > 0
            && parts[2].Length >= 4 && parts[2].All(char.IsAsciiDigit) && int.TryParse(parts[2], out _);
    }

    private void Prune(DateTimeOffset now, int limit)
    {
        _cleanedAt = now;
        var files = new DirectoryInfo(_directory).EnumerateFiles(_prefix + "*.jsonl")
            .Where(f => IsOwned(f.Name) && (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
        foreach (var file in files.ToArray())
            if (file.FullName != _path && now.UtcDateTime - file.LastWriteTimeUtc >= Retention && Delete(file)) files.Remove(file);
        foreach (var file in files.ToArray())
        {
            if (files.Count <= limit) break;
            if (file.FullName != _path && Delete(file)) files.Remove(file);
        }
        // A locked/full directory must stop diagnostics rather than grow storage
        // indefinitely or delete another process's open log.
        if (files.Count > limit) throw new IOException("Diagnostic retention budget is unavailable.");
    }

    private bool Delete(FileInfo file)
    {
        try
        {
            using (new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.None)) { }
            file.Delete(); Interlocked.Increment(ref _deleted); return true;
        }
        catch (FileNotFoundException) { return true; }
        catch (IOException) { Interlocked.Increment(ref _deletionFailures); return false; }
        catch (UnauthorizedAccessException) { Interlocked.Increment(ref _deletionFailures); return false; }
    }

    internal void Flush() => _file?.Flush();
    public void Dispose() => _file?.Dispose();
}
