using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using MacExplorer.Models;
using MacExplorer.Platforms.MacOS;

namespace MacExplorer.Services.Impl;

public sealed class NetworkDiscoveryService : INetworkDiscoveryService, IDisposable
{
    private readonly object _sync = new();
    private readonly Func<Action<string>, IDisposable?> _browse;
    private readonly Func<CancellationToken, Task> _probe;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    public NetworkDiscoveryService()
    {
        _browse = callback => OperatingSystem.IsMacOS() ? MacNetworkNative.Browse(callback) : null;
        _probe = DiscoverWsdAsync;
        _resolve = Dns.GetHostAddressesAsync;
    }
    internal NetworkDiscoveryService(Func<Action<string>, IDisposable?> browse, Func<CancellationToken, Task> probe,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    { _browse = browse; _probe = probe; _resolve = resolve ?? Dns.GetHostAddressesAsync; }
    private readonly Dictionary<string, (NetworkServer Server, DateTime Seen)> _services = new();
    private CancellationTokenSource? _running;
    private IDisposable? _bonjour;
    private int _leases;
    private string? _error;
    public string? Error { get { lock (_sync) return _error; } }
    public event Action? Changed;
    public IReadOnlyList<NetworkServer> Servers { get { lock (_sync) return Merge(_services.Values.Select(v => v.Server)); } }

    public IDisposable Acquire()
    {
        lock (_sync) { if (++_leases == 1) Start(); }
        return new Lease(this);
    }
    private sealed class Lease(NetworkDiscoveryService service) : IDisposable
    {
        private NetworkDiscoveryService? _service = service;
        public void Dispose() => Interlocked.Exchange(ref _service, null)?.Release();
    }
    private void Release() { lock (_sync) { if (--_leases == 0) Stop(); } }
    public void Refresh()
    {
        lock (_sync) { if (_leases == 0) return; Stop(); Start(); }
        Changed?.Invoke();
    }
    private void Start()
    {
        _error = null;
        var run = _running = new();
        var token = run.Token;
        try { _bonjour = _browse(json => _ = ProcessBonjourAsync(json, token)); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { _error = LocalizationText.Get("network.discovery-error"); }
        _ = _probe(token);
    }
    private void Stop()
    {
        _running?.Cancel(); _running?.Dispose(); _running = null;
        _bonjour?.Dispose(); _bonjour = null; _services.Clear();
    }
    internal async Task ProcessBonjourAsync(string json, CancellationToken token)
    {
        try
        {
            var result = JsonSerializer.Deserialize<BonjourResult>(json)!;
            if (token.IsCancellationRequested) return;
            if (result.Ready) { SetError(null, token); return; }
            if (result.Error != 0) { SetError(LocalizationText.Get(result.Error is -65570 or 1 or 13 ? "network.discovery-permission" : "network.discovery-error"), token); return; }
            if (result.Removed) { Remove("b:" + result.Id, token); return; }
            if (string.IsNullOrEmpty(result.Host)) return;
            var server = new NetworkServer("b:" + result.Id, result.Name, result.Host.TrimEnd('.'), result.Port, []);
            Upsert(server, token);
            var addresses = await _resolve(result.Host, token);
            lock (_sync)
            {
                if (token.IsCancellationRequested || !_services.TryGetValue(server.Id, out var current) || !ReferenceEquals(current.Server, server)) return;
                _services[server.Id] = (server with { Addresses = addresses.Select(a => a.ToString()).ToArray() }, current.Seen);
            }
            Changed?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (JsonException) { SetError(LocalizationText.Get("network.discovery-error"), token); }
    }
    private sealed record BonjourResult(string Id, string Name, string Host, int Port, bool Removed, int Error, bool Ready);
    private void SetError(string? error, CancellationToken token = default)
    {
        lock (_sync) { if (token.IsCancellationRequested) return; _error = error; }
        Changed?.Invoke();
    }
    internal void Upsert(NetworkServer server, CancellationToken token)
    {
        lock (_sync) { if (token.IsCancellationRequested) return; _services[server.Id] = (server, DateTime.UtcNow); }
        Changed?.Invoke();
    }
    private void Remove(string id, CancellationToken token)
    {
        lock (_sync) { if (token.IsCancellationRequested) return; _services.Remove(id); }
        Changed?.Invoke();
    }

    internal static IReadOnlyList<NetworkServer> Merge(IEnumerable<NetworkServer> servers)
    {
        var groups = new List<NetworkServer>();
        foreach (var server in servers.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            var matches = groups.Where(s => SameHost(s, server)).ToArray();
            if (matches.Length == 0) { groups.Add(server); continue; }
            var first = matches[0];
            foreach (var match in matches) groups.Remove(match);
            groups.Add(first with { Addresses = matches.SelectMany(m => m.Addresses).Concat(server.Addresses).Distinct().ToArray() });
        }
        return groups.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    private static bool SameHost(NetworkServer a, NetworkServer b) => a.Port == b.Port &&
        (string.Equals(a.Host.TrimEnd('.'), b.Host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
        || a.Addresses.Intersect(b.Addresses, StringComparer.OrdinalIgnoreCase).Any());

    internal static string ProbeMessage(string messageId) => $"""
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery" xmlns:p="http://schemas.microsoft.com/windows/2006/08/wdp/pub">
        <s:Header><a:MessageID>urn:uuid:{messageId}</a:MessageID><a:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</a:To><a:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</a:Action></s:Header>
        <s:Body><d:Probe><d:Types>p:Computer</d:Types></d:Probe></s:Body></s:Envelope>
        """;

    internal static IReadOnlyList<NetworkServer> ParseWsd(byte[] packet, IPAddress sender)
    {
        if (packet.Length > 65536) return [];
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(packet), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 });
            var doc = XDocument.Load(reader);
            XNamespace discovery = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
            XNamespace addressing = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
            var results = new List<NetworkServer>();
            foreach (var match in doc.Descendants(discovery + "ProbeMatch").Concat(doc.Descendants(discovery + "Hello")))
            {
                var types = match.Element(discovery + "Types");
                if (types == null || !types.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(t =>
                {
                    var pair = t.Split(':', 2);
                    return pair.Length == 2 && pair[1] == "Computer" && types.GetNamespaceOfPrefix(pair[0])?.NamespaceName == "http://schemas.microsoft.com/windows/2006/08/wdp/pub";
                })) continue;
                var id = match.Element(addressing + "EndpointReference")?.Element(addressing + "Address")?.Value;
                if (string.IsNullOrWhiteSpace(id)) continue;
                results.Add(new("w:" + id, sender.ToString(), sender.ToString(), 445, [sender.ToString()]));
            }
            return results;
        }
        catch (XmlException) { return []; }
    }

    private async Task DiscoverWsdAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast && n.NetworkInterfaceType != NetworkInterfaceType.Loopback).ToArray();
                var scans = new List<Task>();
                foreach (var network in interfaces)
                {
                    var properties = network.GetIPProperties();
                    foreach (var address in properties.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                        scans.Add(ProbeAsync(address.Address, 0, token));
                    var index = properties.GetIPv6Properties()?.Index ?? 0;
                    if (index > 0 && properties.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6))
                        scans.Add(ProbeAsync(IPAddress.IPv6Any, index, token));
                }
                await Task.WhenAll(scans);
                bool removed;
                lock (_sync)
                {
                    var stale = _services.Where(p => p.Key.StartsWith("w:", StringComparison.Ordinal) && p.Value.Seen < DateTime.UtcNow.AddSeconds(-60)).Select(p => p.Key).ToArray();
                    foreach (var id in stale) _services.Remove(id);
                    removed = stale.Length > 0;
                }
                if (removed) Changed?.Invoke();
                await Task.Delay(TimeSpan.FromSeconds(15), token);
            }
            catch (OperationCanceledException) { break; }
            catch (NetworkInformationException) { SetError(LocalizationText.Get("network.discovery-error"), token); await DelayRetryAsync(token); }
        }
    }
    private static async Task DelayRetryAsync(CancellationToken token) { try { await Task.Delay(15000, token); } catch (OperationCanceledException) { } }
    private async Task ProbeAsync(IPAddress local, int index, CancellationToken token)
    {
        try
        {
            using var socket = new UdpClient(local.AddressFamily);
            socket.Client.Bind(new IPEndPoint(local, 0));
            var target = index == 0 ? new IPEndPoint(IPAddress.Parse("239.255.255.250"), 3702) : new IPEndPoint(new IPAddress(IPAddress.Parse("ff02::c").GetAddressBytes(), index), 3702);
            if (index != 0) socket.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, index);
            else socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var packet = Encoding.UTF8.GetBytes(ProbeMessage(Guid.NewGuid().ToString()));
            await socket.SendAsync(packet, target, timeout.Token);
            while (!timeout.IsCancellationRequested)
            {
                var reply = await socket.ReceiveAsync(timeout.Token);
                foreach (var server in ParseWsd(reply.Buffer, reply.RemoteEndPoint.Address)) Upsert(server, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException ex) { if (ex.SocketErrorCode == SocketError.AccessDenied) SetError(LocalizationText.Get("network.discovery-permission"), token); }
    }
    public void Dispose() { lock (_sync) { Stop(); _leases = 0; } }
}
