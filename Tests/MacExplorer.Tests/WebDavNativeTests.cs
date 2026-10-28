using System.Diagnostics;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Integration")]
[Trait("Area", "Network")]
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
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "MacExplorer.csproj"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var executable = Path.Combine(repository.FullName, $"bin/Debug/net10.0/osx-{architecture}/Mac Explorer.app/Contents/MacOS/MacExplorer");
        Assert.True(File.Exists(executable), "Build the Debug app bundle before running native WebDAV checks.");
        var start = new ProcessStartInfo(python!) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(repository.FullName, "Tools/Testing/webdav-smoke.py"));
        start.ArgumentList.Add(executable);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(160));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var text = await output + await error;
        Assert.True(process.ExitCode == 0, text);
        Assert.Contains("two-window-sidebar-synchronized", text);
        Assert.Contains("untrusted-tls-rejected", text);
        Assert.Contains("uploaded-renamed-deleted", text);
        Assert.Contains("busy-eject-preserves-connection", text);
        Assert.Contains("ejected", text);
    }
}
