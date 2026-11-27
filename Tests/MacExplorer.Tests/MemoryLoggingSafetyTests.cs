using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Memory")]
public sealed class MemoryLoggingSafetyTests
{
    [Fact]
    public void ExpiryRunsAtStartupAndDuringIdleWithoutDeletingForeignFiles()
    {
        WithDirectory(root =>
        {
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            string ExpiredLog()
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                using var old = new RotatingMemoryLog(root, clock: clock);
                old.Write("{\"old\":true}"); old.Flush();
                var path = Directory.GetFiles(root, "memory-*.jsonl").Single();
                File.SetLastWriteTimeUtc(path, clock.GetUtcNow().UtcDateTime.AddDays(-8));
                return path;
            }
            var startupExpired = ExpiredLog();
            var foreign = Path.Combine(root, "memory-manual.jsonl"); File.WriteAllText(foreign, "{}");
            clock.Advance(TimeSpan.FromSeconds(1));
            using var log = new RotatingMemoryLog(root, clock: clock);
            Assert.False(File.Exists(startupExpired));
            log.Write("{\"current\":true}"); log.Flush();
            var current = Directory.GetFiles(root).Single(p => p != foreign);
            // Seed a closed, expired file while the current stream remains open.
            var expired = Path.Combine(root, "memory-20200101T0000000000000-123-0000.jsonl");
            File.WriteAllText(expired, "{}"); File.SetLastWriteTimeUtc(expired, clock.GetUtcNow().UtcDateTime.AddDays(-8));
            clock.Advance(TimeSpan.FromHours(2));
            log.Write("{\"idle\":true}"); log.Flush();
            Assert.False(File.Exists(expired));
            Assert.True(File.Exists(current)); Assert.True(File.Exists(foreign));
            Assert.Equal(2, log.DeletedFiles);
        });
    }

    [Fact]
    public void DailyRotationKeepsContextInEveryFile()
    {
        WithDirectory(root =>
        {
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            using (var log = new RotatingMemoryLog(root, clock: clock, headerFactory: () => "{\"context\":true}"))
            {
                log.Write("{\"first\":true}");
                clock.Advance(TimeSpan.FromHours(25));
                log.Write("{\"second\":true}");
            }
            var files = Directory.GetFiles(root);
            Assert.Equal(2, files.Length);
            Assert.All(files, p => Assert.Equal("{\"context\":true}", File.ReadLines(p).First()));
        });
    }

    [Fact]
    public void RetentionDoesNotFollowOrDeleteSymbolicLinks()
    {
        WithDirectory(root =>
        {
            var target = Path.Combine(root, "private-fixture.txt"); File.WriteAllText(target, "private-fixture-content");
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddDays(-8));
            var link = Path.Combine(root, "memory-20200101T0000000000000-123-0000.jsonl");
            File.CreateSymbolicLink(link, target);
            using (var log = new RotatingMemoryLog(root, maxFiles: 1)) log.Write("{}");
            Assert.True(File.Exists(link));
            Assert.Equal("private-fixture-content", File.ReadAllText(target));
        });
    }

    [Fact]
    public void ActiveLogsAreProtectedAndAnUnavailableBudgetStopsWriting()
    {
        WithDirectory(root =>
        {
            using var first = new RotatingMemoryLog(root, maxFiles: 1);
            first.Write("{\"active\":true}"); first.Flush();
            using var second = new RotatingMemoryLog(root, maxFiles: 1);
            Assert.Throws<IOException>(() => second.Write("{\"other\":true}"));
            Assert.Single(Directory.GetFiles(root));
            first.Write("{\"stillActive\":true}");
        });
    }

    [Fact]
    public void WriterFailureDisablesSamplingAndSkipsSerialization()
    {
        WithDirectory(root =>
        {
            var blocker = Path.Combine(root, "blocked"); File.WriteAllText(blocker, "");
            using var diagnostics = MemoryDiagnostics.Start(blocker);
            Assert.True(SpinWait.SpinUntil(() => !MemoryDiagnostics.IsEnabled, TimeSpan.FromSeconds(5)));
            var payload = new ObservedPayload();
            var payloadType = JsonSerializerOptions.Default.GetTypeInfo(typeof(ObservedPayload));
            for (var i = 0; i < 1000; i++) MemoryDiagnostics.Record("probe", payload, payloadType);
            diagnostics.Sample();
            Assert.Equal(0, payload.Reads);
        });
    }

    [Fact]
    public void EventFloodIsLimitedBeforeSerializationAndCannotDisplaceTrendRecords()
    {
        WithDirectory(root =>
        {
            var payload = new ObservedPayload();
            var payloadType = JsonSerializerOptions.Default.GetTypeInfo(typeof(ObservedPayload));
            var clock = Stopwatch.StartNew();
            using (MemoryDiagnostics.Start(root))
            {
                for (var i = 0; i < 20000; i++) MemoryDiagnostics.Record("probe", payload, payloadType);
                MemoryDiagnostics.Record("memory.ui", JsonElement.Parse("""{"sentinel":true}"""));
            }
            Assert.True(payload.Reads < 20000);
            Assert.True(payload.Reads <= MemoryDiagnostics.EventRateLimit * (Math.Ceiling(clock.Elapsed.TotalSeconds) + 1));
            var trend = Directory.GetFiles(root, "memory-trend-*.jsonl").SelectMany(File.ReadAllLines).ToArray();
            Assert.Contains(trend, line => line.Contains("\"sentinel\":true"));
            Assert.DoesNotContain(trend, line => line.Contains("\"name\":\"probe\""));
            Assert.All(Directory.GetFiles(root), file =>
            {
                using var context = JsonDocument.Parse(File.ReadLines(file).First());
                Assert.Equal("session.context", context.RootElement.GetProperty("name").GetString());
                Assert.Equal(3, context.RootElement.GetProperty("data").GetProperty("diagnosticSchema").GetInt32());
            });
        });
    }

    [Fact]
    public async Task BusyCacheStatisticsDoNotWaitForTheBusinessLock()
    {
        var cache = new WeakBitmapCache(1024);
        var sync = typeof(WeakBitmapCache).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        using var held = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var worker = Task.Run(() => { lock (sync) { held.Set(); release.Wait(TimeSpan.FromSeconds(5)); } });
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
            var state = cache.GetMemoryState();
            Assert.Equal(1, state.Skipped); Assert.Equal(0, state.Owners);
        }
        finally { release.Set(); await worker; }
        Assert.Equal(0, cache.GetMemoryState().Skipped);
    }

    [Fact]
    public void EndedLifetimeMetadataExpiresEvenIfTheOwnerIsStillAlive()
    {
        WithDirectory(root =>
        {
            var owner = new object(); var registry = new MemoryDiagnosticLifetimes();
            using (MemoryDiagnostics.Start(root))
            {
                var entry = registry.Track(owner, DiagnosticObjectKind.Window);
                entry.SetState(DiagnosticObjectState.Closed);
                entry.StateAt = Stopwatch.GetTimestamp() - (long)(TimeSpan.FromHours(25).TotalSeconds * Stopwatch.Frequency);
                registry.Sample();
            }
            var records = Directory.GetFiles(root).SelectMany(File.ReadAllLines).Select(l => JsonDocument.Parse(l)).ToArray();
            try
            {
                var sample = Assert.Single(records.Where(d => d.RootElement.GetProperty("name").GetString() == "lifetime.sample"));
                Assert.Equal(0, sample.RootElement.GetProperty("data").GetProperty("tracked").GetInt32());
                Assert.Equal(1, sample.RootElement.GetProperty("data").GetProperty("expired").GetInt64());
            }
            finally { foreach (var record in records) record.Dispose(); }
            GC.KeepAlive(owner);
        });
    }

    [AvaloniaFact]
    public void QueuedUiObservationDoesNotRunAfterDiagnosticsStop()
    {
        WithDirectory(root =>
        {
            var source = new CountingSource();
            MemoryDiagnostics.Register(source);
            using var diagnostics = MemoryDiagnostics.Start(root);
            diagnostics.Sample();
            diagnostics.Dispose();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, source.Reads);
            GC.KeepAlive(source);
        });
    }

    [Fact]
    public void WriterThreadSerializationEnforcesSizeLimitsIsolatesFailuresAndFlushesInOrder()
    {
        WithDirectory(root =>
        {
            var callerThreadId = Environment.CurrentManagedThreadId;
            var first = new ThreadCapturingPayload("first");
            var second = new ThreadCapturingPayload("second");
            var oversized = new TextPayload(new string('x', MemoryDiagnostics.MaxRecordCharacters + 512));
            var allowedTaskFailure = new TextPayload(new string('y', MemoryDiagnostics.MaxRecordCharacters + 512));
            var failing = new ThrowingPayload();

            var threadPayloadType = JsonSerializerOptions.Default.GetTypeInfo(typeof(ThreadCapturingPayload));
            var textPayloadType = JsonSerializerOptions.Default.GetTypeInfo(typeof(TextPayload));
            var throwingPayloadType = JsonSerializerOptions.Default.GetTypeInfo(typeof(ThrowingPayload));

            MemoryDiagnostics diagnostics;
            using (diagnostics = MemoryDiagnostics.Start(root))
            {
                MemoryDiagnostics.Record("probe.first", first, threadPayloadType);
                MemoryDiagnostics.Record("probe.oversized", oversized, textPayloadType);
                MemoryDiagnostics.Record("probe.throwing", failing, throwingPayloadType);
                MemoryDiagnostics.Record("task.failed", allowedTaskFailure, textPayloadType);
                MemoryDiagnostics.Record("probe.second", second, threadPayloadType);
            }

            Assert.NotEqual(0, first.SerializedOnThreadId);
            Assert.NotEqual(callerThreadId, first.SerializedOnThreadId);
            Assert.NotEqual(0, second.SerializedOnThreadId);
            Assert.NotEqual(callerThreadId, second.SerializedOnThreadId);
            Assert.Equal(1, diagnostics.OversizedCount);
            Assert.Equal(1, diagnostics.SerializationFailedCount);
            Assert.Equal(2, diagnostics.DroppedCount);

            var eventDocs = Directory.GetFiles(root, "memory-*.jsonl")
                .Where(path => !Path.GetFileName(path).StartsWith("memory-trend-", StringComparison.Ordinal))
                .SelectMany(File.ReadAllLines)
                .Select(line => JsonDocument.Parse(line))
                .ToArray();
            var trendDocs = Directory.GetFiles(root, "memory-trend-*.jsonl")
                .SelectMany(File.ReadAllLines)
                .Select(line => JsonDocument.Parse(line))
                .ToArray();
            try
            {
                var names = eventDocs.Select(doc => doc.RootElement.GetProperty("name").GetString()).ToArray();
                Assert.DoesNotContain("probe.oversized", names);
                Assert.DoesNotContain("probe.throwing", names);
                var firstDoc = Assert.Single(eventDocs, d => d.RootElement.GetProperty("name").GetString() == "probe.first");
                var taskDoc = Assert.Single(eventDocs, d => d.RootElement.GetProperty("name").GetString() == "task.failed");
                var secondDoc = Assert.Single(eventDocs, d => d.RootElement.GetProperty("name").GetString() == "probe.second");
                var seq1 = firstDoc.RootElement.GetProperty("sequence").GetInt64();
                var seq2 = taskDoc.RootElement.GetProperty("sequence").GetInt64();
                var seq3 = secondDoc.RootElement.GetProperty("sequence").GetInt64();
                Assert.True(seq1 < seq2 && seq2 < seq3);

                var stop = Assert.Single(trendDocs, d => d.RootElement.GetProperty("name").GetString() == "session.stop");
                Assert.Equal(2, stop.RootElement.GetProperty("data").GetProperty("droppedEvents").GetInt64());
            }
            finally
            {
                foreach (var doc in eventDocs) doc.Dispose();
                foreach (var doc in trendDocs) doc.Dispose();
            }
        });
    }

    [Fact]
    public void CacheMaintenanceIsolatesFailingCacheAndTrimsRemainingCaches()
    {
        WithDirectory(root =>
        {
            var before = new RecordingCache();
            var failing = new ThrowingCache();
            var after = new RecordingCache();
            MemoryCacheMaintenance.Register(before);
            MemoryCacheMaintenance.Register(failing);
            MemoryCacheMaintenance.Register(after);
            var cutoff = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            try
            {
                using (MemoryDiagnostics.Start(root))
                {
                    MemoryCacheMaintenance.TrimNow(cutoff);
                }
            }
            finally { failing.ShouldThrow = false; }

            Assert.Equal(1, before.TrimCount);
            Assert.Equal(cutoff, before.LastCutoff);
            Assert.Equal(1, failing.TrimCount);
            Assert.Equal(1, after.TrimCount);
            Assert.Equal(cutoff, after.LastCutoff);

            var eventDocs = Directory.GetFiles(root, "memory-*.jsonl")
                .Where(path => !Path.GetFileName(path).StartsWith("memory-trend-", StringComparison.Ordinal))
                .SelectMany(File.ReadAllLines)
                .Select(line => JsonDocument.Parse(line))
                .ToArray();
            try
            {
                Assert.Contains(eventDocs, doc =>
                    doc.RootElement.GetProperty("name").GetString() == "memory.trim_failed"
                    && doc.RootElement.GetProperty("data").GetProperty("source").GetString() == nameof(ThrowingCache)
                    && doc.RootElement.GetProperty("data").GetProperty("exception").GetString() == nameof(InvalidOperationException));
            }
            finally
            {
                foreach (var doc in eventDocs) doc.Dispose();
            }

            GC.KeepAlive(before);
            GC.KeepAlive(failing);
            GC.KeepAlive(after);
        });
    }

    [AvaloniaFact]
    public void PeriodicWindowRenderCaptureThrottlesHealthyAndOccludedWindowsWithoutNativeProbe()
    {
        WithDirectory(root =>
        {
            var window = new AppWindow { Width = 640, Height = 480 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            long nowMs = 1_000;
            var nativeProbes = 0;
            var occluded = false;
            try
            {
                using (MemoryDiagnostics.Start(root))
                {
                    using var probe = new WindowRenderDiagnostics(
                        window,
                        clockMs: () => nowMs,
                        readNative: () => { nativeProbes++; return null; },
                        isNativeOccluded: () => occluded);

                    // Opening capture always runs immediately.
                    Assert.Equal(1, nativeProbes);

                    // Within the 5-minute healthy interval, periodic capture must not probe native state.
                    nowMs += 30_000;
                    probe.Capture("periodic");
                    Assert.Equal(1, nativeProbes);

                    // Past the interval, an occluded window still skips the expensive native snapshot.
                    nowMs += WindowRenderDiagnostics.PeriodicHealthyIntervalMs;
                    occluded = true;
                    probe.Capture("periodic");
                    Assert.Equal(1, nativeProbes);

                    // Once unoccluded past the interval, periodic capture probes and records once.
                    occluded = false;
                    probe.Capture("periodic");
                    Assert.Equal(2, nativeProbes);

                    // Immediately after logging, another periodic tick is throttled again.
                    nowMs += 30_000;
                    probe.Capture("periodic");
                    Assert.Equal(2, nativeProbes);

                    // Non-periodic events always capture immediately regardless of healthy throttle.
                    nowMs += 5_000;
                    probe.Capture("activated");
                    Assert.Equal(3, nativeProbes);

                    // Minimized windows skip periodic capture even after the healthy interval elapses.
                    window.WindowState = WindowState.Minimized;
                    var probesAfterMinimize = nativeProbes;
                    nowMs += WindowRenderDiagnostics.PeriodicHealthyIntervalMs + 1_000;
                    probe.Capture("periodic");
                    Assert.Equal(probesAfterMinimize, nativeProbes);
                }
            }
            finally { window.Close(); }

            var records = Directory.GetFiles(root, "memory-*.jsonl")
                .Where(path => !Path.GetFileName(path).StartsWith("memory-trend-", StringComparison.Ordinal))
                .SelectMany(File.ReadAllLines)
                .Select(line => JsonDocument.Parse(line))
                .ToArray();
            try
            {
                var renderReasons = records
                    .Where(d => d.RootElement.GetProperty("name").GetString() == "window.render")
                    .Select(d => d.RootElement.GetProperty("data").GetProperty("reason").GetString())
                    .ToArray();
                Assert.Single(renderReasons, reason => reason == "periodic");
                Assert.Contains("opened", renderReasons);
                Assert.Contains("activated", renderReasons);
                Assert.Contains("closed", renderReasons);
            }
            finally
            {
                foreach (var doc in records) doc.Dispose();
            }
        });
    }

    [Fact]
    public void ThreadCountHelperReturnsPositiveCount()
    {
        using var process = Process.GetCurrentProcess();
        Assert.True(MemoryDiagnostics.ReadThreadCount(process) > 0);
    }

    private sealed class CountingSource : IMemoryDiagnosticSource
    {
        internal int Reads;
        public MemoryDiagnosticState GetMemoryState() { Reads++; return new("safety-probe", 0); }
    }

    private sealed class ObservedPayload
    {
        internal int Reads;
        public int Value { get { Interlocked.Increment(ref Reads); return 42; } }
    }

    private sealed class ThreadCapturingPayload(string label)
    {
        internal int SerializedOnThreadId;
        public string Label
        {
            get
            {
                Volatile.Write(ref SerializedOnThreadId, Environment.CurrentManagedThreadId);
                return label;
            }
        }
    }

    private sealed class TextPayload(string text)
    {
        public string Text => text;
    }

    private sealed class ThrowingPayload
    {
        public string Broken => throw new InvalidOperationException("simulated serialization failure");
    }

    private sealed class RecordingCache : IIdleMemoryCache
    {
        internal int TrimCount;
        internal DateTime LastCutoff;
        public void TrimMemory(DateTime unusedBefore)
        {
            TrimCount++;
            LastCutoff = unusedBefore;
        }
    }

    private sealed class ThrowingCache : IIdleMemoryCache
    {
        internal bool ShouldThrow = true;
        internal int TrimCount;
        public void TrimMemory(DateTime unusedBefore)
        {
            TrimCount++;
            if (ShouldThrow) throw new InvalidOperationException("simulated cache trim failure");
        }
    }

    private static void WithDirectory(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "fk-memory-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, true); }
    }
}
