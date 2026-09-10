using System.Diagnostics;
using System.Net;
using System.Text.Json;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

public sealed class StartupUpdateTests
{
    [Theory]
    [InlineData("999.0.0", true)]
    [InlineData("0.0.0", false)]
    public async Task WorkerReturnsOnlyNewerVersionsWithoutInstalling(string version, bool available)
    {
        using var http = new HttpClient(new VersionResponseHandler(version));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await StartupUpdateChecker.RunWorkerAsync(new AppUpdateService(http), output, error);
        Assert.Equal(0, code);
        Assert.Empty(error.ToString());
        var result = JsonSerializer.Deserialize<VersionInfo>(output.ToString());
        if (available) Assert.Equal(version, result!.Version);
        else Assert.Null(result);
    }

    [Fact]
    public async Task WorkerReportsConnectionFailureWithoutProducingAnUpdate()
    {
        using var http = new HttpClient(new VersionResponseHandler(null));
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await StartupUpdateChecker.RunWorkerAsync(new AppUpdateService(http), output, error));
        Assert.Empty(output.ToString());
        Assert.NotEmpty(error.ToString());
    }

    [Fact]
    public async Task ChildActuallyRunsAtLowPriority()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
        var start = StartupUpdateChecker.CreateStartInfo("/bin/sh", "unused");
        start.ArgumentList.RemoveAt(start.ArgumentList.Count - 1);
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("printf '{\"VERSION\":\"%s\"}' \"$(ps -o nice= -p $$ | tr -d ' ')\"");
        var result = await StartupUpdateChecker.CheckAsync(start, TestContext.Current.CancellationToken);
        Assert.Equal("19", result!.Version);
    }

    [Fact]
    public async Task CancellingCheckStopsItsChildProcess()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
        var pidFile = Path.GetTempFileName();
        try
        {
            var start = StartupUpdateChecker.CreateStartInfo("/bin/sh", "unused");
            start.ArgumentList.RemoveAt(start.ArgumentList.Count - 1);
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("echo $$ > \"$1\"; exec sleep 30");
            start.ArgumentList.Add("update-test");
            start.ArgumentList.Add(pidFile);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var check = StartupUpdateChecker.CheckAsync(start, cancellation.Token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            string pid;
            while (string.IsNullOrWhiteSpace(pid = await File.ReadAllTextAsync(pidFile, deadline.Token)))
                await Task.Delay(10, deadline.Token);
            using var child = Process.GetProcessById(int.Parse(pid));
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
            await child.WaitForExitAsync(deadline.Token);
            Assert.True(child.HasExited);
        }
        finally { File.Delete(pidFile); }
    }

    private sealed class VersionResponseHandler(string? version) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (version == null) throw new HttpRequestException("offline");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new VersionCheckResponse
                {
                    Success = true,
                    Data = new VersionInfo { Version = version, Memo = "更新内容" }
                }))
            });
        }
    }
}
