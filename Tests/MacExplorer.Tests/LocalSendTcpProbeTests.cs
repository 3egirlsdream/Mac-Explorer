using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Integration")]
[Trait("Area", "LocalSend")]
public sealed class LocalSendTcpProbeTests
{
    [Fact(Explicit = true)]
    public async Task OfflineEndpointReturnsFailureWithoutExceptionNotifications()
    {
        // Reserve a local port without listening, so other processes cannot take it.
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)reserved.LocalEndPoint!;
        var exceptions = 0;
        void OnException(object? sender, FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is SocketException or OperationCanceledException) Interlocked.Increment(ref exceptions);
        }
        AppDomain.CurrentDomain.FirstChanceException += OnException;
        try
        {
            for (var i = 0; i < 20; i++)
            {
                var result = await LocalSendTcpProbe.ConnectAsync(endpoint, TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
                // A bound, non-listening port can reject or silently drop SYNs depending on the OS.
                Assert.Contains(result, new[] { SocketError.ConnectionRefused, SocketError.TimedOut });
            }
            Assert.Equal(SocketError.TimedOut, await LocalSendTcpProbe.ConnectAsync(endpoint, TimeSpan.Zero, CancellationToken.None));
            Assert.Equal(0, exceptions);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= OnException; }
    }

    [Fact(Explicit = true)]
    public async Task ListeningPeerIsDetectedAndCallerCancellationIsPreserved()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        Assert.Equal(SocketError.Success,
            await LocalSendTcpProbe.ConnectAsync(endpoint, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        using var accepted = await listener.AcceptSocketAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LocalSendTcpProbe.ConnectAsync(endpoint, TimeSpan.FromSeconds(1), cancellation.Token));
    }
}
