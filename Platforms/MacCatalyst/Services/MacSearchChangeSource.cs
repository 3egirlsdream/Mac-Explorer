using System.Runtime.InteropServices;
using MacExplorer.Services.Search;

namespace MacExplorer.Platforms.MacCatalyst.Services;

/// <summary>Long-lived recursive search watches, independent of open windows and tabs.</summary>
public sealed class MacSearchChangeSource : ISearchChangeSource
{
    public IDisposable Watch(string root, ulong sinceEventId, Action<SearchChange> onChange, bool fullScanFollows)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("FSEvents requires macOS.");
        return new Subscription(root, sinceEventId, onChange, fullScanFollows);
    }

    // Exposed as a pure function so recovery flags can be regression-tested without macOS.
    public static SearchChange Decode(string root, string path, uint flags, ulong id)
    {
        const uint droppedOrWrapped = 0x2 | 0x4 | 0x8;
        const uint rootOrMountChanged = 0x20 | 0x40 | 0x80;
        var reset = (flags & (rootOrMountChanged | 0x8)) != 0;
        return new SearchChange((flags & droppedOrWrapped) != 0 || reset ? root : path,
            (flags & 0x20000) != 0, (flags & (0x1 | droppedOrWrapped | rootOrMountChanged)) != 0,
            id, reset);
    }

    // FSEvents can return physical paths (/private/tmp) for a logical root (/tmp).
    // Only map the watched prefix; do not case-fold or normalize file identities.
    public static string MapEventPath(string path, string watchedRoot, string requestedRoot) =>
        path == watchedRoot ? requestedRoot : SearchPath.IsWithin(path, watchedRoot)
            ? SearchPath.Prefix(requestedRoot) + path[SearchPath.Prefix(watchedRoot).Length..] : path;

    private sealed class Subscription : IDisposable
    {
        private readonly string _root;
        private readonly string _watchedRoot;
        private readonly Action<SearchChange> _publish;
        private readonly Callback _callback; // Instance-owned delegate; never a static singleton target.
        private IntPtr _stream, _queue, _path, _paths;
        private int _disposed;

        public Subscription(string root, ulong sinceEventId, Action<SearchChange> publish, bool fullScanFollows)
        {
            _root = root;
            var resolved = realpath(root, IntPtr.Zero);
            if (resolved == IntPtr.Zero) throw new IOException($"Unable to resolve search root: {root}");
            try { _watchedRoot = SearchPath.Normalize(Marshal.PtrToStringUTF8(resolved)!); }
            finally { free(resolved); }
            _publish = publish;
            _callback = OnEvents;
            try
            {
                _path = CFStringCreateWithCString(IntPtr.Zero, _watchedRoot, 0x08000100);
                if (_path == IntPtr.Zero) throw new IOException("Unable to allocate FSEvents root.");
                _paths = CFArrayCreate(IntPtr.Zero, [_path], 1, IntPtr.Zero);
                _queue = dispatch_queue_create("com.macexplorer.search-events", IntPtr.Zero);
                if (_paths == IntPtr.Zero || _queue == IntPtr.Zero) throw new IOException("Unable to create search event queue.");
                var current = FSEventsGetCurrentEventId();
                // A full reconciliation covers the pre-start history. Replaying it as
                // dirty work would needlessly scan the same tree twice at each launch.
                var since = fullScanFollows || sinceEventId == 0 || sinceEventId > current ? current : sinceEventId;
                // WatchRoot | NoDefer | FileEvents. Do NOT ignore our own file operations.
                _stream = FSEventStreamCreate(IntPtr.Zero, _callback, IntPtr.Zero, _paths, since, 0.35, 0x4 | 0x2 | 0x10);
                if (_stream == IntPtr.Zero) throw new IOException("Unable to create search event stream.");
                FSEventStreamSetDispatchQueue(_stream, _queue);
                if (!FSEventStreamStart(_stream)) throw new IOException("Unable to start search event stream.");
            }
            catch { Dispose(); throw; }
        }

        private void OnEvents(IntPtr stream, IntPtr context, nint count, IntPtr paths, IntPtr flags, IntPtr ids)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var path = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(paths, i * IntPtr.Size));
                    var flag = unchecked((uint)Marshal.ReadInt32(flags, i * sizeof(uint)));
                    if (string.IsNullOrEmpty(path) || flag == 0x10) continue; // HistoryDone marker only.
                    var id = unchecked((ulong)Marshal.ReadInt64(ids, i * sizeof(ulong)));
                    _publish(Decode(_root, MapEventPath(path, _watchedRoot, _root), flag, id));
                }
            }
            catch (Exception ex)
            {
                // Do not allow an exception to cross the unmanaged callback boundary.
                System.Diagnostics.Debug.WriteLine($"Search FSEvents callback: {ex}");
                try { _publish(new(_root, true, true, 0)); } catch { }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_stream != IntPtr.Zero)
            {
                FSEventStreamStop(_stream);
                FSEventStreamInvalidate(_stream);
                // Dispose is called by the index worker/shutdown, never from this queue.
                // Drain callbacks before releasing the stream or its managed delegate.
                if (_queue != IntPtr.Zero) dispatch_sync_f(_queue, IntPtr.Zero, Drain);
                FSEventStreamRelease(_stream);
                _stream = IntPtr.Zero;
            }
            if (_queue != IntPtr.Zero) { dispatch_release(_queue); _queue = IntPtr.Zero; }
            if (_paths != IntPtr.Zero) { CFRelease(_paths); _paths = IntPtr.Zero; }
            if (_path != IntPtr.Zero) { CFRelease(_path); _path = IntPtr.Zero; }
            GC.KeepAlive(_callback);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Callback(IntPtr stream, IntPtr context, nint count, IntPtr paths, IntPtr flags, IntPtr ids);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DispatchAction(IntPtr context);
    private static readonly DispatchAction Drain = _ => { };
    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string SystemLibrary = "/usr/lib/libSystem.B.dylib";
    [DllImport(CoreServices)] private static extern IntPtr FSEventStreamCreate(IntPtr allocator, Callback callback,
        IntPtr context, IntPtr paths, ulong since, double latency, uint flags);
    [DllImport(CoreServices)] private static extern ulong FSEventsGetCurrentEventId();
    [DllImport(CoreServices)] private static extern void FSEventStreamSetDispatchQueue(IntPtr stream, IntPtr queue);
    [DllImport(CoreServices)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool FSEventStreamStart(IntPtr stream);
    [DllImport(CoreServices)] private static extern void FSEventStreamStop(IntPtr stream);
    [DllImport(CoreServices)] private static extern void FSEventStreamInvalidate(IntPtr stream);
    [DllImport(CoreServices)] private static extern void FSEventStreamRelease(IntPtr stream);
    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(CoreFoundation)] private static extern IntPtr CFArrayCreate(IntPtr allocator, IntPtr[] values, nint count, IntPtr callbacks);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr value);
    [DllImport(SystemLibrary)] private static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);
    [DllImport(SystemLibrary)] private static extern void free(IntPtr value);
    [DllImport(SystemLibrary)] private static extern IntPtr dispatch_queue_create([MarshalAs(UnmanagedType.LPUTF8Str)] string label, IntPtr attributes);
    [DllImport(SystemLibrary)] private static extern void dispatch_sync_f(IntPtr queue, IntPtr context, DispatchAction action);
    [DllImport(SystemLibrary)] private static extern void dispatch_release(IntPtr value);
}
