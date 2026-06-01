using System.Runtime.InteropServices;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

/// <summary>
/// macOS FSEvents watcher using CoreServices P/Invoke.
/// Reference-counted directory watching with automatic stream rebuild.
/// Feeds changes into IDirectoryChangeNotifier for the unified refresh pipeline.
/// </summary>
public class MacFSEventsWatcher : IFSEventsWatcher, IDisposable
{
    private readonly IDirectoryChangeNotifier _notifier;
    private readonly object _lock = new();

    /// <summary>Watched paths with reference counts.</summary>
    private readonly Dictionary<string, int> _watchedPaths = new(StringComparer.Ordinal);

    private IntPtr _stream;
    private bool _disposed;

    // Static delegate instance to prevent GC collection
    private static FSEventStreamCallback? _callbackDelegate;

    // Store reference to the instance for the static callback
    private static MacFSEventsWatcher? _instance;

    public MacFSEventsWatcher(IDirectoryChangeNotifier notifier)
    {
        _notifier = notifier;
        _instance = this;
    }

    public void WatchDirectory(string path)
    {
        if (string.IsNullOrEmpty(path) || _disposed) return;

        lock (_lock)
        {
            if (_watchedPaths.TryGetValue(path, out var count))
            {
                _watchedPaths[path] = count + 1;
                return; // Already watching, just bump ref count
            }

            _watchedPaths[path] = 1;
            RebuildStream();
        }
    }

    public void UnwatchDirectory(string path)
    {
        if (string.IsNullOrEmpty(path) || _disposed) return;

        lock (_lock)
        {
            if (!_watchedPaths.TryGetValue(path, out var count)) return;

            if (count > 1)
            {
                _watchedPaths[path] = count - 1;
                return;
            }

            _watchedPaths.Remove(path);
            RebuildStream();
        }
    }

    /// <summary>
    /// Stop + invalidate + release old stream, then create + schedule + start a new one
    /// for all currently watched paths. Must be called under _lock.
    /// </summary>
    private void RebuildStream()
    {
        // Tear down old stream
        if (_stream != IntPtr.Zero)
        {
            FSEventStreamStop(_stream);
            FSEventStreamInvalidate(_stream);
            FSEventStreamRelease(_stream);
            _stream = IntPtr.Zero;
        }

        if (_watchedPaths.Count == 0) return;

        var pathPtrs = Array.Empty<IntPtr>();
        var pathsArray = IntPtr.Zero;
        try
        {
            // Build CFArray of path strings
            pathPtrs = new IntPtr[_watchedPaths.Count];
            int idx = 0;
            foreach (var p in _watchedPaths.Keys)
            {
                pathPtrs[idx++] = CFStringCreateWithCString(IntPtr.Zero, p, kCFStringEncodingUTF8);
            }

            pathsArray = CFArrayCreate(IntPtr.Zero, pathPtrs, pathPtrs.Length, IntPtr.Zero);

            // Create callback delegate (stored as static to prevent GC)
            _callbackDelegate = OnFSEvent;

            _stream = FSEventStreamCreate(
                IntPtr.Zero,
                _callbackDelegate,
                IntPtr.Zero,       // context
                pathsArray,
                kFSEventStreamEventIdSinceNow,
                0.5,               // latency: 0.5s
                kFSEventStreamCreateFlagFileEvents | kFSEventStreamCreateFlagNoDefer);

            if (_stream == IntPtr.Zero)
            {
                Console.WriteLine("[MacExplorer] FSEventStreamCreate returned null");
                return;
            }

            // Schedule on main RunLoop
            var mainRunLoop = CFRunLoopGetMain();
            FSEventStreamScheduleWithRunLoop(_stream, mainRunLoop, _kCFRunLoopDefaultMode);
            FSEventStreamStart(_stream);

        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MacExplorer] FSEvents RebuildStream failed: {ex.Message}");
        }
        finally
        {
            foreach (var ptr in pathPtrs)
            {
                if (ptr != IntPtr.Zero) CFRelease(ptr);
            }

            if (pathsArray != IntPtr.Zero) CFRelease(pathsArray);
        }
    }

    /// <summary>FSEvents callback — extract parent directories and notify.</summary>
    private static void OnFSEvent(
        IntPtr streamRef, IntPtr clientCallBackInfo, nint numEvents,
        IntPtr eventPaths, IntPtr eventFlags, IntPtr eventIds)
    {
        var instance = _instance;
        if (instance == null || instance._disposed) return;

        try
        {
            var dirs = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < numEvents; i++)
            {
                var pathPtr = Marshal.ReadIntPtr(eventPaths, i * IntPtr.Size);
                if (pathPtr == IntPtr.Zero) continue;
                var path = Marshal.PtrToStringUTF8(pathPtr);
                if (string.IsNullOrEmpty(path)) continue;

                // The changed path could be a file — get parent directory
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    dirs.Add(dir);
                // Also add the path itself in case it's a directory
                dirs.Add(path);
            }

            if (dirs.Count > 0)
            {
                // Filter to only directories we're actually watching
                string[] watched;
                lock (instance._lock)
                {
                    watched = dirs.Where(d => instance._watchedPaths.ContainsKey(d)).ToArray();
                }

                if (watched.Length > 0)
                    instance._notifier.NotifyChanged(watched);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MacExplorer] FSEvents callback error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            if (_stream != IntPtr.Zero)
            {
                FSEventStreamStop(_stream);
                FSEventStreamInvalidate(_stream);
                FSEventStreamRelease(_stream);
                _stream = IntPtr.Zero;
            }
            _watchedPaths.Clear();
        }

        if (ReferenceEquals(_instance, this))
            _instance = null;
    }

    // ── CoreServices FSEvents P/Invoke ──

    private delegate void FSEventStreamCallback(
        IntPtr streamRef, IntPtr clientCallBackInfo, nint numEvents,
        IntPtr eventPaths, IntPtr eventFlags, IntPtr eventIds);

    private const ulong kFSEventStreamEventIdSinceNow = 0xFFFFFFFFFFFFFFFF;
    private const uint kFSEventStreamCreateFlagFileEvents = 0x00000010;
    private const uint kFSEventStreamCreateFlagNoDefer = 0x00000002;
    private const uint kCFStringEncodingUTF8 = 0x08000100;

    // kCFRunLoopDefaultMode
    private static readonly IntPtr _kCFRunLoopDefaultMode = CFStringCreateWithCString(
        IntPtr.Zero, "kCFRunLoopDefaultMode", kCFStringEncodingUTF8);

    [DllImport("/System/Library/Frameworks/CoreServices.framework/CoreServices")]
    private static extern IntPtr FSEventStreamCreate(
        IntPtr allocator, FSEventStreamCallback callback, IntPtr context,
        IntPtr pathsToWatch, ulong sinceWhen, double latency, uint flags);

    [DllImport("/System/Library/Frameworks/CoreServices.framework/CoreServices")]
    private static extern void FSEventStreamScheduleWithRunLoop(
        IntPtr streamRef, IntPtr runLoop, IntPtr runLoopMode);

    [DllImport("/System/Library/Frameworks/CoreServices.framework/CoreServices")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool FSEventStreamStart(IntPtr streamRef);

    [DllImport("/System/Library/Frameworks/CoreServices.framework/CoreServices")]
    private static extern void FSEventStreamStop(IntPtr streamRef);

    [DllImport("/System/Library/Frameworks/CoreServices.framework/CoreServices")]
    private static extern void FSEventStreamInvalidate(IntPtr streamRef);

    [DllImport("/System/Library/Frameworks/CoreServices.framework/CoreServices")]
    private static extern void FSEventStreamRelease(IntPtr streamRef);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFRunLoopGetMain();

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFArrayCreate(
        IntPtr allocator, IntPtr[] values, nint numValues, IntPtr callBacks);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringCreateWithCString(
        IntPtr allocator, string cStr, uint encoding);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);
}
