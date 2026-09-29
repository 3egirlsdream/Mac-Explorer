using System.Net;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public sealed partial class LocalSendService
{
    private sealed class CandidateState(LocalSendDevice device)
    {
        public LocalSendDevice Device = device;
        public DateTime NextAttempt;
        public DateTime NormalNextAttempt;
        public DateTime LastTouched = DateTime.UtcNow;
        public DateTime LastSuccess;
        public long HistoryVerifiedUtcTicks;
        public int Failures;
        public bool InFlight;
    }

    private sealed class DiscoveryState(CancellationTokenSource cancellation,
        IReadOnlyList<ScanInterface> interfaces, IReadOnlyList<VerifiedPeerHistory> history)
    {
        public CancellationToken Token { get; } = cancellation.Token;
        public readonly IReadOnlyList<ScanInterface> Interfaces = interfaces;
        public readonly IReadOnlyList<VerifiedPeerHistory> History = history;
        public readonly Dictionary<string, CandidateState> Candidates = new(StringComparer.OrdinalIgnoreCase);
        public readonly SemaphoreSlim Signal = new(0, 1);
        public readonly object RateLock = new();
        public long NextUnknownProbeTicks;
        public DateTime LastRefresh;
        public DateTime LastPrune;
        public bool AnnounceRequested;
        public int ExternalOperations;
        public TaskCompletionSource ExternalIdle = CompletedIdle();
        public Task ReceiveTask = Task.CompletedTask;
        public Task CoordinatorTask = Task.CompletedTask;
        public Task Completion => Task.WhenAll(ReceiveTask, CoordinatorTask, ExternalIdle.Task);

        private static TaskCompletionSource CompletedIdle()
        {
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            idle.SetResult();
            return idle;
        }

        public void Wake()
        {
            try { Signal.Release(); }
            catch (SemaphoreFullException) { }
        }

    }

    private IDisposable TrackExternalOperation(DiscoveryState state)
    {
        lock (_discoveryLock)
        {
            if (!ReferenceEquals(_discovery, state) || state.Token.IsCancellationRequested)
                throw new OperationCanceledException(state.Token);
            if (state.ExternalOperations++ == 0)
                state.ExternalIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return new ExternalOperation(this, state);
    }

    private sealed class ExternalOperation(LocalSendService owner, DiscoveryState state) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._discoveryLock)
                if (--state.ExternalOperations == 0) state.ExternalIdle.TrySetResult();
        }
    }

    private void RefreshKnown(DiscoveryState state)
    {
        lock (_discoveryLock)
        {
            if (!ReferenceEquals(_discovery, state) || DateTime.UtcNow - state.LastRefresh < TimeSpan.FromSeconds(15)) return;
            state.LastRefresh = DateTime.UtcNow;
            foreach (var candidate in state.Candidates.Values)
                if (!candidate.InFlight)
                {
                    candidate.NextAttempt = DateTime.UtcNow;
                    candidate.NormalNextAttempt = candidate.NextAttempt;
                }
            state.Wake();
        }
    }

    private void QueueCandidate(DiscoveryState state, LocalSendDevice peer, long historyVerifiedUtcTicks = 0)
    {
        if (peer.Protocol is not ("https" or "http") || peer.Port is < 1 or > 65535
            || !ValidPeerFingerprint(peer.Protocol, peer.Fingerprint)
            || string.IsNullOrWhiteSpace(peer.Alias) || peer.Alias.Length > 256
            || !IPAddress.TryParse(peer.Address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || peer.Fingerprint.Equals(_fingerprint, StringComparison.OrdinalIgnoreCase)) return;
        lock (_discoveryLock)
        {
            if (!ReferenceEquals(_discovery, state) || state.Token.IsCancellationRequested) return;
            var wake = false;
            var key = peer.Fingerprint;
            if ((_peers.TryGetValue(peer.Fingerprint, out var verified)
                    && (verified.Device.Address != peer.Address || verified.Device.Port != peer.Port))
                || (state.Candidates.TryGetValue(key, out var current) && current.InFlight
                    && (current.Device.Address != peer.Address || current.Device.Port != peer.Port)))
                key += "@" + peer.Address + ":" + peer.Port;
            if (state.Candidates.TryGetValue(key, out var existing))
            {
                wake = existing.Device.Address != peer.Address || existing.Device.Port != peer.Port;
                existing.Device = peer;
                existing.LastTouched = DateTime.UtcNow;
                existing.HistoryVerifiedUtcTicks = Math.Max(existing.HistoryVerifiedUtcTicks, historyVerifiedUtcTicks);
            }
            else
            {
                if (state.Candidates.Count >= 128)
                {
                    var evict = state.Candidates.Where(item => !item.Value.InFlight
                        && Priority(item.Value) <= (historyVerifiedUtcTicks > 0 ? 1 : 0))
                        .OrderBy(item => Priority(item.Value))
                        .ThenByDescending(item => item.Value.Failures)
                        .ThenBy(item => item.Value.LastTouched).FirstOrDefault();
                    if (evict.Key != null) state.Candidates.Remove(evict.Key);
                }
                if (state.Candidates.Count < 128)
                {
                    state.Candidates[key] = new CandidateState(peer)
                    { HistoryVerifiedUtcTicks = historyVerifiedUtcTicks };
                    wake = true;
                }
            }
            if (wake) state.Wake();
        }
    }

    private static int Priority(CandidateState candidate)
        => candidate.LastSuccess != DateTime.MinValue ? 2 : candidate.HistoryVerifiedUtcTicks > 0 ? 1 : 0;

    private static long VerifiedTicks(CandidateState candidate)
        => candidate.LastSuccess != DateTime.MinValue ? candidate.LastSuccess.Ticks : candidate.HistoryVerifiedUtcTicks;

    private static CandidateState[] FastKnownCandidates(DiscoveryState state)
        => state.Candidates.Values.Where(item => VerifiedTicks(item) > 0)
            .OrderByDescending(VerifiedTicks).Take(8).ToArray();

    private static bool FastKnown(DiscoveryState state, CandidateState candidate)
        => FastKnownCandidates(state).Contains(candidate);

    private LocalSendDevice[] Snapshot(DiscoveryState state)
    {
        lock (_discoveryLock)
        {
            if (!ReferenceEquals(_discovery, state) || state.Token.IsCancellationRequested) return [];
            var now = DateTime.UtcNow;
            foreach (var key in _peers.Where(item => now - item.Value.Seen > TimeSpan.FromSeconds(75))
                .Select(item => item.Key).ToArray()) _peers.Remove(key);
            return _peers.Values.OrderByDescending(item => item.Seen)
                .Select(item => item.Device).ToArray();
        }
    }

    private async Task RunDiscoveryAsync(DiscoveryState state)
    {
        try
        {
            foreach (var item in state.History)
                QueueCandidate(state, new LocalSendDevice(item.Alias, item.Fingerprint, item.Address, item.Port, "https"),
                    item.VerifiedUtcTicks);
            await AnnounceAsync(state.Token);
            var fast = FastScanCandidates(state.Interfaces, state.History, ScanSubnet);
            var fastSet = fast.ToHashSet(StringComparer.Ordinal);
            IEnumerator<string> NewRound() => fast.Concat(ExpandedScanStream(state.Interfaces, state.History)
                    .Where(address => !fastSet.Contains(address)))
                .Take(65536).GetEnumerator();
            using var scanOwner = new ScanRound(NewRound());
            var roundComplete = false;
            var cooldownUntil = DateTime.MinValue;
            var scanResumeAt = DateTime.MinValue;
            while (!state.Token.IsCancellationRequested)
            {
                PruneCandidates(state);
                bool announce;
                lock (_discoveryLock)
                {
                    announce = state.AnnounceRequested;
                    state.AnnounceRequested = false;
                }
                if (announce) await AnnounceAsync(state.Token);
                await ProbeDueCandidatesAsync(state);
                if (state.Interfaces.Count == 0 || _sending != null || _incoming != null
                    || (roundComplete && DateTime.UtcNow < cooldownUntil)
                    || DateTime.UtcNow < scanResumeAt)
                {
                    await WaitForDiscoveryAsync(state, TimeSpan.FromSeconds(state.Interfaces.Count == 0 ? 30 : 5));
                    continue;
                }
                if (roundComplete)
                {
                    scanOwner.Reset(NewRound());
                    roundComplete = false;
                }
                var activeUntil = DateTime.UtcNow + TimeSpan.FromSeconds(20);
                while (DateTime.UtcNow < activeUntil && !state.Token.IsCancellationRequested
                       && _sending == null && _incoming == null)
                {
                    var batch = new List<string>(4);
                    while (batch.Count < 4 && scanOwner.MoveNext()) batch.Add(scanOwner.Current);
                    if (batch.Count == 0)
                    {
                        roundComplete = true;
                        cooldownUntil = DateTime.UtcNow + TimeSpan.FromMinutes(5);
                        break;
                    }
                    await ScanCandidatesAsync(state, batch, DiscoveryPort, TimeSpan.FromSeconds(3), state.Token);
                    await ProbeDueCandidatesAsync(state);
                    await Task.Yield();
                }
                if (!roundComplete)
                {
                    scanResumeAt = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                    await WaitForDiscoveryAsync(state, TimeSpan.FromSeconds(5));
                }
            }
        }
        catch (OperationCanceledException) when (state.Token.IsCancellationRequested) { }
        catch (Exception ex) { _logger?.LogWarning(ex, "LocalSend background discovery stopped"); }
    }

    private async Task ProbeDueCandidatesAsync(DiscoveryState state)
    {
        List<(CandidateState Item, LocalSendDevice Peer)> due;
        lock (_discoveryLock)
        {
            if (!ReferenceEquals(_discovery, state)) return;
            var now = DateTime.UtcNow;
            var fast = FastKnownCandidates(state);
            foreach (var item in fast)
                if (item.NextAttempt > now + TimeSpan.FromSeconds(5))
                    item.NextAttempt = now + TimeSpan.FromSeconds(5);
            foreach (var item in state.Candidates.Values)
                if (!fast.Contains(item) && item.NextAttempt < item.NormalNextAttempt)
                    item.NextAttempt = item.NormalNextAttempt;
            var candidates = state.Candidates.Values.Where(item => !item.InFlight && item.NextAttempt <= now)
                .OrderByDescending(item => fast.Contains(item))
                .ThenByDescending(Priority)
                .ThenByDescending(VerifiedTicks)
                .ThenByDescending(item => item.LastTouched)
                .Take(4).ToList();
            foreach (var item in candidates) item.InFlight = true;
            due = candidates.Select(item => (item, item.Device)).ToList();
        }
        if (due.Count == 0) return;
        await Task.WhenAll(due.Select(async entry =>
        {
            var item = entry.Item;
            var peer = entry.Peer;
            var started = DateTime.UtcNow;
            var success = await ProbePeerAsync(state, peer, state.Token);
            lock (_discoveryLock)
            {
                if (!ReferenceEquals(_discovery, state)) return;
                item.InFlight = false;
                if (item.Device.Address != peer.Address || item.Device.Port != peer.Port
                    || (_peers.TryGetValue(peer.Fingerprint, out var replacement)
                        && replacement.Seen > started
                        && (replacement.Device.Address != peer.Address || replacement.Device.Port != peer.Port)))
                {
                    state.Wake();
                    return;
                }
                item.Failures = success ? 0 : Math.Min(item.Failures + 1, 5);
                var fastKnown = FastKnown(state, item);
                var normalSeconds = success ? 60 : item.Failures switch
                {
                    1 => 15, 2 => 30, 3 => 60, 4 => 120, _ => 300
                };
                var now = DateTime.UtcNow;
                item.NormalNextAttempt = now + TimeSpan.FromSeconds(normalSeconds + Random.Shared.Next(0, 4));
                item.NextAttempt = fastKnown ? now + TimeSpan.FromSeconds(5) : item.NormalNextAttempt;
                if (!success && _peers.TryGetValue(peer.Fingerprint, out var online)
                    && online.Device.Address == peer.Address && online.Device.Port == peer.Port)
                    _peers.Remove(peer.Fingerprint);
            }
        }));
    }

    private void PruneCandidates(DiscoveryState state)
    {
        lock (_discoveryLock)
        {
            var now = DateTime.UtcNow;
            if (!ReferenceEquals(_discovery, state) || now - state.LastPrune < TimeSpan.FromMinutes(1)) return;
            state.LastPrune = now;
            foreach (var key in state.Candidates.Where(item => !item.Value.InFlight
                    && Priority(item.Value) == 0 && now - item.Value.LastTouched > TimeSpan.FromMinutes(10))
                .Select(item => item.Key).ToArray()) state.Candidates.Remove(key);
        }
    }

    private async Task WaitForDiscoveryAsync(DiscoveryState state, TimeSpan duration)
    {
        lock (_discoveryLock)
        {
            if (!ReferenceEquals(_discovery, state)) return;
            var earliest = state.Candidates.Values
                .Where(item => !item.InFlight)
                .Select(item => item.NextAttempt).DefaultIfEmpty(DateTime.MaxValue).Min();
            if (earliest != DateTime.MaxValue)
            {
                var untilKnown = earliest - DateTime.UtcNow;
                duration = TimeSpan.FromTicks(Math.Min(duration.Ticks, Math.Max(0, untilKnown.Ticks)));
            }
        }
        await state.Signal.WaitAsync(duration, state.Token);
    }

    private static async Task RateLimitUnknownAsync(DiscoveryState state, CancellationToken token)
    {
        long start;
        lock (state.RateLock)
        {
            var now = Stopwatch.GetTimestamp();
            start = Math.Max(now, state.NextUnknownProbeTicks);
            state.NextUnknownProbeTicks = start + Stopwatch.Frequency * 16 / 1000;
        }
        var delay = TimeSpan.FromSeconds((start - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
        if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
    }

    private static bool ValidPeerFingerprint(string protocol, string? fingerprint)
        => protocol == "https"
            ? fingerprint is { Length: 64 } && fingerprint.All(Uri.IsHexDigit)
            : !string.IsNullOrWhiteSpace(fingerprint) && fingerprint.Length <= 128;

    private static bool ValidAnnouncementFingerprint(string? fingerprint)
        => !string.IsNullOrWhiteSpace(fingerprint) && fingerprint.Length <= 128;

    private sealed class ScanRound(IEnumerator<string> iterator) : IDisposable
    {
        private IEnumerator<string> _iterator = iterator;
        public string Current => _iterator.Current;
        public bool MoveNext() => _iterator.MoveNext();
        public void Reset(IEnumerator<string> next)
        {
            _iterator.Dispose();
            _iterator = next;
        }
        public void Dispose() => _iterator.Dispose();
    }
}
