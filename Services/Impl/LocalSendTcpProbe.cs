using System.Net;
using System.Net.Sockets;

namespace MacExplorer.Services.Impl;

/// <summary>Offline peers are normal discovery results, not first-chance exceptions.</summary>
internal static class LocalSendTcpProbe
{
    internal static async Task<SocketError> ConnectAsync(IPEndPoint endpoint, TimeSpan timeout,
        CancellationToken cancellationToken, TimeProvider? timeProvider = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout <= TimeSpan.Zero) return SocketError.TimedOut;
        using var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var args = new SocketAsyncEventArgs { RemoteEndPoint = endpoint };
        var completed = new TaskCompletionSource<SocketError>(TaskCreationOptions.RunContinuationsAsynchronously);
        args.Completed += (_, result) => completed.TrySetResult(result.SocketError);
        using var deadline = new CancellationTokenSource(timeout, timeProvider ?? TimeProvider.System);
        try
        {
            if (!socket.ConnectAsync(args))
                return args.SocketError;
            // Wait for the socket's completion before disposing its event args. A timeout
            // cancels the native connect without faulting an awaited Task on every address.
            using var timeoutRegistration = deadline.Token.Register(() => Socket.CancelConnectAsync(args));
            using var cancellationRegistration = cancellationToken.Register(() => Socket.CancelConnectAsync(args));
            var error = await completed.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return deadline.IsCancellationRequested ? SocketError.TimedOut : error;
        }
        catch (SocketException ex)
        {
            // Invalid local socket state can still fail synchronously before an operation starts.
            cancellationToken.ThrowIfCancellationRequested();
            return ex.SocketErrorCode;
        }
    }
}
