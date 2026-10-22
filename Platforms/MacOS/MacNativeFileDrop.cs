using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Platforms.MacOS;

internal static class MacNativeFileDrop
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DropCallback(nint context, nint directory, nint path, nint error, nint permissionSource);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FolderGrantCallback(nint context, nint bookmark, nint error);
    private static readonly DropCallback Completed = OnCompleted;
    private static readonly DropCallback Retried = (context, directory, path, error, source)
        => PostCompletion(context, directory, path, error, source, allowAuthorization: false);
    private static readonly FolderGrantCallback FolderSelected = OnFolderSelected;
    private static readonly Dictionary<nint, WeakReference<Window>> Windows = [];
    private static readonly SemaphoreSlim AuthorizationGate = new(1, 1);
    private static FileDropFolderAccess? _folderAccess;
    private static FileDropFolderAccess FolderAccess => _folderAccess ??= new(
        Path.Combine(RuntimePaths.DataDirectory, "drop-folder-bookmarks.json"), new NativeBookmarkAccess());

    internal static void Enable(Window window)
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (window.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle handle) return;
        try
        {
            if (EnableNative(handle.NSView)) Windows[handle.NSView] = new(window);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    internal static void Disable(Window window)
    {
        foreach (var entry in Windows.ToArray())
            if (!entry.Value.TryGetTarget(out var owner) || ReferenceEquals(owner, window)) Windows.Remove(entry.Key);
        if (Windows.Count == 0) { _folderAccess?.Dispose(); _folderAccess = null; }
    }

    internal static bool HasIncomingContent
    {
        get
        {
            if (!OperatingSystem.IsMacOS()) return false;
            try { return GetKindNative() != 0; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }

    internal static IDisposable? AcquireAccess()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            var handle = BeginAccessNative();
            return handle == 0 ? null : new DropAccessLease(handle);
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    private sealed class DropAccessLease(nint handle) : IDisposable
    {
        private nint _handle = handle;
        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, 0);
            if (handle != 0) EndAccessNative(handle);
        }
    }

    internal static string[] GetIncomingPaths()
    {
        if (!OperatingSystem.IsMacOS()) return [];
        nint paths = 0;
        try
        {
            paths = CopyPathsNative();
            return paths == 0 ? [] : System.Text.Json.JsonSerializer.Deserialize<string[]>(Marshal.PtrToStringUTF8(paths)!) ?? [];
        }
        catch (DllNotFoundException) { return []; }
        catch (EntryPointNotFoundException) { return []; }
        finally { if (paths != 0) FreePathsNative(paths); }
    }

    internal static bool Receive(Window window, string targetDirectory)
    {
        Services.Subscriptions.SubscriptionAccess.RequireAccess();
        if (window.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle handle) return false;
        FolderAccess.Restore();
        return ReceiveNative(targetDirectory, handle.NSView, Completed);
    }

    private static void OnCompleted(nint context, nint directory, nint path, nint error, nint permissionSource)
        => PostCompletion(context, directory, path, error, permissionSource, allowAuthorization: true);

    private static void PostCompletion(nint context, nint directory, nint path, nint error,
        nint permissionSource, bool allowAuthorization)
    {
        // The native reader can run after its original view/window has closed.
        var target = Marshal.PtrToStringUTF8(directory);
        var saved = Marshal.PtrToStringUTF8(path);
        var failure = Marshal.PtrToStringUTF8(error);
        var deniedSource = Marshal.PtrToStringUTF8(permissionSource);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (allowAuthorization && target != null && deniedSource != null
                    && Windows.TryGetValue(context, out var owner) && owner.TryGetTarget(out var receiver))
                {
                    await AuthorizeAndRetryAsync(receiver, deniedSource, target, context);
                    return;
                }
                if (target != null && saved != null)
                    App.Services.GetService<IDirectoryChangeNotifier>()?.NotifyChanged([target]);
                if (saved != null && App.Services.GetService<IFileOperationHistoryService>() is { } history)
                    await history.RecordCreatedAsync(saved);
                if (failure != null && Windows.TryGetValue(context, out var reference) && reference.TryGetTarget(out var window))
                    AppToast.ShowError(window, LocalizationText.Get("拖放失败: {0}", failure));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Native file drop completion: {ex}");
                if (Windows.TryGetValue(context, out var owner) && owner.TryGetTarget(out var receiver))
                    AppToast.ShowError(receiver, LocalizationText.Get("拖放失败: {0}", ex.Message));
            }
        });
    }

    private static async Task AuthorizeAndRetryAsync(Window window, string source, string target, nint context)
    {
        await AuthorizationGate.WaitAsync();
        try
        {
            if (!window.IsVisible) return;
            if (!FolderAccess.Covers(source))
            {
                var bookmark = await PickSourceFolderAsync(window, source);
                if (bookmark == null || !window.IsVisible) return;
                if (!FolderAccess.Remember(bookmark, source))
                    throw new UnauthorizedAccessException(LocalizationText.Get("未能启用文件夹授权，请重新选择包含此附件的文件夹。"));
                if (FolderAccess.PersistenceError is { } error)
                    AppToast.ShowError(window, LocalizationText.Get("本次授权已启用，但无法记住此文件夹：{0}", error));
            }
            Services.Subscriptions.SubscriptionAccess.RequireAccess();
            if (!CopyFileNative(source, target, context, Retried))
                throw new IOException(LocalizationText.Get("无法继续保存拖入的文件。"));
        }
        finally { AuthorizationGate.Release(); }
    }

    private static Task<string?> PickSourceFolderAsync(Window window, string source)
    {
        if (window.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle handle)
            return Task.FromResult<string?>(null);
        var folder = FileDropFolderAccess.SuggestedFolder(source);
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cookie = GCHandle.Alloc(completion);
        try
        {
            AuthorizeFolderNative(source, folder, handle.NSView,
                LocalizationText.Get("允许读取附件文件夹"),
                LocalizationText.Get("macOS 阻止读取此附件。请选择“{0}”文件夹并授权，将继续保存。授权包括其子文件夹，后续拖入可复用此授权。", Path.GetFileName(folder)),
                LocalizationText.Get("授权并保存"),
                LocalizationText.Get("请选择包含“{0}”的文件夹。", Path.GetFileName(source)),
                GCHandle.ToIntPtr(cookie), FolderSelected);
        }
        catch { cookie.Free(); throw; }
        return completion.Task;
    }

    private static void OnFolderSelected(nint context, nint bookmark, nint error)
    {
        var cookie = GCHandle.FromIntPtr(context);
        var completion = (TaskCompletionSource<string?>)cookie.Target!;
        var saved = Marshal.PtrToStringUTF8(bookmark);
        var failure = Marshal.PtrToStringUTF8(error);
        cookie.Free();
        if (failure != null) completion.TrySetException(new IOException(failure));
        else completion.TrySetResult(saved);
    }

    [DllImport("MacExplorerNativeDrag", EntryPoint = "me_file_drop_enable")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool EnableNative(nint view);

    [DllImport("MacExplorerNativeDrag", EntryPoint = "me_file_drop_kind")]
    private static extern int GetKindNative();

    [DllImport("MacExplorerNativeDrag", EntryPoint = "me_file_drop_begin_access")]
    private static extern nint BeginAccessNative();

    [DllImport("MacExplorerNativeDrag", EntryPoint = "me_file_drop_end_access")]
    private static extern void EndAccessNative(nint handle);

    [DllImport("MacExplorerNativeDrag", EntryPoint = "me_file_drop_copy_paths")]
    private static extern nint CopyPathsNative();

    [DllImport("MacExplorerNativeDrag", EntryPoint = "me_file_drop_free_paths")]
    private static extern void FreePathsNative(nint paths);

    [DllImport("MacExplorerNativeDrag", EntryPoint = "me_file_drop_receive")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool ReceiveNative([MarshalAs(UnmanagedType.LPUTF8Str)] string directory, nint context, DropCallback callback);

    [DllImport("MacExplorerNativeDrag", EntryPoint = "me_file_drop_copy_file")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CopyFileNative([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string directory, nint context, DropCallback callback);

    [DllImport("MacExplorerNativeDrag", EntryPoint = "me_file_drop_authorize_folder")]
    private static extern void AuthorizeFolderNative([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string folder, nint view,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title, [MarshalAs(UnmanagedType.LPUTF8Str)] string message,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string prompt, [MarshalAs(UnmanagedType.LPUTF8Str)] string invalidSelection,
        nint context, FolderGrantCallback callback);
}
