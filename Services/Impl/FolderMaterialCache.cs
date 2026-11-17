using SkiaSharp;

namespace MacExplorer.Services.Impl;

/// <summary>Bounded native materials. A borrowed image stays alive until its last render finishes.</summary>
internal sealed class FolderMaterialCache : IIdleMemoryCache, IMemoryDiagnosticSource, IDisposable
{
    internal static FolderMaterialCache Shared { get; } = new(4L * 1024 * 1024);
    private readonly long _budget;
    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();
    private long _cachedBytes, _liveBytes;
    private int _leases;
    private bool _disposed;

    internal sealed class Entry(string key, SKImage image)
    {
        internal readonly string Key = key;
        internal readonly SKImage Image = image;
        internal readonly long Bytes = (long)image.Width * image.Height * 4;
        internal int Users;
        internal bool Cached;
        internal DateTime LastUsed = DateTime.UtcNow;
    }

    internal sealed class Lease : IDisposable
    {
        private FolderMaterialCache? _owner;
        private readonly Entry _entry;
        internal SKImage Image => _entry.Image;
        internal Lease(FolderMaterialCache owner, Entry entry) { _owner = owner; _entry = entry; }
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_entry);
    }

    internal FolderMaterialCache(long budget)
    {
        _budget = budget;
        MemoryCacheMaintenance.Register(this);
        MemoryDiagnostics.Register(this);
    }

    internal Lease Acquire(string key, Func<SKImage> create)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(key, out var node)) return Borrow(node);
        }
        // Never hold the cache lock while decoding/rasterizing: diagnostics and trimming run on the UI thread.
        var image = create();
        lock (_sync)
        {
            if (_disposed) { image.Dispose(); throw new ObjectDisposedException(nameof(FolderMaterialCache)); }
            if (_entries.TryGetValue(key, out var existing)) { image.Dispose(); return Borrow(existing); }
            return AddOwned(key, image);
        }
    }

    private Lease Borrow(LinkedListNode<Entry> node)
    {
        node.Value.LastUsed = DateTime.UtcNow;
        _lru.Remove(node); _lru.AddFirst(node);
        node.Value.Users++; _leases++;
        return new Lease(this, node.Value);
    }

    private Lease AddOwned(string key, SKImage image)
    {
        var entry = new Entry(key, image) { Users = 1 };
        _liveBytes += entry.Bytes; _leases++;
        if (entry.Bytes <= _budget)
        {
            while (_cachedBytes + entry.Bytes > _budget && _lru.Last is { } last) Evict(last);
            entry.Cached = true;
            _entries.Add(key, _lru.AddFirst(entry));
            _cachedBytes += entry.Bytes;
        }
        return new Lease(this, entry);
    }

    private void Evict(LinkedListNode<Entry> node)
    {
        _lru.Remove(node); _entries.Remove(node.Value.Key);
        node.Value.Cached = false; _cachedBytes -= node.Value.Bytes;
        if (node.Value.Users == 0) Destroy(node.Value);
    }
    private void Destroy(Entry entry) { _liveBytes -= entry.Bytes; entry.Image.Dispose(); }
    private void Release(Entry entry)
    {
        lock (_sync)
        {
            entry.Users--; _leases--;
            if (entry.Users == 0 && !entry.Cached) Destroy(entry);
        }
    }
    public void TrimMemory(DateTime unusedBefore)
    {
        if (!Monitor.TryEnter(_sync)) return;
        try
        {
            foreach (var node in _entries.Values.Where(node => node.Value.LastUsed < unusedBefore).ToArray()) Evict(node);
        }
        finally { Monitor.Exit(_sync); }
    }
    public MemoryDiagnosticState GetMemoryState()
    {
        if (!Monitor.TryEnter(_sync)) return new("folder-materials", 0, Owners: 0, Skipped: 1);
        try { return new("folder-materials", _entries.Count, _liveBytes, Pending: _leases); }
        finally { Monitor.Exit(_sync); }
    }
    internal long CachedBytes { get { lock (_sync) return _cachedBytes; } }
    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            while (_lru.Last is { } last) Evict(last);
        }
    }
}
