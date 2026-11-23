using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Integration")]
[Trait("Area", "Network")]
[Trait("Serial", "true")]
public sealed class WebDavNativeTests
{
    [Fact(Explicit = true)]
    public async Task IsolatedAppMountsBrowsesWritesAndDisconnectsWithNativeAuthenticationAndTlsChecks()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Requires the macOS WebDAV filesystem.");
        var configuration = typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .Cast<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration;
        Assert.SkipWhen(configuration != "Debug", "The isolated WebDAV harness is only included in Debug builds.");
        var python = Environment.GetEnvironmentVariable("MACEXPLORER_WEBDAV_TEST_PYTHON");
        Assert.SkipWhen(string.IsNullOrEmpty(python), "Set MACEXPLORER_WEBDAV_TEST_PYTHON to an isolated WsgiDAV QA virtualenv.");
        var repository = NativeHarnessRunner.FindRepositoryRoot();
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var executable = Path.Combine(repository, $"bin/Debug/net10.0/osx-{architecture}/Mac Explorer.app/Contents/MacOS/MacExplorer");
        Assert.True(File.Exists(executable), "Build the Debug app bundle before running native WebDAV checks.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(160));
        var text = await NativeHarnessRunner.RunAsync(
            python!,
            [Path.Combine(repository, "Tools/Testing/webdav-smoke.py"), executable],
            timeout.Token,
            includeStderrInOutput: true);
        Assert.Contains("two-window-sidebar-synchronized", text);
        Assert.Contains("untrusted-tls-rejected", text);
        Assert.Contains("uploaded-renamed-deleted", text);
        Assert.Contains("busy-eject-preserves-connection", text);
        Assert.Contains("ejected", text);
    }
}
