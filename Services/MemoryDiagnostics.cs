using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using SkiaSharp;

namespace MacExplorer.Services;

internal readonly record struct MemoryDiagnosticState(
    [property: JsonPropertyName("Kind")] string Kind,
    [property: JsonPropertyName("Entries")] int Entries,
    [property: JsonPropertyName("Bytes")] long Bytes = 0,
    [property: JsonPropertyName("Pending")] int Pending = 0,
    [property: JsonPropertyName("Owners")] int Owners = 1,
    [property: JsonPropertyName("Skipped")] int Skipped = 0);
// Called on the UI thread: bounded counter reads only, no I/O, decoding, or
// waiting for business locks. Busy sources must report Skipped instead.
internal interface IMemoryDiagnosticSource { MemoryDiagnosticState GetMemoryState(); }

/// <summary>Bounded, local diagnostics. Task failures include paths and exception details; no file contents are collected.</summary>
internal sealed class MemoryDiagnostics : IDisposable
{
    private static readonly List<WeakReference<IMemoryDiagnosticSource>> Sources = [];
    private static MemoryDiagnostics? _current;
    internal const int QueueCapacity = 128, EventCapacity = 96, EventRateLimit = 128, SourceCapacity = 512,
        MaxRecordCharacters = 8192, MaxTaskFailureRecordCharacters = 48 * 1024;
    private readonly record struct PendingRecord(
        DateTime Utc,
        long UptimeSeconds,
        long Sequence,
        string Name,
        object Data,
        JsonTypeInfo? DataType,
        bool Summary);
    private readonly Channel<PendingRecord> _records = Channel.CreateBounded<PendingRecord>(new BoundedChannelOptions(QueueCapacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Timer _timer;
    private readonly Task _writer;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly MemoryGcDiagnostics _gcDiagnostics = new();
    private readonly object _admission = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly DiagnosticSessionContext _context;
    private RotatingMemoryLog? _events, _trends;
    private int _sampling, _uiPending, _disabled, _disposed, _pendingEvents, _eventCount;
    private long _eventWindow = Stopwatch.GetTimestamp(), _rateLimited, _queueFull, _oversized, _serializationFailed;
    private static long _sourcesEvicted;
    private long _lastFootprint, _dropped, _sequence, _lastAllocated, _lastSampleAt, _sampleId;
    internal static MemoryDiagnosticLifetimes Lifetimes { get; } = new();
    internal static bool IsEnabled => Volatile.Read(ref _current) is { } current && Volatile.Read(ref current._disabled) == 0;
    internal long OversizedCount => Interlocked.Read(ref _oversized);
    internal long DroppedCount => Interlocked.Read(ref _dropped);
    internal long SerializationFailedCount => Interlocked.Read(ref _serializationFailed);

    internal static MemoryDiagnosticLifetime? Track(object owner, DiagnosticObjectKind kind, long relatedId = 0)
        => IsEnabled ? Lifetimes.Track(owner, kind, relatedId) : null;

    internal static void Operation(DiagnosticAction action, long objectId, long relatedId = 0, int? count = null, bool? visible = null, int? mode = null)
    {
        if (!IsEnabled) return;
        Record("operation", new DiagnosticOperationReport(action.ToString(), objectId, relatedId, count, visible, mode));
    }

    internal static MemoryDiagnosticOperation? BeginOperation(DiagnosticAction action, long objectId, long relatedId = 0)
        => IsEnabled ? new(action, objectId, relatedId) : null;

    private MemoryDiagnostics(string directory)
    {
        _context = new DiagnosticSessionContext(3, typeof(App).Assembly.GetName().Version?.ToString(),
            typeof(Avalonia.Controls.Window).Assembly.GetName().Version?.ToString(),
            DistributionChannel.IsAppStore ? "AppStore" : "GitHub", RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessId, GCSettings.IsServerGC, GCSettings.LatencyMode.ToString(),
            GC.GetConfigurationVariables().Where(p => p.Value is bool or int or uint or long or ulong or double).ToDictionary(),
            new DiagnosticPolicy(SampleSeconds: 30, QueueCapacity, EventRateLimit, MaxRecordCharacters,
                MaxTaskFailureRecordCharacters, RetentionDays: 7, FileBytes: 2 * 1024 * 1024,
                EventFiles: 6, TrendFiles: 2, SourceCapacity, LifetimeCapacity: MemoryDiagnosticLifetimes.Capacity,
                EndedLifetimeHours: 24));
        // A process-wide timer/writer must never root a file task through AsyncLocal.
        using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
        _timer = new Timer(_ => Sample(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        _writer = Task.Run(async () =>
        {
            try
            {
                var buffer = new ArrayBufferWriter<byte>(1024);
                using var events = new RotatingMemoryLog(directory, maxFiles: 6,
                    headerFactory: () => SerializeNow("session.context", _context));
                using var trends = new RotatingMemoryLog(directory, maxFiles: 2, prefix: "memory-trend-",
                    headerFactory: () => SerializeNow("session.context", _context));
                _events = events; _trends = trends;
                while (await _records.Reader.WaitToReadAsync())
                {
                    var wroteEvents = false;
                    var wroteTrends = false;
                    while (_records.Reader.TryRead(out var record))
                    {
                        if (!record.Summary) Interlocked.Decrement(ref _pendingEvents);
                        string json;
                        try
                        {
                            if (record.Name == "session.stop" && record.Data is DiagnosticSessionStop)
                                record = record with { Data = new DiagnosticSessionStop(Interlocked.Read(ref _dropped)) };
                            json = Serialize(record, buffer);
                            // Exception text and paths can expand sixfold when JSON-escaped.
                            var limit = record.Name is "task.create_failed" or "task.failed"
                                ? MaxTaskFailureRecordCharacters
                                : MaxRecordCharacters;
                            if (json.Length > limit)
                            {
                                Drop(ref _oversized);
                                continue;
                            }
                        }
                        catch
                        {
                            Drop(ref _serializationFailed);
                            continue;
                        }
                        finally
                        {
                            if (buffer.Capacity > 16 * 1024) buffer = new ArrayBufferWriter<byte>(1024);
                            else buffer.Clear();
                        }
                        if (record.Summary) { trends.Write(json); wroteTrends = true; }
                        else { events.Write(json); wroteEvents = true; }
                    }
                    if (wroteEvents) events.Flush();
                    if (wroteTrends) trends.Flush();
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

    internal static void Record(string name, object data, JsonTypeInfo? dataType = null)
    {
        var current = Volatile.Read(ref _current);
        if (current == null || Volatile.Read(ref current._disabled) != 0) return;
        current.WriteRecord(name, data, dataType);
    }

    private static bool IsSummary(string name) => name is "session.start" or "session.stop" or "memory.sample"
        or "memory.gc" or "memory.ui" or "memory.observer" or "lifetime.sample" or "memory.sample_failed" or "memory.ui_failed";

    private string SerializeNow(string name, object data, JsonTypeInfo? dataType = null)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        return Serialize(new PendingRecord(
            DateTime.UtcNow, (long)_uptime.Elapsed.TotalSeconds,
            Interlocked.Increment(ref _sequence), name, data, dataType, Summary: true), buffer);
    }

    private string Serialize(in PendingRecord record, ArrayBufferWriter<byte> buffer)
    {
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("utc", record.Utc);
        writer.WriteNumber("uptimeSeconds", record.UptimeSeconds);
        writer.WriteString("sessionId", _sessionId);
        writer.WriteNumber("sequence", record.Sequence);
        writer.WriteString("name", record.Name);
        writer.WritePropertyName("data");
        JsonSerializer.Serialize(writer, record.Data,
            record.DataType ?? DiagnosticsJsonContext.Default.Options.GetTypeInfo(record.Data.GetType()));
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private void Drop(ref long reason) { Interlocked.Increment(ref reason); Interlocked.Increment(ref _dropped); }

    private void WriteRecord(string name, object data, JsonTypeInfo? dataType = null)
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
        var pending = new PendingRecord(DateTime.UtcNow, (long)_uptime.Elapsed.TotalSeconds,
            Interlocked.Increment(ref _sequence), name, data, dataType, summary);
        if (!_records.Writer.TryWrite(pending))
        {
            Drop(ref _queueFull);
            if (!summary) Interlocked.Decrement(ref _pendingEvents);
        }
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
            var health = new DiagnosticHealth(_records.Reader.Count, EventCapacity,
                Interlocked.Read(ref _rateLimited), Interlocked.Read(ref _queueFull),
                Interlocked.Read(ref _oversized), Interlocked.Read(ref _serializationFailed),
                Interlocked.Read(ref _sourcesEvicted),
                (_events?.DeletedFiles ?? 0) + (_trends?.DeletedFiles ?? 0),
                (_events?.DeletionFailures ?? 0) + (_trends?.DeletionFailures ?? 0));
            Record("memory.sample", new MemorySample(sampleId,
                RssBytes: process.WorkingSet64, PhysicalFootprintBytes: footprint,
                ManagedBytes: GC.GetTotalMemory(false), HeapBytes: gc.HeapSizeBytes, FragmentedBytes: gc.FragmentedBytes,
                LiveAfterGcBytes: gc.HeapSizeBytes - gc.FragmentedBytes, CommittedBytes: gc.TotalCommittedBytes,
                AllocatedBytes: allocated, AllocationRateBytesPerSecond: allocationRate, GcIndex: gc.Index,
                Gen0: GC.CollectionCount(0), Gen1: GC.CollectionCount(1), Gen2: GC.CollectionCount(2),
                Threads: ReadThreadCount(process), FootprintGrowthBytes: growth,
                JitCompiledIlBytes: JitInfo.GetCompiledILBytes(), JitCompiledMethods: JitInfo.GetCompiledMethodCount(),
                SkiaCpuResourceBytes: SKGraphics.GetResourceCacheTotalBytesUsed(), SkiaCpuFontBytes: SKGraphics.GetFontCacheUsed(),
                DiagnosticHealth: health,
                HighMemory: footprint >= 2L * 1024 * 1024 * 1024 || growth >= 256L * 1024 * 1024,
                DroppedEvents: Interlocked.Read(ref _dropped), ImageDecode: BoundedImageDecoder.Statistics));
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
                        int? tabCount = null;
                        if (windows != null)
                        {
                            var tabs = 0;
                            for (var i = 0; i < windows.Count; i++)
                                if (windows[i] is Views.MainWindow mainWindow
                                    && mainWindow.DataContext is ViewModels.MainWindowViewModel vm)
                                    tabs += vm.Tabs.Count;
                            tabCount = tabs;
                        }
                        Record("memory.ui", new MemoryUiSample(sampleId,
                            Stopwatch.GetElapsedTime(uiQueuedAt).TotalMilliseconds, windows?.Count, tabCount,
                            Platforms.MacOS.MacAutomationLifetime.NativeChildrenFixActive,
                            TextShaping.PohTextShaping.IsActive, states));
                        if (windows != null)
                        {
                            var captured = 0;
                            for (var i = 0; i < windows.Count && captured < 8; i++)
                            {
                                if (windows[i] is Controls.AppWindow appWindow
                                    && appWindow.IsVisible
                                    && appWindow.WindowState != Avalonia.Controls.WindowState.Minimized)
                                {
                                    captured++;
                                    appWindow.RenderDiagnostics?.Capture("periodic");
                                }
                            }
                        }
                    }
                    catch (Exception ex) { Record("memory.ui_failed", new DiagnosticFailure(ex.GetType().Name)); }
                    finally
                    {
                        if (ReferenceEquals(Volatile.Read(ref _current), this) && Volatile.Read(ref _disabled) == 0)
                            WriteRecord("memory.observer", new MemoryObserverSample(sampleId, workerMs,
                                Stopwatch.GetElapsedTime(uiStarted).TotalMilliseconds));
                        Volatile.Write(ref _uiPending, 0);
                    }
                }, DispatcherPriority.Background);
        }
        catch (Exception ex) { Record("memory.sample_failed", new DiagnosticFailure(ex.GetType().Name)); }
        finally { Volatile.Write(ref _sampling, 0); }
    }

    internal static MemoryDiagnosticState[] ReadSources()
    {
        List<IMemoryDiagnosticSource>? live = null;
        lock (Sources)
        {
            if (Sources.Count == 0) return [];
            var write = 0;
            for (var i = 0; i < Sources.Count; i++)
            {
                var reference = Sources[i];
                if (reference.TryGetTarget(out var source))
                {
                    Sources[write++] = reference;
                    (live ??= new List<IMemoryDiagnosticSource>(Sources.Count)).Add(source);
                }
            }
            if (write < Sources.Count) Sources.RemoveRange(write, Sources.Count - write);
        }
        if (live == null || live.Count == 0) return [];
        var grouped = new Dictionary<string, MemoryDiagnosticState>(StringComparer.Ordinal);
        for (var i = 0; i < live.Count; i++)
        {
            var source = live[i];
            MemoryDiagnosticState state;
            try { state = source.GetMemoryState(); }
            catch (Exception ex)
            {
                Record("memory.source_failed", new MemorySourceFailure(source.GetType().Name, ex.GetType().Name));
                continue;
            }
            if (grouped.TryGetValue(state.Kind, out var existing))
            {
                grouped[state.Kind] = new MemoryDiagnosticState(
                    state.Kind,
                    existing.Entries + state.Entries,
                    existing.Bytes + state.Bytes,
                    existing.Pending + state.Pending,
                    existing.Owners + state.Owners,
                    existing.Skipped + state.Skipped);
            }
            else
            {
                grouped[state.Kind] = state;
            }
        }
        if (grouped.Count == 0) return [];
        var result = new MemoryDiagnosticState[grouped.Count];
        grouped.Values.CopyTo(result, 0);
        return result;
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

    [DllImport("libMacExplorerNativeDrag", EntryPoint = "me_memory_thread_count")]
    private static extern int NativeThreadCount();
    internal static int ReadThreadCount(Process process)
    {
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var count = NativeThreadCount();
                if (count >= 0) return count;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
        return process.Threads.Count;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (ReferenceEquals(Volatile.Read(ref _current), this))
        {
            Record("session.stop", new DiagnosticSessionStop(Interlocked.Read(ref _dropped)));
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
