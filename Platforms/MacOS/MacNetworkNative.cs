using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using MacExplorer.Models;

namespace MacExplorer.Platforms.MacOS;

internal static class MacNetworkNative
{
    private const string Library = "libMacExplorerNativeDrag.dylib";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Callback(long id, IntPtr json);
    private static readonly Callback Receive = OnResult;
    private static readonly ConcurrentDictionary<long, Action<string>> Requests = new();
    private static long _nextId;
    private static void OnResult(long id, IntPtr json)
    {
        try { if (Requests.TryGetValue(id, out var callback)) callback(Marshal.PtrToStringUTF8(json) ?? "{}"); }
        catch { /* Do not unwind a managed exception through the native callback. */ }
    }
    internal static async Task<string[]> MountAsync(string uri, string username, string password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        Requests[id] = json =>
        {
            try
            {
                var result = JsonSerializer.Deserialize<MountResult>(json)!;
                if (result.Status == -128) completion.TrySetCanceled();
                else if (result.Status != 0) completion.TrySetException(new SmbMountException(result.Status));
                else completion.TrySetResult(result.Paths);
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        };
        try
        {
            me_smb_mount(id, uri, username, password, Receive);
            using var registration = cancellationToken.Register(() => { me_smb_cancel(id); completion.TrySetCanceled(cancellationToken); });
            return await completion.Task;
        }
        finally { Requests.TryRemove(id, out _); }
    }
    internal static IReadOnlyList<VolumeInfo> GetVolumes()
    {
        var pointer = me_network_volumes();
        try { return JsonSerializer.Deserialize<VolumeInfo[]>(Marshal.PtrToStringUTF8(pointer) ?? "[]") ?? []; }
        finally { me_string_free(pointer); }
    }
    internal static IDisposable Browse(Action<string> callback)
    {
        var id = Interlocked.Increment(ref _nextId);
        Requests[id] = callback; me_network_browse(id, Receive);
        return new BrowseLease(id);
    }
    private sealed class BrowseLease(long id) : IDisposable
    {
        public void Dispose() { if (Requests.TryRemove(id, out _)) me_network_browse_stop(id); }
    }
    internal static bool Open(string uri) => me_network_open(uri);
    private sealed record MountResult(int Status, string[] Paths);
    [DllImport(Library)] private static extern void me_smb_mount(long id, [MarshalAs(UnmanagedType.LPUTF8Str)] string uri, [MarshalAs(UnmanagedType.LPUTF8Str)] string user, [MarshalAs(UnmanagedType.LPUTF8Str)] string password, Callback callback);
    [DllImport(Library)] private static extern void me_smb_cancel(long id);
    [DllImport(Library)] private static extern IntPtr me_network_volumes();
    [DllImport(Library)] private static extern void me_string_free(IntPtr pointer);
    [DllImport(Library)] private static extern void me_network_browse(long id, Callback callback);
    [DllImport(Library)] private static extern void me_network_browse_stop(long id);
    [DllImport(Library)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool me_network_open([MarshalAs(UnmanagedType.LPUTF8Str)] string uri);
}

public sealed class SmbMountException(int status) : IOException(Services.Impl.LocalizationText.Get("network.mount-error", status))
{
    public int Status { get; } = status;
}
