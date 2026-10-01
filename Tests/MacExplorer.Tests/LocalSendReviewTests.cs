using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

// RuntimePaths.TestRootVariable is process-wide, including for other test collections.
[CollectionDefinition("LocalSend review", DisableParallelization = true)]
public sealed class LocalSendReviewCollection { }

[Collection("LocalSend review")]
public sealed class LocalSendReviewTests : IAsyncLifetime
{
    private readonly string? _oldRoot = Environment.GetEnvironmentVariable(RuntimePaths.TestRootVariable);
    private readonly string _root = Path.Combine("/private/tmp", "fk-localsend-review-" + Guid.NewGuid().ToString("N"));
    private readonly BackgroundTaskManager _tasks = new();
    private LocalSendService _service = null!;
    private readonly DirectoryAccess _testAccess;
    public LocalSendReviewTests()
    {
        _testAccess = new DirectoryAccess(Path.Combine(_root, "access.json"), true, internalRoots: [_root]);
    }
    private readonly HttpClient _client = new(new HttpClientHandler
    {
        // Only this test fixture's loopback listener is contacted by this client.
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        UseProxy = false
    }) { Timeout = TimeSpan.FromSeconds(15) };

    public async ValueTask InitializeAsync()
    {
        Environment.SetEnvironmentVariable(RuntimePaths.TestRootVariable, _root);
        RuntimePaths.PrepareTestRoot();
        _service = new LocalSendService(new MemorySettings(), _tasks)
        {
            FileAccessOverride = _testAccess,
            ScanInterfacesOverride = () => [],
            DiscoveryAddressesOverride = () => [IPAddress.Loopback]
        };
        using (var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            _service.DiscoveryUdpPortOverride = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        _service.ConfirmReceiveAsync = (request, _) => Task.FromResult(new LocalSendReceiveDecision(true, request.DefaultDirectory));
        await _service.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        try { if (_service != null) await _service.DisposeAsync(); }
        finally
        {
            _testAccess.Dispose();
            Environment.SetEnvironmentVariable(RuntimePaths.TestRootVariable, _oldRoot);
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("foreign-ids")]
    [InlineData("null-map")]
    [InlineData("empty-token")]
    public async Task InvalidAcceptanceIsRejectedBeforeAnyUpload(string kind)
    {
        Dictionary<string, string?>? accepted = kind switch
        {
            // Same count as the offered files: this previously reported success with zero uploads.
            "foreign-ids" => new() { ["other-a"] = "a", ["other-b"] = "b" },
            "null-map" => null,
            // A valid first entry must not be uploaded before the later invalid entry is checked.
            _ => new() { ["0"] = "a", ["1"] = "" }
        };
        var peer = await StartPeerAsync(Results.Json(new { sessionId = "review-session", files = accepted }));
        await using var host = peer.App;
        await _service.SendAsync(peer.Device, await SourceFilesAsync()).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        var task = Assert.Single(_tasks.Tasks);
        Assert.Equal(BackgroundTaskState.Failed, task.State);
        Assert.Contains("文件接收列表", task.ErrorMessage!);
        Assert.Empty(peer.Uploads);
        Assert.Equal("review-session", Assert.Single(peer.Cancellations));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidFullAndPartialAcceptanceKeepExistingSemantics(bool partial)
    {
        var accepted = new Dictionary<string, string> { ["0"] = "first-token" };
        if (!partial) accepted["1"] = "second-token";
        var peer = await StartPeerAsync(Results.Json(new { sessionId = "review-session", files = accepted }));
        await using var host = peer.App;
        await _service.SendAsync(peer.Device, await SourceFilesAsync()).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        var task = Assert.Single(_tasks.Tasks);
        Assert.Equal(partial ? BackgroundTaskState.Failed : BackgroundTaskState.Completed, task.State);
        Assert.Equal(partial ? 1 : 2, peer.Uploads.Count);
        Assert.Equal("alpha", Encoding.UTF8.GetString(peer.Uploads["0"]));
        if (partial) Assert.Contains("拒绝 1", task.ErrorMessage!);
        else Assert.Equal("beta", Encoding.UTF8.GetString(peer.Uploads["1"]));
        Assert.Empty(peer.Cancellations);
    }

    [Fact]
    public async Task NoContentPreparationStillCompletesWithoutUploading()
    {
        var peer = await StartPeerAsync(Results.NoContent());
        await using var host = peer.App;
        await _service.SendAsync(peer.Device, await SourceFilesAsync()).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(BackgroundTaskState.Completed, Assert.Single(_tasks.Tasks).State);
        Assert.Empty(peer.Uploads);
        Assert.Empty(peer.Cancellations);
    }

    [Fact]
    public async Task OversizedPreparationReplyDoesNotStartUploading()
    {
        var peer = await StartPeerAsync(Results.Json(new
        {
            sessionId = "review-session",
            files = new Dictionary<string, string> { ["0"] = "a", ["1"] = "b" },
            // Valid JSON with valid IDs: failure must be the size budget, not JSON validation.
            padding = new string('x', 5 * 1024 * 1024)
        }));
        await using var host = peer.App;
        await _service.SendAsync(peer.Device, await SourceFilesAsync()).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(BackgroundTaskState.Failed, Assert.Single(_tasks.Tasks).State);
        Assert.Empty(peer.Uploads);
    }

    [Fact]
    public async Task ReplyBudgetDoesNotLimitTheUploadedFileSize()
    {
        var path = Path.Combine(_root, "Documents", "large.bin");
        var bytes = new byte[5 * 1024 * 1024];
        RandomNumberGenerator.Fill(bytes);
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        var peer = await StartPeerAsync(Results.Json(new
        {
            sessionId = "review-session", files = new Dictionary<string, string> { ["0"] = "a" }
        }));
        await using var host = peer.App;
        await _service.SendAsync(peer.Device, [path]).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(BackgroundTaskState.Completed, Assert.Single(_tasks.Tasks).State);
        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(Assert.Single(peer.Uploads).Value));
    }

    [Fact]
    public async Task CancelledConfirmationCannotPublishALateAcceptedDecision()
    {
        var target = Path.Combine(_root, "cancelled-receive");
        _service.ConfirmReceiveAsync = (_, token) =>
        {
            // Deterministic ordering: cancellation precedes the accepted callback result.
            _tasks.CancelTask(Assert.Single(_tasks.Tasks).Id);
            Assert.True(token.IsCancellationRequested);
            return Task.FromResult(new LocalSendReceiveDecision(true, target));
        };
        using var cancelled = await _client.PostAsJsonAsync(Url("prepare-upload"), ReceiveBody(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, cancelled.StatusCode);
        Assert.Equal(BackgroundTaskState.Cancelled, Assert.Single(_tasks.Tasks).State);
        Assert.False(Directory.Exists(target));

        // The cancelled reservation must not block the next sender.
        _service.ConfirmReceiveAsync = (request, _) => Task.FromResult(new LocalSendReceiveDecision(true, request.DefaultDirectory));
        using var next = await _client.PostAsJsonAsync(Url("prepare-upload"), ReceiveBody(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        using var json = JsonDocument.Parse(await next.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var id = json.RootElement.GetProperty("sessionId").GetString()!;
        using var cleanup = await _client.PostAsync(Url("cancel?sessionId=" + Uri.EscapeDataString(id)), null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, cleanup.StatusCode);
    }

    [Fact]
    public async Task CompletedFileCannotBeUploadedAgainWhileAnotherFileIsPending()
    {
        using var prepare = await _client.PostAsJsonAsync(Url("prepare-upload"), ReceiveBody(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, prepare.StatusCode);
        using var json = JsonDocument.Parse(await prepare.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var session = json.RootElement.GetProperty("sessionId").GetString()!;
        var tokens = json.RootElement.GetProperty("files");
        Assert.Equal(HttpStatusCode.OK, await UploadAsync("a", "a"));
        Assert.Equal(HttpStatusCode.Conflict, await UploadAsync("a", "x"));
        Assert.Equal(HttpStatusCode.OK, await UploadAsync("b", "b"));
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(_root, "Downloads", "a.txt"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(_root, "Downloads", "a 2.txt")));
        Assert.Equal("b", await File.ReadAllTextAsync(Path.Combine(_root, "Downloads", "b.txt"), TestContext.Current.CancellationToken));

        async Task<HttpStatusCode> UploadAsync(string id, string data)
        {
            var url = Url($"upload?sessionId={session}&fileId={id}&token={tokens.GetProperty(id).GetString()}");
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(data));
            using var response = await _client.PostAsync(url, content, TestContext.Current.CancellationToken);
            return response.StatusCode;
        }
    }

    private string Url(string path) => $"https://127.0.0.1:{_service.ListeningPort}/api/localsend/v2/{path}";

    private static object ReceiveBody() => new
    {
        info = new { alias = "Review sender", version = "2.2", fingerprint = new string('a', 64), port = 53400, protocol = "https" },
        files = new[] { "a", "b" }.ToDictionary(id => id, id => new
        {
            id, fileName = id + ".txt", size = 1L, fileType = "application/octet-stream"
        })
    };

    private async Task<string[]> SourceFilesAsync()
    {
        var first = Path.Combine(_root, "Documents", "first.txt");
        var second = Path.Combine(_root, "Documents", "second.txt");
        await File.WriteAllTextAsync(first, "alpha", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, "beta", TestContext.Current.CancellationToken);
        return [first, second];
    }

    private static async Task<(WebApplication App, LocalSendDevice Device,
        ConcurrentDictionary<string, byte[]> Uploads, ConcurrentQueue<string> Cancellations)> StartPeerAsync(IResult prepare)
    {
        var uploads = new ConcurrentDictionary<string, byte[]>();
        var cancellations = new ConcurrentQueue<string>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapPost("/api/localsend/v2/prepare-upload", () => prepare);
        app.MapPost("/api/localsend/v2/upload", async (HttpContext context) =>
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            uploads[context.Request.Query["fileId"].ToString()] = buffer.ToArray();
            return Results.Ok();
        });
        app.MapPost("/api/localsend/v2/cancel", (HttpContext context) =>
        {
            cancellations.Enqueue(context.Request.Query["sessionId"].ToString());
            return Results.Ok();
        });
        try
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            var server = app.Services.GetRequiredService<IServer>();
            var port = new Uri(Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses)).Port;
            return (app, new LocalSendDevice("Review peer", "review-peer", "127.0.0.1", port, "http"), uploads, cancellations);
        }
        catch { await app.DisposeAsync(); throw; }
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly ConcurrentDictionary<string, string> _values = new();
        public event Action<string>? SettingChanged;
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public T Get<T>(string key, T defaultValue)
        {
            if (!_values.TryGetValue(key, out var value)) return defaultValue;
            return typeof(T) == typeof(bool) ? (T)(object)bool.Parse(value) : (T)(object)value;
        }
        public void Set(string key, string value) { _values[key] = value; SettingChanged?.Invoke(key); }
        public void Set<T>(string key, T value) => Set(key, value?.ToString() ?? "");
        public Dictionary<string, string> GetAll() => new(_values);
    }
}
