using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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
            for (var i = 0; i < 1000; i++) MemoryDiagnostics.Record("probe", payload);
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
            var clock = Stopwatch.StartNew();
            using (MemoryDiagnostics.Start(root))
            {
                for (var i = 0; i < 20000; i++) MemoryDiagnostics.Record("probe", payload);
                MemoryDiagnostics.Record("memory.ui", new { sentinel = true });
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

    private static void WithDirectory(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "fk-memory-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, true); }
    }
}
