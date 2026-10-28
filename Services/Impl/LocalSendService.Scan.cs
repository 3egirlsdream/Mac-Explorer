using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using MacExplorer.Services;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public sealed partial class LocalSendService
{
    internal readonly record struct ScanInterface(string Address, int Prefix, string Scope);
    internal sealed record VerifiedPeerHistory(string Fingerprint, string Address, int Port, string Alias,
        string[] Scopes, long VerifiedUtcTicks);
    internal Func<IReadOnlyList<ScanInterface>>? ScanInterfacesOverride;
    internal Func<string, int, CancellationToken, Task<bool>>? TcpProbeOverride;

    private IReadOnlyList<ScanInterface> CurrentScanInterfaces()
        => ScanInterfacesOverride?.Invoke() ?? PhysicalScanInterfaces();

    private static IReadOnlyList<ScanInterface> PhysicalScanInterfaces()
    {
        var result = new List<ScanInterface>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType is not
                (NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                    or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx)) continue;
            var properties = network.GetIPProperties();
            var gateways = properties.GatewayAddresses.Select(item => item.Address)
                .Where(item => item.AddressFamily == AddressFamily.InterNetwork && !item.Equals(IPAddress.Any))
                .Select(item => item.ToString()).OrderBy(item => item, StringComparer.Ordinal).ToArray();
            // The macOS VM bridges also report Ethernet, but have no gateway.
            if (gateways.Length == 0) continue;
            foreach (var address in properties.UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address.Address)
                    || address.PrefixLength is < 1 or > 32) continue;
                var ip = address.Address.GetAddressBytes();
                if (ip[0] is 0 or >= 224) continue;
                var networkAddress = ToUInt32(address.Address) & (uint.MaxValue << (32 - address.PrefixLength));
                var scope = $"{network.Id}|{ToAddress(networkAddress)}/{address.PrefixLength}|{string.Join(',', gateways)}";
                result.Add(new ScanInterface(address.Address.ToString(), address.PrefixLength, scope));
            }
        }
        return result.Distinct().Take(3).ToArray();
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static string ToAddress(uint address)
        => $"{(address >> 24) & 255}.{(address >> 16) & 255}.{(address >> 8) & 255}.{address & 255}";

    private static bool IsPrivate16(uint address)
    {
        var first = address >> 24;
        var second = (address >> 16) & 255;
        return first == 10 || (first == 172 && second is >= 16 and <= 31) || (first == 192 && second == 168);
    }

    private static IEnumerable<string> SubnetAddresses(string subnet)
    {
        var (network, prefix, _) = ParseScanSubnet(subnet);
        var count = 1 << (32 - prefix);
        for (var index = 0; index < count; index++)
        {
            if (prefix < 31 && (index == 0 || index == count - 1)) continue;
            yield return ToAddress(network + (uint)index);
        }
    }

    internal static IReadOnlyList<VerifiedPeerHistory> MatchingHistory(
        IEnumerable<VerifiedPeerHistory> history, IReadOnlySet<string> currentScopes)
        => history.Where(item => item.Scopes.Any(currentScopes.Contains))
            .OrderByDescending(item => item.VerifiedUtcTicks).Take(8).ToArray();

    internal static IReadOnlyList<string> FastScanCandidates(
        IReadOnlyList<ScanInterface> interfaces, IReadOnlyList<VerifiedPeerHistory> history, string optionalSubnet)
    {
        var subnets = new List<string>();
        foreach (var peer in history.Take(2))
        {
            if (!IPAddress.TryParse(peer.Address, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                continue;
            try { subnets.Add(ParseScanSubnet($"{address}/24").Normalized); }
            catch (ArgumentException) { }
        }
        if (!string.IsNullOrWhiteSpace(optionalSubnet))
        {
            try { subnets.Add(ParseScanSubnet(optionalSubnet).Normalized); }
            catch (ArgumentException) { }
        }
        foreach (var network in interfaces)
            subnets.Add(ParseScanSubnet($"{network.Address}/{Math.Max(24, network.Prefix)}").Normalized);
        var self = interfaces.Select(item => item.Address).ToHashSet(StringComparer.Ordinal);
        return subnets.Distinct(StringComparer.Ordinal).Take(6)
            .SelectMany(SubnetAddresses).Where(address => !self.Contains(address))
            .Distinct(StringComparer.Ordinal).Take(1536).ToArray();
    }

    internal static IReadOnlyList<string> ExpandedScanCandidates(
        IReadOnlyList<ScanInterface> interfaces, IReadOnlyList<VerifiedPeerHistory> history, int maxCandidates = 65536)
        => ExpandedScanStream(interfaces, history).Take(maxCandidates).ToArray();

    private static IEnumerable<string> ExpandedScanStream(
        IReadOnlyList<ScanInterface> interfaces, IReadOnlyList<VerifiedPeerHistory> history)
    {
        var privateAddresses = interfaces.Select(item => IPAddress.TryParse(item.Address, out var parsed) ? parsed : null)
            .Where(item => item is { AddressFamily: AddressFamily.InterNetwork } && IsPrivate16(ToUInt32(item)))
            .Select(item => ToUInt32(item!)).Distinct().ToArray();
        var groups = privateAddresses.Select(address => address & 0xffff0000u).Distinct().ToArray();
        if (groups.Length == 0) yield break;

        var preferred = new List<IEnumerator<uint>>();
        foreach (var peer in history)
        {
            if (!IPAddress.TryParse(peer.Address, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                continue;
            var value = ToUInt32(address);
            if (groups.Contains(value & 0xffff0000u))
                preferred.Add(Range(value & 0xffffff00u, 256).GetEnumerator());
        }
        foreach (var address in privateAddresses)
            preferred.Add(NearbySubnetAddresses(address, 16).GetEnumerator());
        var remaining = privateAddresses.Select(address => NearbySubnetAddresses(address, 256).GetEnumerator()).ToList();
        var seen = new HashSet<uint>();
        var self = privateAddresses.ToHashSet();
        foreach (var address in AppendRoundRobin(preferred)) yield return address;
        foreach (var address in AppendRoundRobin(remaining)) yield return address;

        IEnumerable<string> AppendRoundRobin(List<IEnumerator<uint>> streams)
        {
            try
            {
                while (streams.Count > 0)
                    for (var index = 0; index < streams.Count;)
                    {
                        if (!streams[index].MoveNext())
                        {
                            streams[index].Dispose();
                            streams.RemoveAt(index);
                            continue;
                        }
                        var address = streams[index].Current;
                        if (seen.Add(address) && !self.Contains(address)) yield return ToAddress(address);
                        index++;
                    }
            }
            finally
            {
                foreach (var stream in streams) stream.Dispose();
            }
        }
    }

    private static IEnumerable<uint> NearbySubnetAddresses(uint address, int subnetCount)
    {
        var firstSubnet = (int)((address >> 8) & 255) & ~(subnetCount - 1);
        var localSubnet = (int)((address >> 8) & 255);
        var subnets = Enumerable.Range(firstSubnet, subnetCount)
            .OrderBy(subnet => Math.Abs(subnet - localSubnet)).ThenBy(subnet => subnet).ToArray();
        var network = address & 0xffff0000u;
        // Visit each nearby subnet before advancing the host offset, rather than
        // exhausting thousands of lower addresses before reaching a neighbour.
        for (uint host = 1; host <= 254; host++)
            foreach (var subnet in subnets)
                yield return network | ((uint)subnet << 8) | host;
        foreach (var subnet in subnets)
        {
            yield return network | ((uint)subnet << 8);
            yield return network | ((uint)subnet << 8) | 255;
        }
    }

    private static IEnumerable<uint> Range(uint start, int count)
    {
        for (var index = 0; index < count; index++) yield return start + (uint)index;
    }

    private IReadOnlyList<VerifiedPeerHistory> RelevantHistory(IReadOnlyList<ScanInterface> interfaces)
    {
        var scopes = interfaces.Select(item => item.Scope).ToHashSet(StringComparer.Ordinal);
        lock (_historyLock) return MatchingHistory(LoadHistory(), scopes);
    }

    private List<VerifiedPeerHistory> LoadHistory()
    {
        if (_verifiedHistory != null) return _verifiedHistory;
        var raw = _settings.Get(ILocalSendService.VerifiedPeersKey, "");
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 32 * 1024) return _verifiedHistory = [];
        try
        {
            var entries = JsonSerializer.Deserialize<List<VerifiedPeerHistory>>(raw, Json) ?? [];
            return _verifiedHistory = entries.Where(item => item != null && IsValidHistory(item))
                .OrderByDescending(item => item.VerifiedUtcTicks).Take(8).ToList();
        }
        catch (JsonException) { return _verifiedHistory = []; }
    }

    private static bool IsValidHistory(VerifiedPeerHistory item)
        => item.Scopes is { Length: > 0 and <= 3 } && item.Port is >= 1 and <= 65535
            && item.Fingerprint is { Length: 64 } && item.Fingerprint.All(Uri.IsHexDigit)
            && !string.IsNullOrWhiteSpace(item.Alias) && item.VerifiedUtcTicks > 0
            && IPAddress.TryParse(item.Address, out var address) && address.AddressFamily == AddressFamily.InterNetwork;

    private void RememberVerifiedPeer(LocalSendDevice peer, DiscoveryState state,
        CancellationToken cancellationToken, DateTime attemptStarted)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!RememberPeer(peer, state, attemptStarted)) return;
        lock (_discoveryLock)
        {
            if (!ReferenceEquals(_discovery, state) || state.Token.IsCancellationRequested) return;
            foreach (var key in state.Candidates.Keys.Where(key => key.StartsWith(peer.Fingerprint + "@", StringComparison.OrdinalIgnoreCase)).ToArray())
                state.Candidates.Remove(key);
        }
        if (!Enabled || _running is not { IsCancellationRequested: false } || peer.Protocol != "https"
            || peer.Fingerprint.Length != 64 || !peer.Fingerprint.All(Uri.IsHexDigit)) return;
        var scopes = (ScanInterfacesOverride?.Invoke() ?? state.Interfaces).Select(item => item.Scope).Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal).ToArray();
        if (scopes.Length == 0) return;
        var now = UtcNow.Ticks;
        lock (_discoveryLock)
        {
            if (!ReferenceEquals(_discovery, state) || state.Token.IsCancellationRequested) return;
            lock (_historyLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var history = LoadHistory().ToList();
                var index = history.FindIndex(item => item.Fingerprint.Equals(peer.Fingerprint, StringComparison.OrdinalIgnoreCase)
                    && item.Scopes.Any(scopes.Contains));
                var previous = index >= 0 ? history[index] : null;
                if (previous != null && previous.Address == peer.Address && previous.Port == peer.Port
                    && previous.Alias == peer.Alias && previous.Scopes.SequenceEqual(scopes)
                    && now - previous.VerifiedUtcTicks < TimeSpan.FromDays(1).Ticks) return;
                if (index >= 0) history.RemoveAt(index);
                history.Insert(0, new VerifiedPeerHistory(peer.Fingerprint, peer.Address, peer.Port, peer.Alias, scopes, now));
                var updated = history.OrderByDescending(item => item.VerifiedUtcTicks).Take(8).ToList();
                cancellationToken.ThrowIfCancellationRequested();
                _settings.Set(ILocalSendService.VerifiedPeersKey, JsonSerializer.Serialize(updated, Json));
                _verifiedHistory = updated;
            }
        }
    }

    private async Task<bool?> TcpPortOpenAsync(string address, int port, CancellationToken cancellationToken)
    {
        await _tcpProbeGate.WaitAsync(cancellationToken);
        RecordPeak(ref _peakTcp, Interlocked.Increment(ref _tcpInFlight));
        Interlocked.Increment(ref _unknownProbes);
        try
        {
            try
            {
                if (TcpProbeOverride is { } probe)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromMilliseconds(250));
                    return await probe(address, port, timeout.Token);
                }
                var error = await LocalSendTcpProbe.ConnectAsync(new IPEndPoint(IPAddress.Parse(address), port),
                    TimeSpan.FromMilliseconds(250), cancellationToken, _timeProvider);
                return error == SocketError.TimedOut ? null : error == SocketError.Success;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { return null; }
            catch (SocketException ex) { return ex.SocketErrorCode == SocketError.TimedOut ? null : false; }
        }
        finally
        {
            Interlocked.Decrement(ref _tcpInFlight);
            _tcpProbeGate.Release();
        }
    }

    private Task ScanCandidatesAsync(DiscoveryState state, IEnumerable<string> addresses, int port,
        TimeSpan verifyTimeout, CancellationToken cancellationToken)
    {
        return ScanCandidatesAsync(state, EnumerateAsync(), port, verifyTimeout, cancellationToken);

        async IAsyncEnumerable<string> EnumerateAsync()
        {
            foreach (var address in addresses) yield return address;
            await Task.CompletedTask;
        }
    }

    private async Task ScanCandidatesAsync(DiscoveryState state, IAsyncEnumerable<string> addresses, int port,
        TimeSpan verifyTimeout, CancellationToken cancellationToken, bool discoveryOnly = false)
    {
        var openAddresses = Channel.CreateBounded<string>(new BoundedChannelOptions(16)
        {
            SingleWriter = false, SingleReader = false, FullMode = BoundedChannelFullMode.Wait
        });
        var probing = ProbeAsync();
        var verifying = Parallel.ForEachAsync(openAddresses.Reader.ReadAllAsync(cancellationToken),
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken },
            async (address, token) =>
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                attempt.CancelAfter(verifyTimeout);
                try { await ConnectByAddressCoreAsync(state, address, port, attempt.Token, discoveryOnly); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger?.LogDebug(ex, "LocalSend scan skipped {Address}", address); }
            });
        await Task.WhenAll(probing, verifying);

        async Task ProbeAsync()
        {
            Exception? error = null;
            try
            {
                await Parallel.ForEachAsync(addresses,
                    new ParallelOptions { MaxDegreeOfParallelism = TcpScanConcurrency, CancellationToken = cancellationToken },
                    async (address, token) =>
                    {
                        await RateLimitUnknownAsync(state, token);
                        var open = await TcpPortOpenAsync(address, port, token);
                        if (open == null)
                        {
                            // One paced retry covers a lost SYN or a phone waking its radio.
                            await RateLimitUnknownAsync(state, token);
                            open = await TcpPortOpenAsync(address, port, token);
                        }
                        if (open == true) await openAddresses.Writer.WriteAsync(address, token);
                    });
            }
            catch (Exception ex) { error = ex; throw; }
            finally { openAddresses.Writer.TryComplete(error); }
        }
    }
}
