using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

public sealed class LocalSendServiceTests : IAsyncLifetime
{
    private readonly string? _oldRoot = Environment.GetEnvironmentVariable(RuntimePaths.TestRootVariable);
    private readonly string _root = Path.Combine("/private/tmp", "fk-localsend-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySettings _settings = new();
    private readonly BackgroundTaskManager _tasks = new();
    private LocalSendService _service = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        Environment.SetEnvironmentVariable(RuntimePaths.TestRootVariable, _root);
        RuntimePaths.PrepareTestRoot();
        _service = new LocalSendService(_settings, _tasks);
        _service.ScanInterfacesOverride = () => [];
        _service.DiscoveryAddressesOverride = () => [IPAddress.Loopback];
        using (var availableUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            _service.DiscoveryUdpPortOverride = ((IPEndPoint)availableUdp.Client.LocalEndPoint!).Port;
        _service.ConfirmReceiveAsync = (request, _) => Task.FromResult(new LocalSendReceiveDecision(true, request.DefaultDirectory));
        await _service.StartAsync();
        _client = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            UseProxy = false
        }) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _service.DisposeAsync();
        Environment.SetEnvironmentVariable(RuntimePaths.TestRootVariable, _oldRoot);
        Directory.Delete(_root, recursive: true);
    }

    private string Url(string path) => $"https://127.0.0.1:{_service.ListeningPort}/api/localsend/v2/{path}";
    private object PrepareBody(params (string Id, string Name, long Size, string? Hash)[] files) => new
    {
        info = new { alias = "Test sender", version = "2.2", fingerprint = new string('a', 64), port = 53400, protocol = "https", deviceType = "desktop" },
        files = files.ToDictionary(file => file.Id, file => new
        {
            id = file.Id, fileName = file.Name, size = file.Size,
            fileType = "application/octet-stream", sha256 = file.Hash
        })
    };

    private async Task<(string Session, Dictionary<string, string> Tokens)> PrepareAsync(params (string Id, string Name, long Size, string? Hash)[] files)
    {
        using var response = await _client.PostAsJsonAsync(Url("prepare-upload"), PrepareBody(files));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("sessionId").GetString()!, json.RootElement.GetProperty("files")
            .EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString()!));
    }

    private async Task<HttpStatusCode> UploadAsync(string session, string id, string token, byte[] data)
    {
        using var response = await _client.PostAsync(Url($"upload?sessionId={session}&fileId={id}&token={token}"), new ByteArrayContent(data));
        return response.StatusCode;
    }

    [Fact]
    public async Task ListenerAdvertisesItsActualPort()
    {
        Assert.InRange(_service.ListeningPort, 1, 65535);
        using var response = await _client.GetAsync(Url("info"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(_service.ListeningPort, json.RootElement.GetProperty("port").GetInt32());
    }

    [Fact]
    public async Task LegacyInfoDiscoverySelectsVersionTwoAndRejectsSelf()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var legacyUrl = $"https://127.0.0.1:{_service.ListeningPort}/api/localsend/v1/info";
        var peerFingerprint = new string('a', 64);
        using var response = await _client.GetAsync($"{legacyUrl}?fingerprint={peerFingerprint}", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var version = json.RootElement.GetProperty("version").GetString();
        Assert.StartsWith("2.", version);
        Assert.Equal(_service.ListeningPort, json.RootElement.GetProperty("port").GetInt32());
        Assert.Equal(_service.Fingerprint, json.RootElement.GetProperty("fingerprint").GetString());

        using var registered = await _client.PostAsJsonAsync(Url("register"), new
        {
            alias = "Official scanner", version = "2.1", fingerprint = peerFingerprint,
            port = 53317, protocol = "https", deviceType = "mobile"
        }, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);

        using var self = await _client.GetAsync($"{legacyUrl}?fingerprint={_service.Fingerprint}", cancellationToken);
        Assert.Equal(HttpStatusCode.PreconditionFailed, self.StatusCode);
    }

    [Fact]
    public void ScanSubnetSettingNormalizesAndRejectsInvalidRanges()
    {
        _service.ScanSubnet = "172.20.235.83/24";
        Assert.Equal("172.20.235.0/24", _service.ScanSubnet);
        Assert.Equal(_service.ScanSubnet, _settings.Get(ILocalSendService.ScanSubnetKey));
        _service.ScanSubnet = "127.0.0.1/32";
        Assert.Equal("127.0.0.1/32", _service.ScanSubnet);
        foreach (var invalid in new[] { "172.20.235.0/23", "224.0.0.0/24", "0.0.0.0/24", "127.0.0.0/24", "255.255.255.255/32", "172.20.235/24" })
            Assert.Throws<ArgumentException>(() => _service.ScanSubnet = invalid);
        _service.ScanSubnet = "";
        Assert.Equal("", _service.ScanSubnet);
    }

    [Fact]
    public void AutomaticCandidatesRespectNetworkScopePrefixAndPrivate16Boundary()
    {
        LocalSendService.ScanInterface[] networks =
        [
            new("172.20.227.63", 24, "wifi-a"),
            new("172.20.202.24", 24, "ethernet-a")
        ];
        var history = new[]
        {
            new LocalSendService.VerifiedPeerHistory(new string('a', 64), "172.20.235.80", 53317,
                "Known phone", ["wifi-a"], 10L)
        };
        var matching = LocalSendService.MatchingHistory(history, new HashSet<string> { "wifi-a", "ethernet-a" });
        Assert.Contains("172.20.235.80", LocalSendService.FastScanCandidates(networks, matching, ""));
        Assert.Empty(LocalSendService.MatchingHistory(history, new HashSet<string> { "wifi-b" }));
        Assert.DoesNotContain("172.20.235.80", LocalSendService.FastScanCandidates(networks, [], ""));

        var expanded = LocalSendService.ExpandedScanCandidates(networks, [], 10000);
        Assert.Contains("172.20.235.80", expanded);
        Assert.Equal(expanded.Count, expanded.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("172.20.227.63", expanded);
        Assert.All(expanded, address => Assert.StartsWith("172.20.", address));

        var narrow = LocalSendService.FastScanCandidates(
            [new LocalSendService.ScanInterface("192.168.1.140", 25, "wifi-b")], [], "");
        Assert.Contains("192.168.1.200", narrow);
        Assert.DoesNotContain("192.168.1.20", narrow);
        var tenRange = LocalSendService.ExpandedScanCandidates(
            [new LocalSendService.ScanInterface("10.2.3.4", 16, "wifi-c")], [], 512);
        Assert.All(tenRange, address => Assert.StartsWith("10.2.", address));
        var fair = LocalSendService.ExpandedScanCandidates(
            [new LocalSendService.ScanInterface("10.2.3.4", 16, "wifi-c"),
             new LocalSendService.ScanInterface("192.168.1.4", 24, "ethernet-b")], [], 128);
        Assert.Contains(fair, address => address.StartsWith("10.2.", StringComparison.Ordinal));
        Assert.Contains(fair, address => address.StartsWith("192.168.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TargetedSubnetScanPinsCertificateBoundsResponsesAndCancelsBeforeRegistration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=LocalSend scan test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        var advertisedFingerprint = new string('a', 64);
        var oversized = false;
        var holdInfo = false;
        var infoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registerCount = 0;
        var port = 0;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        app.MapGet("/api/localsend/v2/info", async (HttpContext context) =>
        {
            if (holdInfo)
            {
                infoStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            if (oversized) return Results.Text(new string('x', 65 * 1024));
            return Results.Json(new { alias = "Scan peer", version = "2.1", deviceModel = "Test",
                deviceType = "mobile", fingerprint = advertisedFingerprint, port, protocol = "https",
                download = false, announce = false });
        });
        app.MapPost("/api/localsend/v2/register", () =>
        {
            Interlocked.Increment(ref registerCount);
            return Results.Json(new { alias = "Scan peer", version = "2.1", deviceModel = "Test",
                deviceType = "mobile", fingerprint, port, protocol = "https",
                download = false, announce = false });
        });
        await app.StartAsync(cancellationToken);
        var server = app.Services.GetRequiredService<IServer>();
        port = new Uri(Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses)).Port;

        await _service.ScanSubnetAsync("127.0.0.1/32", port, cancellationToken);
        Assert.Equal(0, registerCount);

        advertisedFingerprint = fingerprint;
        oversized = true;
        await _service.ScanSubnetAsync("127.0.0.1/32", port, cancellationToken);
        Assert.Equal(0, registerCount);

        oversized = false;
        holdInfo = true;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var scan = _service.ScanSubnetAsync("127.0.0.1/32", port, cancel.Token);
        await infoStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        Assert.Equal(0, registerCount);
        Assert.DoesNotContain(await _service.DiscoverAsync(cancellationToken), peer => peer.Fingerprint == fingerprint);

        holdInfo = false;
        _service.ScanInterfacesOverride = () => [new LocalSendService.ScanInterface("127.0.0.1", 32, "test-network")];
        await _service.ScanSubnetAsync("127.0.0.1/32", port, cancellationToken);
        Assert.Equal(1, registerCount);
        Assert.Equal(1, _settings.HistoryWrites);
        Assert.Contains(fingerprint, _settings.Get(ILocalSendService.VerifiedPeersKey)!);
        await _service.ScanSubnetAsync("127.0.0.1/32", port, cancellationToken);
        Assert.Equal(1, _settings.HistoryWrites);
        _service.ScanInterfacesOverride = () => [];
        Assert.Contains(await _service.DiscoverAsync(cancellationToken), peer => peer.Fingerprint == fingerprint);

        holdInfo = true;
        var beforeTimeoutCount = registerCount;
        await _service.ScanSubnetAsync("127.0.0.1/32", port, cancellationToken);
        Assert.Equal(beforeTimeoutCount, registerCount);
        Assert.Contains(await _service.DiscoverAsync(cancellationToken), peer => peer.Fingerprint == fingerprint);
    }

    [Fact]
    public async Task NetworkRejoinCancelsAnInFlightManualConnection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=LocalSend network change", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        app.MapGet("/api/localsend/v2/info", async (HttpContext context) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        });
        await app.StartAsync(cancellationToken);
        var server = app.Services.GetRequiredService<IServer>();
        var port = new Uri(Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses)).Port;
        var (_, oldNetwork) = await _service.DiscoveryTokensAsync(cancellationToken);
        var connection = _service.ConnectByAddressAsync("127.0.0.1", port, cancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        await _service.RejoinDiscoveryAsync();
        Assert.True(oldNetwork.IsCancellationRequested);
        var (_, newNetwork) = await _service.DiscoveryTokensAsync(cancellationToken);
        Assert.False(newNetwork.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection);
        Assert.Equal(0, _settings.HistoryWrites);
    }

    [Fact]
    public async Task MenuSubscriptionContinuesAcrossNetworkGenerationWithoutStartingAnotherScan()
    {
        using var cancel = new CancellationTokenSource();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        var original = _service.DiscoveryCoordinator;
        var subscription = _service.DiscoverAsync(cancel.Token, _ =>
        {
            if (Interlocked.Increment(ref callbacks) == 1) first.TrySetResult();
            else second.TrySetResult();
        });
        await first.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Same(original, _service.DiscoveryCoordinator);
        await _service.RejoinDiscoveryAsync();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotSame(original, _service.DiscoveryCoordinator);
        Assert.False(subscription.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => subscription);
    }

    [Fact]
    public async Task BackgroundRegistersWithoutMenuAndKeepsWorkingAfterMalformedPeerAndUdpBurst()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=LocalSend background test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        var malformed = true;
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrations = 0;
        var port = 0;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        app.MapPost("/api/localsend/v2/register", () =>
        {
            Interlocked.Increment(ref registrations);
            registered.TrySetResult();
            return Results.Json(new { alias = "Background peer", version = "2.2",
                fingerprint = malformed ? null : fingerprint, port, protocol = "https" });
        });
        await app.StartAsync();
        port = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses)).Port;

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var udpPort = _service.DiscoveryUdpPort;
        Assert.True(udpPort > 0);
        for (var index = 1; index <= 180; index++)
        {
            var packet = JsonSerializer.SerializeToUtf8Bytes(new { alias = "Burst", version = "2.2",
                fingerprint = index.ToString("x64"), port = 65000, protocol = "https", announce = true });
            await udp.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, udpPort));
        }
        await Task.Delay(200);
        Assert.InRange(_service.DiscoveryStats.Candidates, 1, 128);

        using var inbound = await _client.PostAsJsonAsync(Url("register"), new { alias = "Background peer",
            version = "2.2", fingerprint, port, protocol = "https" });
        Assert.Equal(HttpStatusCode.OK, inbound.StatusCode);
        await registered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(1, registrations);

        malformed = false;
        var devices = await _service.DiscoverAsync(TestContext.Current.CancellationToken);
        Assert.Contains(devices, device => device.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase));
        for (var index = 181; index <= 360; index++)
        {
            var packet = JsonSerializer.SerializeToUtf8Bytes(new { alias = "More burst", version = "2.2",
                fingerprint = index.ToString("x64"), port = 65000, protocol = "https", announce = true });
            await udp.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, udpPort));
        }
        await Task.Delay(200);
        Assert.InRange(_service.DiscoveryStats.Candidates, 1, 128);
        Assert.Contains(await _service.DiscoverAsync(TestContext.Current.CancellationToken),
            device => device.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase));

        using var menu = new CancellationTokenSource();
        using var secondMenu = new CancellationTokenSource();
        var coordinator = _service.DiscoveryCoordinator;
        var subscription = _service.DiscoverAsync(menu.Token, _ => { });
        var secondSubscription = _service.DiscoverAsync(secondMenu.Token, _ => { });
        Assert.Same(coordinator, _service.DiscoveryCoordinator);
        menu.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => subscription);
        Assert.Same(coordinator, _service.DiscoveryCoordinator);
        secondMenu.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondSubscription);
        Assert.True(_service.ListeningPort > 0);
        Assert.Contains(await _service.DiscoverAsync(TestContext.Current.CancellationToken),
            device => device.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task KnownHistoryRegistersAgainWithinSecondsWithoutOpeningMenuAndRecoversAfterFailure()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=LocalSend known peer", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        var calls = new List<DateTime>();
        var sixth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var port = 0;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        app.MapPost("/api/localsend/v2/register", async (HttpContext context) =>
        {
            int count;
            lock (calls)
            {
                calls.Add(DateTime.UtcNow);
                count = calls.Count;
            }
            if (count == 6) sixth.TrySetResult();
            if (count == 5)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted); }
                catch (OperationCanceledException) { }
            }
            return count == 3 ? Results.StatusCode(503) : Results.Json(new
            { alias = "Known peer", version = "2.2", fingerprint, port, protocol = "https" });
        });
        await app.StartAsync();
        port = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses)).Port;

        var settings = new MemorySettings();
        settings.Set(ILocalSendService.VerifiedPeersKey, JsonSerializer.Serialize(new[]
        {
            new LocalSendService.VerifiedPeerHistory(fingerprint, "127.0.0.1", port,
                "Known peer", ["known-local"], DateTime.UtcNow.Ticks)
        }));
        await using var service = new LocalSendService(settings, new BackgroundTaskManager());
        service.ScanInterfacesOverride = () => [new LocalSendService.ScanInterface("127.0.0.1", 32, "known-local")];
        service.DiscoveryAddressesOverride = () => [IPAddress.Loopback];
        await service.StartAsync();
        // No DiscoverAsync call: the third response fails, and the fifth times out.
        await sixth.Task.WaitAsync(TimeSpan.FromSeconds(45));
        DateTime[] observed;
        lock (calls) observed = calls.Take(6).ToArray();
        var gaps = Enumerable.Range(1, 5).Select(index => (observed[index] - observed[index - 1]).TotalSeconds).ToArray();
        Console.WriteLine($"Known registrations without menu: {string.Join(", ", gaps.Select(gap => gap.ToString("F2")))} seconds; third returned 503, fifth timed out");
        Assert.All(gaps.Take(4), gap => Assert.InRange(gap, 4, 11));
        Assert.InRange(gaps[4], 8, 14);
        Assert.Equal(0, service.DiscoveryStats.UnknownProbes);
        Assert.InRange(service.DiscoveryStats.PeakHttp, 1, 4);

        await service.SetEnabledAsync(false);
        var countAfterStop = calls.Count;
        await Task.Delay(TimeSpan.FromSeconds(6));
        Assert.Equal(countAfterStop, calls.Count);
        Assert.Equal(0, service.ListeningPort);
    }

    [Fact]
    public async Task FastKnownRegistrationIsLimitedToEightVerifiedDevices()
    {
        var fingerprints = new Dictionary<int, string>();
        var calls = new Dictionary<int, List<DateTime>>();
        var allFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options =>
        {
            for (var index = 0; index < 9; index++) options.Listen(IPAddress.Loopback, 0);
        });
        await using var app = builder.Build();
        app.MapPost("/api/localsend/v2/register", (HttpContext context) =>
        {
            string fingerprint;
            lock (calls)
            {
                var port = context.Connection.LocalPort;
                fingerprint = fingerprints[port];
                if (!calls.TryGetValue(port, out var times)) calls[port] = times = [];
                times.Add(DateTime.UtcNow);
                if (calls.Count == 9) allFirst.TrySetResult();
            }
            return Results.Json(new { alias = "Known HTTP peer", version = "2.2", fingerprint });
        });
        await app.StartAsync();
        var ports = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.Select(address => new Uri(address).Port).ToArray();
        Assert.Equal(9, ports.Length);
        for (var index = 0; index < ports.Length; index++) fingerprints[ports[index]] = $"known-http-{index}";

        foreach (var port in ports)
        {
            using var inbound = await _client.PostAsJsonAsync(Url("register"), new
            { alias = "Known HTTP peer", version = "2.2", fingerprint = fingerprints[port], port, protocol = "http" });
            Assert.Equal(HttpStatusCode.OK, inbound.StatusCode);
        }
        // No DiscoverAsync call: every peer is verified in-process by background registration.
        await allFirst.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await Task.Delay(TimeSpan.FromSeconds(10));
        int[] observed;
        lock (calls)
        {
            observed = ports.Select(port => calls[port].Count).ToArray();
            Console.WriteLine($"Nine verified peers after ten seconds: {string.Join(", ", ports.Select(port => $"{port}={string.Join("/", calls[port].Select(time => time.ToString("HH:mm:ss.fff")))}"))}");
        }
        Assert.Equal(8, observed.Count(count => count >= 2));
        Assert.Single(observed, count => count == 1);
        Assert.Equal(0, _service.DiscoveryStats.UnknownProbes);
        Assert.InRange(_service.DiscoveryStats.PeakHttp, 1, 4);
    }

    [Fact]
    public async Task HttpAnnouncementKeepsLegacyUnencryptedRegistrationBounded()
    {
        const string fingerprint = "legacy-http-fingerprint";
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.MapPost("/api/localsend/v2/register", () =>
        {
            registered.TrySetResult();
            return Results.Json(new { alias = "HTTP peer", version = "2.2", fingerprint });
        });
        await app.StartAsync();
        var port = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses)).Port;

        using var inbound = await _client.PostAsJsonAsync(Url("register"), new { alias = "HTTP peer",
            version = "2.2", fingerprint, port, protocol = "http" });
        Assert.Equal(HttpStatusCode.OK, inbound.StatusCode);
        await registered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Contains(await _service.DiscoverAsync(TestContext.Current.CancellationToken),
            device => device.Fingerprint == fingerprint && device.Protocol == "http");
        Assert.Equal(0, _settings.HistoryWrites);
        Assert.InRange(_service.DiscoveryStats.PeakHttp, 1, 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateOldAddressRegistrationDoesNotReplaceNewVerifiedAddress(bool oldFails)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=LocalSend moving peer", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldPort = 0;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate));
            options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate));
        });
        await using var app = builder.Build();
        app.MapGet("/api/localsend/v2/info", () => new { alias = "Moving peer", version = "2.2", fingerprint });
        app.MapPost("/api/localsend/v2/register", async (HttpContext context) =>
        {
            if (context.Connection.LocalPort == oldPort)
            {
                oldStarted.TrySetResult();
                await releaseOld.Task.WaitAsync(context.RequestAborted);
                oldReturned.TrySetResult();
                if (oldFails) return Results.StatusCode(503);
            }
            return Results.Json(new { alias = "Moving peer", version = "2.2", fingerprint });
        });
        await app.StartAsync();
        var ports = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.Select(address => new Uri(address).Port).ToArray();
        Assert.Equal(2, ports.Length);
        oldPort = ports[0];
        var newPort = ports[1];

        using var inbound = await _client.PostAsJsonAsync(Url("register"), new { alias = "Moving peer",
            version = "2.2", fingerprint, port = oldPort, protocol = "https" });
        Assert.Equal(HttpStatusCode.OK, inbound.StatusCode);
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(8));
        var moved = await _service.ConnectByAddressAsync("127.0.0.1", newPort, TestContext.Current.CancellationToken);
        Assert.Equal(newPort, moved.Port);
        releaseOld.TrySetResult();
        await oldReturned.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(300);
        var matching = (await _service.DiscoverAsync(TestContext.Current.CancellationToken))
            .Where(device => device.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Equal(newPort, Assert.Single(matching).Port);
    }

    [Fact]
    public async Task UnknownProbeRateAndConcurrencyStayWithinBudget()
    {
        var addresses = Enumerable.Range(1, 96).Select(index => $"127.0.0.{index}").ToArray();
        var scan = _service.ScanAddressesAsync(addresses, 65001, TestContext.Current.CancellationToken);
        await Task.Delay(1000);
        var firstSecond = _service.DiscoveryStats.UnknownProbes;
        Assert.InRange(firstSecond, 1, 64);
        await scan;
        var stats = _service.DiscoveryStats;
        Console.WriteLine($"LocalSend probes: firstSecond={firstSecond}, total={stats.UnknownProbes}, peakTcp={stats.PeakTcp}, peakHttp={stats.PeakHttp}");
        Assert.InRange(stats.PeakTcp, 1, 16);
        Assert.InRange(stats.PeakHttp, 0, 4);
    }

    [Fact]
    public async Task RepeatedDisposeCannotRestartListener()
    {
        await _service.DisposeAsync();
        await _service.DisposeAsync();
        Assert.Equal(0, _service.ListeningPort);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _service.StartAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _service.SetEnabledAsync(true));
    }

    [Fact]
    public async Task StopAndNetworkRebuildDrainProbesAndDoNotKeepOldGenerationAlive()
    {
        var addresses = Enumerable.Range(1, 96).Select(index => $"127.0.0.{index}").ToArray();
        var scan = _service.ScanAddressesAsync(addresses, 65001, TestContext.Current.CancellationToken);
        for (var attempt = 0; attempt < 50 && _service.DiscoveryStats.UnknownProbes < 10; attempt++)
            await Task.Delay(20);
        Assert.True(_service.DiscoveryStats.UnknownProbes >= 10);
        await _service.SetEnabledAsync(false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        var stoppedCount = _service.DiscoveryStats.UnknownProbes;
        Assert.Equal(0, _service.ListeningPort);
        Assert.Equal(0, _service.DiscoveryUdpPort);
        await Task.Delay(300);
        Assert.Equal(stoppedCount, _service.DiscoveryStats.UnknownProbes);

        await _service.SetEnabledAsync(true);
        var first = _service.DiscoveryCoordinator;
        Assert.NotNull(first);
        await _service.RejoinDiscoveryAsync();
        Assert.True(first.IsCompleted);
        var second = _service.DiscoveryCoordinator;
        Assert.NotSame(first, second);
        await _service.RejoinDiscoveryAsync();
        Assert.True(second!.IsCompleted);
        Assert.NotSame(second, _service.DiscoveryCoordinator);
        await _service.SetEnabledAsync(false);
        Assert.Equal(0, _service.ListeningPort);
        Assert.Equal(0, _service.DiscoveryUdpPort);
    }

    [Fact]
    public async Task NetworkChangeBurstCannotRestartAfterStop()
    {
        var first = _service.DiscoveryCoordinator;
        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() =>
            _service.OnNetworkAddressChanged(null, EventArgs.Empty))));
        for (var attempt = 0; attempt < 50 && ReferenceEquals(first, _service.DiscoveryCoordinator); attempt++)
            await Task.Delay(20);
        Assert.True(first!.IsCompleted);
        Assert.NotSame(first, _service.DiscoveryCoordinator);
        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() =>
            _service.OnNetworkAddressChanged(null, EventArgs.Empty))));
        await _service.SetEnabledAsync(false);
        _service.OnNetworkAddressChanged(null, EventArgs.Empty);
        await Task.Delay(600);
        Assert.Equal(0, _service.ListeningPort);
        Assert.Equal(0, _service.DiscoveryUdpPort);
        Assert.Null(_service.DiscoveryCoordinator);
    }

    [Fact]
    public async Task ConcurrentStartKeepsOneListenerAndIdentity()
    {
        var port = _service.ListeningPort;
        var fingerprint = _service.Fingerprint;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _service.StartAsync()));
        Assert.Equal(port, _service.ListeningPort);
        Assert.Equal(fingerprint, _service.Fingerprint);
    }

    [Fact]
    public async Task OccupiedDefaultPortFallsBackToAnAdvertisedDynamicPort()
    {
        await _service.SetEnabledAsync(false);
        var occupant = new TcpListener(IPAddress.Any, 53317);
        try
        {
            try { occupant.Start(); }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                // Another LocalSend client already occupies the default port.
            }
            await _service.SetEnabledAsync(true);
            Assert.NotEqual(53317, _service.ListeningPort);
            using var response = await _client.GetAsync(Url("info"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(_service.ListeningPort, json.RootElement.GetProperty("port").GetInt32());
        }
        finally { occupant.Stop(); }
    }

    [Fact]
    public async Task ManualIpConnectionAcceptsOfficialInfoWithoutPortAndProtocolOnlyAfterCertificateMatch()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=LocalSend test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var actualFingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        var returnedFingerprint = new string('A', 64);
        var registerCount = 0;
        var holdInfo = false;
        var infoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        app.MapGet("/api/localsend/v2/info", async (HttpContext context) =>
        {
            if (holdInfo)
            {
                infoStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            return Results.Json(new { alias = "Official style peer", version = "2.1", fingerprint = returnedFingerprint });
        });
        app.MapPost("/api/localsend/v2/register", () =>
        {
            Interlocked.Increment(ref registerCount);
            return new { alias = "Official style peer", version = "2.1", fingerprint = returnedFingerprint };
        });
        await app.StartAsync();
        var server = app.Services.GetRequiredService<IServer>();
        var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
        var port = new Uri(address).Port;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _service.ConnectByAddressAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
        Assert.Equal(0, registerCount);

        returnedFingerprint = actualFingerprint;
        var peer = await _service.ConnectByAddressAsync("127.0.0.1", port, TestContext.Current.CancellationToken);
        Assert.Equal("Official style peer", peer.Alias);
        Assert.Equal(port, peer.Port);
        Assert.Equal(actualFingerprint, peer.Fingerprint, ignoreCase: true);
        Assert.Equal(1, registerCount);
        Assert.Empty(_tasks.Tasks);
        Assert.Contains(await _service.DiscoverAsync(TestContext.Current.CancellationToken), device => device.Fingerprint == peer.Fingerprint);

        holdInfo = true;
        var interrupted = _service.ConnectByAddressAsync("127.0.0.1", port, TestContext.Current.CancellationToken);
        await infoStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await _service.SetEnabledAsync(false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted);
    }

    [Fact]
    public async Task SendSingleDirectoryZeroByteAndCollisionOverPinnedHttps()
    {
        var source = Path.Combine(_root, "Documents", "资料");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "中文长名称.txt"), "hello");
        await File.WriteAllBytesAsync(Path.Combine(source, "zero.dat"), []);
        var device = new LocalSendDevice("self", _service.Fingerprint!, "127.0.0.1", _service.ListeningPort, "https");
        await _service.SendAsync(device, [source]);
        var target = Path.Combine(_root, "Downloads", "资料");
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(target, "nested", "中文长名称.txt")));
        Assert.Empty(await File.ReadAllBytesAsync(Path.Combine(target, "zero.dat")));
        Assert.False(Directory.Exists(Path.Combine(target, "empty")));
        await _service.SendAsync(device, [source]);
        Assert.True(File.Exists(Path.Combine(target, "nested", "中文长名称 2.txt")));
        Assert.Contains(_tasks.Tasks, task => task.Label == "发送到 self" && task.State == BackgroundTaskState.Failed
            && task.ErrorMessage!.Contains("跳过"));
    }

    [Fact]
    public async Task RejectsTraversalAndWrongTokenThenAcceptsCorrectUpload()
    {
        using (var invalid = await _client.PostAsJsonAsync(Url("prepare-upload"), PrepareBody(("a", "../escape.txt", 3, null))))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var (session, tokens) = await PrepareAsync(("a", "safe/ok.txt", 3, null));
        Assert.Equal(HttpStatusCode.Forbidden, await UploadAsync(session, "a", "wrong", "abc"u8.ToArray()));
        Assert.Equal(HttpStatusCode.OK, await UploadAsync(session, "a", tokens["a"], "abc"u8.ToArray()));
        Assert.Equal("abc", await File.ReadAllTextAsync(Path.Combine(_root, "Downloads", "safe", "ok.txt")));
        Assert.Equal(HttpStatusCode.Conflict, await UploadAsync(session, "a", tokens["a"], "abc"u8.ToArray()));
    }

    [Fact]
    public async Task LengthAndHashFailureReleaseTheSessionAndCleanTempFiles()
    {
        var (session, tokens) = await PrepareAsync(("a", "bad.txt", 4, null));
        Assert.Equal((HttpStatusCode)422, await UploadAsync(session, "a", tokens["a"], "abc"u8.ToArray()));
        Assert.False(File.Exists(Path.Combine(_root, "Downloads", "bad.txt")));
        var hash = Convert.ToHexString(SHA256.HashData("other"u8.ToArray())).ToLowerInvariant();
        (session, tokens) = await PrepareAsync(("b", "bad-hash.txt", 3, hash));
        Assert.Equal((HttpStatusCode)422, await UploadAsync(session, "b", tokens["b"], "abc"u8.ToArray()));
        Assert.False(File.Exists(Path.Combine(_root, "Downloads", "bad-hash.txt")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "Downloads"), "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task BusyRejectionAndDisableCancelPendingRequest()
    {
        var waiting = new TaskCompletionSource<LocalSendReceiveDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.ConfirmReceiveAsync = (_, token) =>
        {
            entered.TrySetResult();
            return waiting.Task.WaitAsync(token);
        };
        using var firstCancellation = new CancellationTokenSource();
        var first = _client.PostAsJsonAsync(Url("prepare-upload"), PrepareBody(("a", "pending.txt", 1, null)), firstCancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (var busy = await _client.PostAsJsonAsync(Url("prepare-upload"), PrepareBody(("b", "busy.txt", 1, null))))
            Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        await _service.SetEnabledAsync(false);
        try { using var result = await first; Assert.NotEqual(HttpStatusCode.OK, result.StatusCode); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
        Assert.False(File.Exists(Path.Combine(_root, "Downloads", "pending.txt")));
        await _service.SetEnabledAsync(true);
        Assert.True(_service.ListeningPort > 0);
    }

    [Fact]
    public async Task SenderCanCancelWhileReceiverIsConfirmingWithoutSessionId()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.ConfirmReceiveAsync = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new LocalSendReceiveDecision(false, _service.ReceiveDirectory);
        };
        var prepare = _client.PostAsJsonAsync(Url("prepare-upload"), PrepareBody(("a", "pending.txt", 1, null)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (var wrong = await _client.PostAsync(Url("cancel?sessionId=wrong"), null))
            Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        using (var cancelled = await _client.PostAsync(Url("cancel"), null))
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        using (var result = await prepare.WaitAsync(TimeSpan.FromSeconds(5)))
            Assert.NotEqual(HttpStatusCode.OK, result.StatusCode);
        _service.ConfirmReceiveAsync = (request, _) => Task.FromResult(new LocalSendReceiveDecision(true, request.DefaultDirectory));
        var (session, _) = await PrepareAsync(("b", "next.txt", 1, null));
        using (var missingId = await _client.PostAsync(Url("cancel"), null))
            Assert.Equal(HttpStatusCode.Conflict, missingId.StatusCode);
        using (var cancelledSession = await _client.PostAsync(Url("cancel?sessionId=" + session), null))
            Assert.Equal(HttpStatusCode.OK, cancelledSession.StatusCode);
    }

    [Fact]
    public async Task ReceivesLargeFileBeyondDefaultKestrelLimit()
    {
        var content = new byte[31 * 1024 * 1024];
        RandomNumberGenerator.Fill(content);
        var (session, tokens) = await PrepareAsync(("a", "large.bin", content.Length, null));
        Assert.Equal(HttpStatusCode.OK, await UploadAsync(session, "a", tokens["a"], content));
        Assert.Equal(content.Length, new FileInfo(Path.Combine(_root, "Downloads", "large.bin")).Length);
    }

    [Fact]
    public async Task OversizedMetadataIsRejectedWithoutOrphanTask()
    {
        var size = long.MaxValue / 2;
        using var response = await _client.PostAsJsonAsync(Url("prepare-upload"), PrepareBody(
            ("a", "a", size, null), ("b", "b", size, null), ("c", "c", size, null)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_tasks.Tasks);
        _ = await PrepareAsync(("d", "good.txt", 1, null));
    }

    [Fact]
    public async Task PartialAcceptanceAndRejectedRequest()
    {
        var first = Path.Combine(_root, "Documents", "first.txt");
        var second = Path.Combine(_root, "Documents", "second.txt");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");
        _service.ConfirmReceiveAsync = (request, _) => Task.FromResult(
            new LocalSendReceiveDecision(true, request.DefaultDirectory, ["0"]));
        var device = new LocalSendDevice("self", _service.Fingerprint!, "127.0.0.1", _service.ListeningPort, "https");
        await _service.SendAsync(device, [first, second]);
        Assert.True(File.Exists(Path.Combine(_root, "Downloads", "first.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "Downloads", "second.txt")));
        Assert.Contains(_tasks.Tasks, task => task.Label == "发送到 self" && task.State == BackgroundTaskState.Failed
            && task.ErrorMessage!.Contains("拒绝 1"));
        _service.ConfirmReceiveAsync = (request, _) => Task.FromResult(new LocalSendReceiveDecision(false, request.DefaultDirectory));
        using var rejected = await _client.PostAsJsonAsync(Url("prepare-upload"), PrepareBody(("x", "reject.txt", 1, null)));
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.False(File.Exists(Path.Combine(_root, "Downloads", "reject.txt")));
    }

    [Fact]
    public async Task ParallelUploadsAndCancellationKeepCompletedFiles()
    {
        var (session, tokens) = await PrepareAsync(("a", "a.txt", 1, null), ("b", "b.txt", 1, null));
        var uploads = await Task.WhenAll(
            UploadAsync(session, "a", tokens["a"], "a"u8.ToArray()),
            UploadAsync(session, "b", tokens["b"], "b"u8.ToArray()));
        Assert.All(uploads, code => Assert.Equal(HttpStatusCode.OK, code));
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(_root, "Downloads", "a.txt")));
        Assert.Equal("b", await File.ReadAllTextAsync(Path.Combine(_root, "Downloads", "b.txt")));

        (session, tokens) = await PrepareAsync(("c", "c.txt", 1, null), ("d", "d.txt", 1, null));
        Assert.Equal(HttpStatusCode.OK, await UploadAsync(session, "c", tokens["c"], "c"u8.ToArray()));
        using var cancelled = await _client.PostAsync(Url("cancel?sessionId=" + session), null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("c", await File.ReadAllTextAsync(Path.Combine(_root, "Downloads", "c.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "Downloads", "d.txt")));
        _ = await PrepareAsync(("e", "e.txt", 1, null));
    }

    [Fact]
    public async Task CancellingReceiveTaskKeepsCompletedFilesAndShowsTheirCount()
    {
        var (session, tokens) = await PrepareAsync(("a", "kept.txt", 1, null), ("b", "cancelled.txt", 1, null));
        Assert.Equal(HttpStatusCode.OK, await UploadAsync(session, "a", tokens["a"], "a"u8.ToArray()));
        var task = Assert.Single(_tasks.Tasks);
        _tasks.CancelTask(task.Id);
        Assert.Equal(BackgroundTaskState.Cancelled, task.State);
        Assert.Contains("1/2", task.CurrentFile);
        Assert.Equal(HttpStatusCode.Conflict, await UploadAsync(session, "b", tokens["b"], "b"u8.ToArray()));
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(_root, "Downloads", "kept.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "Downloads", "cancelled.txt")));
    }

    [Fact]
    public async Task AbortedUploadReleasesSessionAndKeepsCompletedFile()
    {
        var (session, tokens) = await PrepareAsync(("a", "kept.txt", 1, null),
            ("b", "aborted.txt", 1024 * 1024, null));
        Assert.Equal(HttpStatusCode.OK, await UploadAsync(session, "a", tokens["a"], "a"u8.ToArray()));
        using var content = new PausedUploadContent();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            Url($"upload?sessionId={session}&fileId=b&token={tokens["b"]}")) { Content = content };
        using var cancellation = new CancellationTokenSource();
        var upload = _client.SendAsync(request, cancellation.Token);
        await content.Started.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        content.Release();
        try { using var response = await upload; }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException) { }

        var task = Assert.Single(_tasks.Tasks);
        for (var attempt = 0; attempt < 50 && task.State == BackgroundTaskState.Running; attempt++)
            await Task.Delay(100);
        Assert.Equal(BackgroundTaskState.Failed, task.State);
        Assert.Contains("中断", task.ErrorMessage);
        Assert.Contains("1/2", task.ErrorMessage);
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(_root, "Downloads", "kept.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "Downloads", "aborted.txt")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "Downloads"), "*.part", SearchOption.AllDirectories));
        _ = await PrepareAsync(("c", "next.txt", 1, null));
    }

    [Fact(Explicit = true)]
    public async Task SendsToOfficialLocalSendClient()
    {
        // Manual interop check: start the official app first and accept its receive dialog.
        var alias = Environment.GetEnvironmentVariable("FK_LOCALSEND_QA_ALIAS") ?? "LocalSend QA";
        var file = Path.Combine(_root, "Documents", "MacExplorer-to-LocalSend.txt");
        await File.WriteAllTextAsync(file, "Mac Explorer official LocalSend interop", TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var device = Assert.Single((await _service.DiscoverAsync(timeout.Token)).Where(item => item.Alias == alias));
        await _service.SendAsync(device, [file]).WaitAsync(timeout.Token);
        Assert.Contains(_tasks.Tasks, task => task.Label == "发送到 " + alias && task.State == BackgroundTaskState.Completed);
    }

    [Fact]
    public async Task SkipsSymlinkAndFifoWithoutOpeningThem()
    {
        var directory = Path.Combine(_root, "Documents", "mixed");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "ordinary.txt"), "ok");
        File.CreateSymbolicLink(Path.Combine(directory, "link.txt"), Path.Combine(directory, "ordinary.txt"));
        using (var process = Process.Start(new ProcessStartInfo("mkfifo")
        {
            ArgumentList = { Path.Combine(directory, "pipe") },
            RedirectStandardError = true
        })!) await process.WaitForExitAsync();
        var device = new LocalSendDevice("self", _service.Fingerprint!, "127.0.0.1", _service.ListeningPort, "https");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _service.SendAsync(device, [directory]).WaitAsync(timeout.Token);
        Assert.True(File.Exists(Path.Combine(_root, "Downloads", "mixed", "ordinary.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "Downloads", "mixed", "link.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "Downloads", "mixed", "pipe")));
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new();
        public int HistoryWrites { get; private set; }
        public event Action<string>? SettingChanged;
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public T Get<T>(string key, T defaultValue)
        {
            if (!_values.TryGetValue(key, out var value)) return defaultValue;
            return typeof(T) == typeof(bool) ? (T)(object)bool.Parse(value) : (T)(object)value;
        }
        public void Set(string key, string value)
        {
            _values[key] = value;
            if (key == ILocalSendService.VerifiedPeersKey) HistoryWrites++;
            SettingChanged?.Invoke(key);
        }
        public void Set<T>(string key, T value) => Set(key, value?.ToString() ?? "");
        public Dictionary<string, string> GetAll() => new(_values);
    }

    private sealed class PausedUploadContent : HttpContent
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => _started.Task;
        public void Release() => _release.TrySetResult();
        protected override bool TryComputeLength(out long length) { length = 1024 * 1024; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        {
            await stream.WriteAsync(new byte[64 * 1024], token);
            await stream.FlushAsync(token);
            _started.TrySetResult();
            await _release.Task.WaitAsync(token);
        }
    }
}
