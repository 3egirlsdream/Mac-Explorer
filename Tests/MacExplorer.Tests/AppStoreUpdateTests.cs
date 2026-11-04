using System.Net;
using System.Text.Json;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Area", "AppUpdate")]
[Trait("Area", "AppStoreUpdate")]
[Trait("Kind", "Unit")]
public sealed class AppStoreUpdateTests
{
    [Fact]
    public void FactorySelectsTheBuildChannel()
    {
        using var http = new HttpClient();
        var service = AppUpdateService.Create(http);
        if (DistributionChannel.IsAppStore)
            Assert.IsType<AppStoreUpdateService>(service);
        else
        {
            Assert.IsType<AppUpdateService>(service);
            Assert.Null(service.UpdatePageUri);
        }
    }

    [Theory]
    [InlineData("999.0.0", true)]
    [InlineData("0.0.0", false)]
    [InlineData(null, false)]
    public async Task ChecksAppleVersionAndKeepsStoreReleaseNotes(string? version, bool available)
    {
        var handler = new LookupHandler();
        using var http = new HttpClient(handler);
        var service = new AppStoreUpdateService(http);
        handler.Json = Response(version ?? service.CurrentVersion);
        var details = await service.GetVersionDetailsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(version ?? service.CurrentVersion, details!.Version);
        Assert.Equal("商店更新说明\n第二行", details.Memo);
        Assert.Equal("2026-11-04T05:54:11Z", details.DateTime);
        Assert.Empty(details.Path);
        Assert.Null(details.History);
        Assert.Equal(available, await service.CheckVersionAsync(TestContext.Current.CancellationToken) != null);
        Assert.Equal("https", handler.LastUri!.Scheme);
        Assert.Equal("itunes.apple.com", handler.LastUri.Host);
        Assert.Contains("id=6820338798", handler.LastUri.Query);
        Assert.Contains("entity=macSoftware", handler.LastUri.Query);
        Assert.Contains("country=", handler.LastUri.Query);
        Assert.Equal("macappstore://itunes.apple.com/app/id6820338798?mt=12", service.UpdatePageUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("{\"resultCount\":0,\"results\":[]}")]
    [InlineData("{\"results\":[{\"trackId\":6820338798,\"bundleId\":\"other.app\",\"version\":\"999.0\"}]}")]
    [InlineData("{\"results\":[{\"trackId\":1,\"bundleId\":\"com.thankful.top.macexplorer.store\",\"version\":\"999.0\"}]}")]
    [InlineData("{\"results\":[{\"trackId\":6820338798,\"bundleId\":\"com.thankful.top.macexplorer.store\",\"version\":\"unknown\"}]}")]
    public async Task MissingOrInvalidStoreListingIsAnErrorInsteadOfAlreadyLatest(string json)
    {
        using var http = new HttpClient(new LookupHandler { Json = json });
        var service = new AppStoreUpdateService(http);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CheckVersionAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NetworkErrorsAndCancellationDoNotProduceAnUpdate()
    {
        using var http = new HttpClient(new LookupHandler { StatusCode = HttpStatusCode.ServiceUnavailable });
        var service = new AppStoreUpdateService(http);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.CheckVersionAsync(TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckVersionAsync(cancelled.Token));
    }

    [Fact]
    public async Task StoreServiceCannotDownloadOrInstallWebsitePackages()
    {
        var handler = new LookupHandler();
        using var http = new HttpClient(handler);
        var service = new AppStoreUpdateService(http);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.DownloadAndInstallAsync(
            new VersionInfo { Version = "999.0", Path = "https://example.com/update.zip" }));
        Assert.Null(handler.LastUri);
    }

    private static string Response(string version) => JsonSerializer.Serialize(new
    {
        resultCount = 1,
        results = new[] { new { trackId = 6820338798L, bundleId = "com.thankful.top.macexplorer.store", version,
            releaseNotes = "商店更新说明\n第二行", currentVersionReleaseDate = "2026-11-04T05:54:11Z" } }
    });

    private sealed class LookupHandler : HttpMessageHandler
    {
        public string Json { get; set; } = "{}";
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;
        public Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(StatusCode) { Content = new StringContent(Json) });
        }
    }
}
