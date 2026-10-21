using Avalonia.Media.Imaging;

namespace MacExplorer.Services.Impl;

/// <summary>Shares borrowed images without keeping unused native bitmaps alive or disposing live bindings.</summary>
internal sealed class WeakBitmapCache(long maxBytes, int maxEntries = 512) : IIdleMemoryCache
{
    private sealed record Entry(string Key, WeakReference<Bitmap> Bitmap, long Bytes)
    {
        public DateTime LastUsed = DateTime.UtcNow;
    }
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = [];
    private readonly object _sync = new();
    private long _bytes;
    private bool _registered;

    public bool TryGet(string key, out Bitmap? bitmap)
    {
        lock (_sync)
        {
            bitmap = null;
            if (!_entries.TryGetValue(key, out var node)) return false;
            if (!node.Value.Bitmap.TryGetTarget(out bitmap)) { Remove(node); return false; }
            node.Value.LastUsed = DateTime.UtcNow;
            _lru.Remove(node); _lru.AddFirst(node);
            return true;
        }
    }

    public Bitmap GetOrAdd(string key, Func<string, Bitmap> factory)
    {
        lock (_sync)
        {
            if (TryGet(key, out var bitmap)) return bitmap!;
            bitmap = factory(key); Add(key, bitmap); return bitmap;
        }
    }

    public void Add(string key, Bitmap bitmap)
    {
        lock (_sync)
        {
            if (!_registered) { MemoryCacheMaintenance.Register(this); _registered = true; }
            if (_entries.TryGetValue(key, out var previous)) Remove(previous);
            var bytes = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4 + key.Length * 2L;
            _entries[key] = _lru.AddFirst(new Entry(key, new(bitmap), bytes));
            _bytes += bytes;
            while ((_bytes > maxBytes || _entries.Count > maxEntries) && _lru.Last is { } last) Remove(last);
        }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _entries.Remove(node.Value.Key); _lru.Remove(node); _bytes -= node.Value.Bytes;
    }

    public void Clear() { lock (_sync) { _entries.Clear(); _lru.Clear(); _bytes = 0; } }

    public void TrimMemory(DateTime unusedBefore)
    {
        lock (_sync)
            foreach (var node in _entries.Values.Where(n => n.Value.LastUsed < unusedBefore || !n.Value.Bitmap.TryGetTarget(out _)).ToArray())
                Remove(node);
    }
}
