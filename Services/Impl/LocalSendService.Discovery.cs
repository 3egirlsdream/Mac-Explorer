using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public sealed partial class LocalSendService
{
    private const int DiscoveryResponseLimit = 64 * 1024;
    internal int? DiscoveryUdpPortOverride;

    private async Task StartDiscoveryAsync()
    {
        var interfaces = await Task.Run(CurrentScanInterfaces);
        var history = RelevantHistory(interfaces);
        var state = new DiscoveryState(_networkDiscovery, interfaces, history);
        lock (_discoveryLock) _discovery = state;
        var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            try
            {
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryUdpPortOverride ?? DiscoveryPort));
                var joined = 0;
                foreach (var address in ActiveIPv4Addresses())
                {
                    try { udp.JoinMulticastGroup(DiscoveryGroup, address); joined++; }
                    catch (SocketException ex) { _logger?.LogDebug(ex, "LocalSend multicast join failed on {Address}", address); }
                }
                if (joined == 0) udp.JoinMulticastGroup(DiscoveryGroup);
            }
            catch (SocketException ex)
            {
                _logger?.LogWarning(ex, "LocalSend multicast receive unavailable; using outbound discovery only");
                udp.Dispose();
                udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            }
            _udp = udp;
            state.ReceiveTask = ReceiveAnnouncementsAsync(state, udp);
            state.CoordinatorTask = Task.Run(() => RunDiscoveryAsync(state));
        }
        catch { udp.Dispose(); throw; }
    }

    internal void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        lock (_discoveryLock)
        {
            var token = _runningToken;
            if (!token.CanBeCanceled || token.IsCancellationRequested) return;
            _networkChangeVersion++;
            if (_networkChangeTask is not { IsCompleted: false } || _networkChangeToken != token)
            {
                _networkChangeToken = token;
                _networkChangeTask = Task.Run(() => RejoinAfterNetworkSettlesAsync(token));
            }
        }
    }

    private async Task RejoinAfterNetworkSettlesAsync(CancellationToken token)
    {
        var cleared = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                int version;
                lock (_discoveryLock) version = _networkChangeVersion;
                await Task.Delay(500, token);
                lock (_discoveryLock)
                    if (version != _networkChangeVersion) continue;
                await RejoinDiscoveryAsync();
                lock (_discoveryLock)
                    if (version == _networkChangeVersion)
                    {
                        if (_networkChangeToken == token)
                        {
                            _networkChangeTask = null;
                            _networkChangeToken = default;
                        }
                        cleared = true;
                        return;
                    }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (!cleared)
                lock (_discoveryLock)
                    if (_networkChangeToken == token)
                    {
                        _networkChangeTask = null;
                        _networkChangeToken = default;
                    }
        }
    }

    internal async Task RejoinDiscoveryAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_running is not { IsCancellationRequested: false } || !Enabled) return;
            var previousDiscovery = _networkDiscovery;
            DiscoveryState? previousState;
            lock (_discoveryLock)
            {
                previousState = _discovery;
                _discovery = null;
                _peers.Clear();
            }
            previousDiscovery.Cancel();
            _udp?.Dispose();
            _udp = null;
            if (previousState != null)
            {
                await previousState.Completion;
                previousState.Signal.Dispose();
            }
            previousDiscovery.Dispose();
            _networkDiscovery = new CancellationTokenSource();
            await StartDiscoveryAsync();
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "LocalSend discovery restart failed after network change"); }
        finally { _lifecycle.Release(); }
    }

    private async Task ReceiveAnnouncementsAsync(DiscoveryState state, UdpClient udp)
    {
        var cancellationToken = state.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var packet = await udp.ReceiveAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (packet.Buffer.Length > 4096) continue;
                var info = JsonSerializer.Deserialize<PeerInfo>(packet.Buffer, Json);
                if (info == null || !ValidAnnouncementFingerprint(info.Fingerprint)
                    || info.Fingerprint.Equals(SelfInfo().Fingerprint, StringComparison.OrdinalIgnoreCase)
                    || info.Port is < 1 or > 65535 || info.Version is null
                    || !info.Version.StartsWith("2.", StringComparison.Ordinal)
                    || info.Protocol is not ("http" or "https")) continue;
                var peer = new LocalSendDevice(info.Alias, info.Fingerprint, packet.RemoteEndPoint.Address.ToString(), info.Port, info.Protocol);
                QueueCandidate(state, peer);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { _logger?.LogDebug(ex, "Ignored malformed LocalSend announcement"); }
        }
    }

    private async Task AnnounceAsync(CancellationToken cancellationToken)
    {
        if (_udp == null) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(SelfInfo() with { Announce = true }, Json);
        foreach (var address in ActiveIPv4Addresses())
        {
            try
            {
                using var sender = new UdpClient(new IPEndPoint(address, 0));
                sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                    address.GetAddressBytes());
                await sender.SendAsync(bytes, new IPEndPoint(DiscoveryGroup, DiscoveryUdpPortOverride ?? DiscoveryPort), cancellationToken);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
            { _logger?.LogDebug(ex, "LocalSend multicast announcement failed on {Address}", address); }
        }
    }

    internal Func<IPAddress[]>? DiscoveryAddressesOverride;

    private IPAddress[] ActiveIPv4Addresses() => DiscoveryAddressesOverride?.Invoke() ?? NetworkInterface.GetAllNetworkInterfaces()
        .Where(item => item.OperationalStatus == OperationalStatus.Up)
        .SelectMany(item => item.GetIPProperties().UnicastAddresses)
        .Select(item => item.Address)
        .Where(item => item.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(item))
        .Distinct()
        .ToArray();

    public async Task<IReadOnlyList<LocalSendDevice>> DiscoverAsync(CancellationToken cancellationToken,
        Action<IReadOnlyList<LocalSendDevice>>? onDevicesChanged = null)
    {
        while (true)
        {
            try { return await DiscoverGenerationAsync(cancellationToken, onDevicesChanged); }
            catch (OperationCanceledException) when (onDevicesChanged != null
                && !cancellationToken.IsCancellationRequested && Enabled && !_disposed)
            { await Task.Delay(100, cancellationToken); }
        }
    }

    private async Task<IReadOnlyList<LocalSendDevice>> DiscoverGenerationAsync(CancellationToken cancellationToken,
        Action<IReadOnlyList<LocalSendDevice>>? onDevicesChanged)
    {
        if (!Enabled) return [];
        await DiscoveryTokensAsync(cancellationToken);
        DiscoveryState state;
        lock (_discoveryLock) state = _discovery ?? throw new OperationCanceledException(cancellationToken);
        RefreshKnown(state);
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.Token);
        var previous = "\0";
        var until = onDevicesChanged == null ? DateTime.UtcNow + TimeSpan.FromSeconds(2) : DateTime.MaxValue;
        do
        {
            total.Token.ThrowIfCancellationRequested();
            var snapshot = Snapshot(state);
            var signature = string.Join('|', snapshot.Select(item =>
                $"{item.Fingerprint}:{item.Address}:{item.Port}:{item.Alias}"));
            if (signature != previous)
            {
                previous = signature;
                total.Token.ThrowIfCancellationRequested();
                onDevicesChanged?.Invoke(snapshot);
            }
            if (DateTime.UtcNow >= until) return snapshot;
            await Task.Delay(200, total.Token);
        } while (true);
    }

    private static (uint Network, int Prefix, string Normalized) ParseScanSubnet(string value)
    {
        var parts = value.Trim().Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var prefix) || prefix is < 24 or > 32)
            throw new ArgumentException("扫描网段须为 IPv4 CIDR，掩码长度为 /24–/32。");
        var octets = parts[0].Split('.');
        if (octets.Length != 4) throw new ArgumentException("请输入有效的 IPv4 网段。");
        var bytes = new byte[4];
        for (var i = 0; i < bytes.Length; i++)
            if (octets[i].Length is < 1 or > 3 || octets[i].Any(c => c is < '0' or > '9')
                || !byte.TryParse(octets[i], out bytes[i]))
                throw new ArgumentException("请输入有效的 IPv4 网段。");
        if (bytes[0] is 0 or >= 224 || (bytes[0] == 127 && prefix != 32))
            throw new ArgumentException("不能扫描保留地址、组播地址或整个回环网段。");
        var address = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var network = address & (uint.MaxValue << (32 - prefix));
        return (network, prefix, $"{(network >> 24) & 255}.{(network >> 16) & 255}.{(network >> 8) & 255}.{network & 255}/{prefix}");
    }

    internal async Task ScanSubnetAsync(string subnet, int port, CancellationToken cancellationToken)
        => await ScanAddressesAsync(SubnetAddresses(subnet).ToArray(), port, cancellationToken);

    internal async Task ScanAddressesAsync(IReadOnlyList<string> addresses, int port, CancellationToken cancellationToken)
    {
        await DiscoveryTokensAsync(cancellationToken);
        DiscoveryState state;
        lock (_discoveryLock) state = _discovery ?? throw new OperationCanceledException(cancellationToken);
        using var operation = TrackExternalOperation(state);
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.Token);
        total.CancelAfter(TimeSpan.FromSeconds(8));
        try { await ScanCandidatesAsync(state, addresses, port, TimeSpan.FromMilliseconds(800), total.Token); }
        catch (OperationCanceledException) when (total.IsCancellationRequested && !cancellationToken.IsCancellationRequested
            && !state.Token.IsCancellationRequested) { }
    }

    public async Task<LocalSendDevice> ConnectByAddressAsync(string address, int port, CancellationToken cancellationToken)
    {
        if (!Enabled) throw new InvalidOperationException("LocalSend 已关闭。");
        if (!IPAddress.TryParse(address.Trim().Trim('[', ']'), out var ip))
            throw new ArgumentException("请输入有效的 IPv4 或 IPv6 地址。", nameof(address));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "端口须为 1–65535。");
        await DiscoveryTokensAsync(cancellationToken);
        DiscoveryState state;
        lock (_discoveryLock) state = _discovery ?? throw new OperationCanceledException(cancellationToken);
        using var operation = TrackExternalOperation(state);
        return await ConnectByAddressCoreAsync(state, ip.ToString(), port, cancellationToken);
    }

    private async Task<LocalSendDevice> ConnectByAddressCoreAsync(DiscoveryState state, string address, int port,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await _httpProbeGate.WaitAsync(timeout.Token);
        RecordPeak(ref _peakHttp, Interlocked.Increment(ref _httpInFlight));
        try { return await ConnectByAddressUnderGateAsync(state, address, port, timeout.Token); }
        finally
        {
            Interlocked.Decrement(ref _httpInFlight);
            _httpProbeGate.Release();
        }
    }

    private async Task<LocalSendDevice> ConnectByAddressUnderGateAsync(DiscoveryState state, string address, int port,
        CancellationToken cancellationToken)
    {
        var attemptStarted = DateTime.UtcNow;
        string? certificateFingerprint = null;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            if (certificate == null) return false;
            certificateFingerprint = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
            return true;
        };
        using var bootstrap = new HttpClient(handler);
        var uri = new UriBuilder("https", address, port, "/api/localsend/v2/info").Uri;
        using var response = await bootstrap.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(DiscoveryResponseLimit, cancellationToken);
        var info = await response.Content.ReadFromJsonAsync<PeerInfo>(Json, cancellationToken);
        if (info == null || string.IsNullOrWhiteSpace(info.Alias) || string.IsNullOrWhiteSpace(info.Fingerprint)
            || string.IsNullOrWhiteSpace(info.Version)
            || info.Fingerprint.Length != 64 || !info.Fingerprint.All(Uri.IsHexDigit)
            || !info.Version.StartsWith("2.", StringComparison.Ordinal))
            throw new InvalidDataException("该地址未返回有效的 LocalSend v2 设备信息。");
        if (!string.Equals(info.Fingerprint, certificateFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("设备信息与 TLS 证书指纹不一致。");
        if (info.Fingerprint.Equals(SelfInfo().Fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能连接到本机 LocalSend。");

        var peer = new LocalSendDevice(info.Alias, info.Fingerprint, address, port, "https");
        using var client = CreatePeerClient(peer);
        using var registration = new HttpRequestMessage(HttpMethod.Post, PeerUri(peer, "register"))
        { Content = JsonContent.Create(SelfInfo(), options: Json) };
        using var registered = await client.SendAsync(registration, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        registered.EnsureSuccessStatusCode();
        await registered.Content.LoadIntoBufferAsync(DiscoveryResponseLimit, cancellationToken);
        var registeredInfo = await registered.Content.ReadFromJsonAsync<PeerInfo>(Json, cancellationToken);
        if (registeredInfo == null || string.IsNullOrWhiteSpace(registeredInfo.Alias)
            || !string.Equals(registeredInfo.Fingerprint, peer.Fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("设备注册时返回的指纹不一致。");
        cancellationToken.ThrowIfCancellationRequested();
        peer = peer with { Alias = registeredInfo.Alias };
        RememberVerifiedPeer(peer, state, cancellationToken, attemptStarted);
        return peer;
    }

    private async Task<bool> ProbePeerAsync(DiscoveryState state, LocalSendDevice peer, CancellationToken cancellationToken)
    {
        var attemptStarted = DateTime.UtcNow;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await _httpProbeGate.WaitAsync(timeout.Token);
            RecordPeak(ref _peakHttp, Interlocked.Increment(ref _httpInFlight));
            try
            {
                using var client = CreatePeerClient(peer);
                using var registration = new HttpRequestMessage(HttpMethod.Post, PeerUri(peer, "register"))
                { Content = JsonContent.Create(SelfInfo(), options: Json) };
                using var response = await client.SendAsync(registration, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode) return false;
                await response.Content.LoadIntoBufferAsync(DiscoveryResponseLimit, timeout.Token);
                var info = await response.Content.ReadFromJsonAsync<PeerInfo>(Json, timeout.Token);
                if (info == null || !ValidPeerFingerprint(peer.Protocol, info.Fingerprint)
                    || string.IsNullOrWhiteSpace(info.Alias)
                    || info.Fingerprint.Equals(SelfInfo().Fingerprint, StringComparison.OrdinalIgnoreCase)
                    || !info.Fingerprint.Equals(peer.Fingerprint, StringComparison.OrdinalIgnoreCase)) return false;
                timeout.Token.ThrowIfCancellationRequested();
                if (peer.Protocol == "https")
                    RememberVerifiedPeer(peer with { Alias = info.Alias }, state, timeout.Token, attemptStarted);
                else RememberPeer(peer with { Alias = info.Alias }, state, attemptStarted);
                return true;
            }
            finally
            {
                Interlocked.Decrement(ref _httpInFlight);
                _httpProbeGate.Release();
            }
        }
        catch (Exception ex)
        { _logger?.LogDebug(ex, "LocalSend peer registration failed at {Address}", peer.Address); return false; }
    }

    private async Task<IResult> RegisterAsync(HttpContext context)
    {
        if (!Enabled || _running == null) return Results.StatusCode(503);
        if (context.Request.ContentLength is > 4096) return Results.BadRequest();
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = 4096;
        try
        {
            var info = await context.Request.ReadFromJsonAsync<PeerInfo>(Json, context.RequestAborted);
            if (info == null || info.Port is < 1 or > 65535 || info.Protocol is not ("http" or "https")
                || info.Version is null || !info.Version.StartsWith("2.", StringComparison.Ordinal)
                || !ValidAnnouncementFingerprint(info.Fingerprint)
                || string.IsNullOrWhiteSpace(info.Alias) || info.Alias.Length > 256) return Results.BadRequest();
            var address = (context.Connection.RemoteIpAddress is { } remoteIp ? (remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp).ToString() : null);
            if (address != null && !info.Fingerprint.Equals(SelfInfo().Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                var state = _discovery;
                if (state != null) QueueCandidate(state,
                    new LocalSendDevice(info.Alias, info.Fingerprint, address, info.Port, info.Protocol));
            }
            return Results.Json(SelfInfo(), Json);
        }
        catch (JsonException) { return Results.BadRequest(); }
        catch (BadHttpRequestException) { return Results.BadRequest(); }
    }

    private bool RememberPeer(LocalSendDevice peer, DiscoveryState state, DateTime attemptStarted)
    {
        if (string.IsNullOrWhiteSpace(peer.Alias) || peer.Fingerprint.Equals(SelfInfo().Fingerprint, StringComparison.OrdinalIgnoreCase)) return false;
        if (!ValidPeerFingerprint(peer.Protocol, peer.Fingerprint)) return false;
        lock (_discoveryLock)
        {
            if (!ReferenceEquals(_discovery, state) || state.Token.IsCancellationRequested) return false;
            if (_peers.TryGetValue(peer.Fingerprint, out var current) && current.Seen > attemptStarted
                && (current.Device.Address != peer.Address || current.Device.Port != peer.Port)) return false;
            _peers[peer.Fingerprint] = (peer, DateTime.UtcNow);
            if (_peers.Count > 128)
            {
                var oldest = _peers.OrderBy(item => item.Value.Seen).First();
                _peers.Remove(oldest.Key);
            }
            if (!state.Candidates.TryGetValue(peer.Fingerprint, out var candidate))
            {
                if (state.Candidates.Count >= 128)
                {
                    var evict = state.Candidates.Where(item => !item.Value.InFlight)
                        .OrderBy(item => Priority(item.Value))
                        .ThenBy(item => item.Value.LastSuccess)
                        .ThenBy(item => item.Value.LastTouched).FirstOrDefault();
                    if (evict.Key != null) state.Candidates.Remove(evict.Key);
                }
                if (state.Candidates.Count < 128)
                    state.Candidates[peer.Fingerprint] = candidate = new CandidateState(peer);
            }
            if (candidate != null)
            {
                candidate.Device = peer;
                candidate.LastSuccess = DateTime.UtcNow;
                candidate.Failures = 0;
                candidate.NormalNextAttempt = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                candidate.NextAttempt = FastKnown(state, candidate)
                    ? DateTime.UtcNow + TimeSpan.FromSeconds(5) : candidate.NormalNextAttempt;
            }
            return true;
        }
    }
}
