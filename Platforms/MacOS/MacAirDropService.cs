using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Platforms.MacOS;

internal sealed class MacAirDropService : IAirDropService
{
    public bool IsSupported => OperatingSystem.IsMacOS();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Completion(IntPtr context, int result, IntPtr error);
    private static readonly Completion Completed = OnCompleted;

    public async Task SendAsync(Window? owner, IReadOnlyList<string> paths)
    {
        Services.Subscriptions.SubscriptionAccess.RequireAccess();
        if (!IsSupported) throw new PlatformNotSupportedException(LocalizationText.Get("airdrop.unavailable"));
        if (paths.Count == 0 || paths.Any(path => !Path.IsPathFullyQualified(path) || path.Contains('\0')))
            throw new IOException(LocalizationText.Get("airdrop.invalid-files"));
        foreach (var path in paths)
        {
            DirectoryAccess.Current.EnsureAccess(path);
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new FileNotFoundException(LocalizationText.Get("airdrop.invalid-files"), path);
        }

        var scopes = new List<IntPtr>();
        try
        {
            if (DistributionChannel.IsAppStore)
                foreach (var path in paths)
                {
                    var access = MacSandboxNative.OpenBookmark(MacSandboxNative.CreateBookmark(path));
                    if (access.Scope == IntPtr.Zero)
                        throw new UnauthorizedAccessException(LocalizationText.Get("airdrop.invalid-files"));
                    scopes.Add(access.Scope);
                }
            await ShareAsync(owner, paths);
        }
        finally
        {
            foreach (var scope in scopes) MacSandboxNative.CloseBookmark(scope);
        }
    }

    private static async Task ShareAsync(Window? owner, IReadOnlyList<string> paths)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = (owner?.TryGetPlatformHandle() as IMacOSTopLevelPlatformHandle)?.NSView ?? IntPtr.Zero;
            if (view == IntPtr.Zero) throw new InvalidOperationException(LocalizationText.Get("airdrop.no-window"));
            var handle = GCHandle.Alloc(completion);
            var started = false;
            try
            {
                var result = me_airdrop_send(view, JsonSerializer.Serialize(paths), GCHandle.ToIntPtr(handle), Completed);
                started = result == 1;
                if (!started) throw new IOException(LocalizationText.Get(result switch
                {
                    -1 => "airdrop.invalid-files",
                    -2 => "airdrop.busy",
                    _ => "airdrop.unavailable"
                }));
            }
            finally { if (!started) handle.Free(); }
        });
        await completion.Task;
    }

    private static void OnCompleted(IntPtr context, int result, IntPtr error)
    {
        var handle = GCHandle.FromIntPtr(context);
        var completion = (TaskCompletionSource)handle.Target!;
        handle.Free();
        if (result < 0)
            completion.TrySetException(new IOException(LocalizationText.Get("airdrop.failed",
                Marshal.PtrToStringUTF8(error) ?? string.Empty)));
        else completion.TrySetResult(); // Closing the system sheet is a normal cancellation.
    }

    [DllImport("MacExplorerNativeDrag")]
    private static extern int me_airdrop_send(IntPtr view,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string pathsJson, IntPtr context, Completion completion);
}
