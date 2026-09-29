using System.Net;
using System.Net.NetworkInformation;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MacExplorer.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public sealed partial class LocalSendService : ILocalSendService, IAsyncDisposable
{
    private const int DiscoveryPort = 53317;
    private static readonly IPAddress DiscoveryGroup = IPAddress.Parse("224.0.0.167");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ISettingsService _settings;
    private readonly IBackgroundTaskManager _tasks;
    private readonly ILogger<LocalSendService>? _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _httpProbeGate = new(4, 4);
    private readonly SemaphoreSlim _tcpProbeGate = new(16, 16);
    private readonly object _discoveryLock = new();
    private readonly Dictionary<string, (LocalSendDevice Device, DateTime Seen)> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _historyLock = new();
    private List<VerifiedPeerHistory>? _verifiedHistory;
    private readonly object _receiveLock = new();
    private CancellationTokenSource _networkDiscovery = new();
    private DiscoveryState? _discovery;
    private Task? _networkChangeTask;
    private CancellationToken _runningToken;
    private CancellationToken _networkChangeToken;
    private int _networkChangeVersion;
    private bool _disposed;
    private CancellationTokenSource? _running;
    private UdpClient? _udp;
    private WebApplication? _host;
    private X509Certificate2? _certificate;
    private string? _fingerprint;
    private IncomingSession? _incoming;
    private BackgroundTaskInfo? _sending;
    private int _tcpInFlight, _httpInFlight, _peakTcp, _peakHttp, _unknownProbes;
    private int _port;
    private string? _lastError;
    public int ListeningPort => _port;
    internal string? Fingerprint => _fingerprint;
    internal int DiscoveryUdpPort => (_udp?.Client.LocalEndPoint as IPEndPoint)?.Port ?? 0;
    internal Task? DiscoveryCoordinator
    {
        get { lock (_discoveryLock) return _discovery?.CoordinatorTask; }
    }
    internal int DiscoveryInFlightCount
    {
        get { lock (_discoveryLock) return _discovery?.Candidates.Values.Count(item => item.InFlight) ?? 0; }
    }
    public string? LastError => _lastError;
    internal (int Candidates, int UnknownProbes, int PeakTcp, int PeakHttp) DiscoveryStats
    {
        get
        {
            lock (_discoveryLock)
                return (_discovery?.Candidates.Count ?? 0, Volatile.Read(ref _unknownProbes),
                    Volatile.Read(ref _peakTcp), Volatile.Read(ref _peakHttp));
        }
    }

    private static void RecordPeak(ref int peak, int active)
    {
        int previous;
        while ((previous = Volatile.Read(ref peak)) < active
            && Interlocked.CompareExchange(ref peak, active, previous) != previous) { }
    }

    public LocalSendService(ISettingsService settings, IBackgroundTaskManager tasks, ILogger<LocalSendService>? logger = null)
    {
        _settings = settings;
        _tasks = tasks;
        _logger = logger;
    }

    public bool Enabled => _settings.Get(ILocalSendService.EnabledKey, true);
    public string Alias
    {
        get => _settings.Get(ILocalSendService.AliasKey, "Mac Explorer · " + Environment.MachineName);
        set
        {
            _settings.Set(ILocalSendService.AliasKey, string.IsNullOrWhiteSpace(value) ? "Mac Explorer · " + Environment.MachineName : value.Trim());
            lock (_discoveryLock)
            {
                if (_discovery is { } state)
                {
                    state.AnnounceRequested = true;
                    state.Wake();
                }
            }
        }
    }
    public string ReceiveDirectory
    {
        get
        {
            var configured = _settings.Get(ILocalSendService.ReceiveDirectoryKey, "");
            if (RuntimePaths.TestRoot is { } root)
            {
                var downloads = Path.Combine(root, "Downloads");
                return string.IsNullOrEmpty(configured) || !IsWithin(configured, root) ? downloads : configured;
            }
            return string.IsNullOrWhiteSpace(configured) ? Path.Combine(RuntimePaths.HomeDirectory, "Downloads") : configured;
        }
        set
        {
            var path = Path.GetFullPath(value);
            if (RuntimePaths.TestRoot is { } root && !IsWithin(path, root))
                throw new ArgumentException("测试模式下接收目录必须位于隔离目录内。", nameof(value));
            _settings.Set(ILocalSendService.ReceiveDirectoryKey, path);
        }
    }
    public string ScanSubnet
    {
        get => _settings.Get(ILocalSendService.ScanSubnetKey, "");
        set => _settings.Set(ILocalSendService.ScanSubnetKey,
            string.IsNullOrWhiteSpace(value) ? "" : ParseScanSubnet(value).Normalized);
    }
    public Func<LocalSendIncomingRequest, CancellationToken, Task<LocalSendReceiveDecision>>? ConfirmReceiveAsync { get; set; }
    public Func<CancellationToken, Task<string?>>? RequestPinAsync { get; set; }

    public async Task StartAsync()
    {
        if (!Enabled) return;
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LocalSendService));
            if (_running != null || !Enabled) return;
            _certificate = LoadCertificate();
            _fingerprint = Convert.ToHexString(SHA256.HashData(_certificate.RawData)).ToLowerInvariant();
            _networkDiscovery.Dispose();
            _networkDiscovery = new CancellationTokenSource();
            _running = new CancellationTokenSource();
            lock (_discoveryLock) _runningToken = _running.Token;
            // Check first so an already occupied default port does not produce
            // Kestrel's expected hosting-failure log on every launch.
            var preferredPort = CanBindDefaultPort() ? DiscoveryPort : 0;
            try { _host = await CreateHostAsync(preferredPort, _running.Token); }
            catch (IOException ex) when (preferredPort == DiscoveryPort && IsAddressInUse(ex))
            { _host = await CreateHostAsync(0, _running.Token); }
            var server = _host.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
            _port = addresses?.Select(address => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Port : 0)
                .FirstOrDefault(value => value > 0) ?? 0;
            if (_port == 0) throw new IOException("无法获取 LocalSend 监听端口。");
            await StartDiscoveryAsync();
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            _discovery?.Wake();
            _lastError = null;
        }
        catch (Exception ex)
        {
            await StopCoreAsync();
            _lastError = ex.Message;
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    private static bool CanBindDefaultPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try { socket.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort)); return true; }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse) { return false; }
    }

    private static bool IsAddressInUse(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
            if (current is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }) return true;
        return false;
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalSendService));
        _settings.Set(ILocalSendService.EnabledKey, enabled);
        if (enabled) { await StartAsync(); return; }
        await _lifecycle.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _lifecycle.Release(); }
        Task? networkChange;
        lock (_discoveryLock) networkChange = _networkChangeTask;
        if (networkChange != null) await networkChange;
    }

    internal async Task<(CancellationToken Running, CancellationToken Network)> DiscoveryTokensAsync(CancellationToken cancellationToken)
    {
        await StartAsync();
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (!Enabled || _running == null) throw new OperationCanceledException("LocalSend 已关闭。", cancellationToken);
            return (_running.Token, _networkDiscovery.Token);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        DiscoveryState? stopping;
        lock (_discoveryLock)
        {
            stopping = _discovery;
            _discovery = null;
            _runningToken = default;
            _peers.Clear();
        }
        _networkDiscovery.Cancel();
        _running?.Cancel();
        _sending?.Cts.Cancel();
        IncomingSession? incoming;
        lock (_receiveLock)
        {
            incoming = _incoming;
            _incoming = null;
        }
        if (incoming != null) FinishIncoming(incoming, "LocalSend 已关闭。");
        _udp?.Dispose();
        _udp = null;
        if (stopping != null)
        {
            await stopping.Completion;
            stopping.Signal.Dispose();
        }
        if (_host != null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await _host.StopAsync(timeout.Token); }
            catch (Exception ex) { _logger?.LogDebug(ex, "LocalSend listener stop failed"); }
            finally { await _host.DisposeAsync(); _host = null; }
        }
        _running?.Dispose();
        _running = null;
        _port = 0;
        _certificate?.Dispose();
        _certificate = null;
        _fingerprint = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopCoreAsync();
        }
        finally { _lifecycle.Release(); }
        Task? networkChange;
        lock (_discoveryLock) networkChange = _networkChangeTask;
        if (networkChange != null) await networkChange;
        _networkDiscovery.Dispose();
    }

    private async Task<WebApplication> CreateHostAsync(int port, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        // Request URLs contain short-lived upload tokens; suppress ASP.NET's URL-bearing access log.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        // Discovery uses IPv4 multicast. Bind IPv4 explicitly so an IPv4 peer
        // cannot own the same port while Kestrel silently binds IPv6 only.
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Any, port, listen => listen.UseHttps(_certificate!)));
        var app = builder.Build();
        const string prefix = "/api/localsend/v2";
        IResult Info(HttpContext context)
        {
            var info = SelfInfo();
            return string.Equals(context.Request.Query["fingerprint"].ToString(), info.Fingerprint, StringComparison.OrdinalIgnoreCase)
                ? Results.StatusCode(412)
                : Results.Json(info, Json);
        }
        app.MapGet("/api/localsend/v1/info", Info);
        app.MapGet(prefix + "/info", Info);
        app.MapPost(prefix + "/register", (Delegate)(Func<HttpContext, Task<IResult>>)RegisterAsync);
        app.MapPost(prefix + "/prepare-upload", (Delegate)(Func<HttpContext, Task<IResult>>)PrepareUploadAsync);
        app.MapPost(prefix + "/upload", (Delegate)(Func<HttpContext, Task<IResult>>)UploadAsync);
        app.MapPost(prefix + "/cancel", (Delegate)(Func<HttpContext, IResult>)CancelIncomingAsync);
        try { await app.StartAsync(cancellationToken); return app; }
        catch { await app.DisposeAsync(); throw; }
    }

    private X509Certificate2 LoadCertificate()
    {
        var directory = Path.Combine(RuntimePaths.LocalApplicationData, "LocalSend");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "identity.pfx");
        if (File.Exists(file)) return X509CertificateLoader.LoadPkcs12FromFile(file, "");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Mac Explorer LocalSend", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.Write(certificate.Export(X509ContentType.Pkcs12, ""));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return X509CertificateLoader.LoadPkcs12FromFile(file, "");
    }

    private PeerInfo SelfInfo() => new(Alias, "2.2", Environment.MachineName, "desktop",
        _fingerprint!, _port, "https", false, false);

    private static bool IsWithin(string path, string root)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return path == root || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private sealed record PeerInfo(string Alias, string Version, string? DeviceModel, string? DeviceType,
        string Fingerprint, int Port, string Protocol, bool Download, bool Announce);
    private sealed record FileInfoDto(string Id, string FileName, long Size, string FileType, string? Sha256 = null);
    private sealed record PrepareRequest(PeerInfo Info, Dictionary<string, FileInfoDto> Files);
    private sealed record PrepareResponse(string SessionId, Dictionary<string, string> Files);

    private HttpClient CreatePeerClient(LocalSendDevice peer)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        if (peer.Protocol == "https")
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) =>
                cert != null && CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(cert.GetRawCertData()), Convert.FromHexString(peer.Fingerprint));
        }
        // Uploads may remain active for longer than 15 minutes; each request
        // applies its own bounded wait or idle timeout.
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static Uri PeerUri(LocalSendDevice peer, string path)
        => new($"{peer.Protocol}://{(peer.Address.Contains(':') ? "[" + peer.Address + "]" : peer.Address)}:{peer.Port}/api/localsend/v2/{path}");
}
