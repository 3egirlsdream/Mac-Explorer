using System.IO.Compression;
using System.Net;
using System.Text.Json;
using MacExplorer.PluginSdk;
using MacExplorer.Services.Plugins;
using Xunit;

namespace MacExplorer.Tests;

public class PluginTrialTests
{
    private static string Paid(PluginTestEnvironment env, string version, int days)
    {
        var package = env.CreateFixture(version, 2);
        using var zip = ZipFile.Open(package, ZipArchiveMode.Update);
        var entry = zip.GetEntry("plugin.json")!;
        PluginManifest manifest;
        using (var input = entry.Open()) manifest = JsonSerializer.Deserialize<PluginManifest>(input, PluginProtocol.Json)!;
        entry.Delete();
        using var output = zip.CreateEntry("plugin.json").Open();
        JsonSerializer.Serialize(output, manifest with { Paid = true, TrialDays = days }, PluginProtocol.Json);
        return package;
    }

    [Fact]
    public async Task TrialSurvivesUninstallRestartUpgradeAndClockRollback()
    {
        using var env = new PluginTestEnvironment();
        var original = Paid(env, "1.0.0", 7); await env.Manager.InstallAsync(original);
        var start = DateTimeOffset.UtcNow;
        Assert.Null(env.Manager.GetTrial("test.fixture").StartedAt);
        var trial = env.Manager.GetTrial("test.fixture", true, start);
        await env.Manager.UninstallAsync("test.fixture");
        await env.Manager.InstallAsync(Paid(env, "2.0.0", 30));
        Assert.Equal(trial.ExpiresAt, env.Manager.GetTrial("test.fixture", true, start.AddDays(2)).ExpiresAt);
        await env.Manager.SetEnabledAsync("test.fixture", false);
        await env.Manager.SetEnabledAsync("test.fixture", true);
        var expired = env.Manager.GetTrial("test.fixture", now: start.AddDays(8));
        Assert.False(expired.Active);
        Assert.False(env.Manager.GetTrial("test.fixture", now: start).Active);
        await env.Manager.DisposeAsync();
        await using var restarted = env.NewManager(); await restarted.InitializeAsync();
        Assert.Equal(trial.StartedAt, restarted.GetTrial("test.fixture").StartedAt);
        Assert.False(restarted.GetTrial("test.fixture", true).Active);
    }

    [Fact]
    public async Task TrialStartsOnceUnderConcurrentRequests()
    {
        using var env = new PluginTestEnvironment(); await env.Manager.InstallAsync(Paid(env, "1.0.0", 3));
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => env.Manager.GetTrial("test.fixture", true))));
        Assert.Single(results.Select(r => r.StartedAt).Distinct());
        Assert.Single(results.Select(r => r.ExpiresAt).Distinct());
    }

    [Fact]
    public void CorruptLedgerDoesNotGrantNewTrial()
    {
        using var env = new PluginTestEnvironment(); env.Settings.Set("plugin_trials_v1", "broken");
        Assert.Throws<JsonException>(() => env.Manager.GetTrial("test.fixture", true));
    }

    [Fact]
    public async Task PaidPluginWithoutAuthorizationInterfaceFailsClosed()
    {
        using var env = new PluginTestEnvironment(); await env.Manager.InstallAsync(Paid(env, "1.0.0", 7));
        var files = new[] { new PluginFile(env.Write("input.txt", "original")) };
        await using var session = await env.Manager.StartAsync("test.fixture", "run", files);
        var access = await session.CallAsync<PluginAccessResult>("check-access", new PluginAccessRequest(new("call", "run", files, session.WorkDirectory), new()), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(PluginAccessStatus.Failed, access.Status);
    }

    [Fact]
    public async Task MarketHashMismatchNeverInstalls()
    {
        using var env = new PluginTestEnvironment(); var bytes = await File.ReadAllBytesAsync(env.CreateFixture());
        using var http = new HttpClient(new PackageHandler(bytes)); var client = new PluginMarketClient(http);
        var item = new PluginMarketItem(new() { Id = "test.fixture", Version = "1.0.0" }, "https://example.test/plugin", bytes.Length, new string('0',64));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.InstallAsync(item, env.Manager, new Progress<double?>(), CancellationToken.None));
        Assert.DoesNotContain(env.Manager.Plugins, p => p.Manifest.Id == "test.fixture");
        Assert.Empty(Directory.GetDirectories(env.Manager.RootDirectory, ".download-*"));
    }
    private sealed class PackageHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request });
    }
}
