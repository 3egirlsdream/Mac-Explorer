using Avalonia.Media.Imaging;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Controls;

/// <summary>Viewport requests and owned thumbnails; no disk work or decoding in Render.</summary>
internal sealed class FastFileListImages
{
    private const long MaxBytes = 32L * 1024 * 1024;
    private static readonly SemaphoreSlim DecodeGate = new(4);
    private readonly Dictionary<ImageKey, CancellationTokenSource> _requests = [];
    private readonly Dictionary<ImageKey, DateTime> _failed = [];
    private readonly Dictionary<ImageKey, LinkedListNode<CachedImage>> _cache = [];
    private readonly LinkedList<CachedImage> _lru = new();
    private readonly Dictionary<TypeKey, Bitmap> _types = [];
    private readonly HashSet<TypeKey> _pendingTypes = [];
    private HashSet<ImageKey> _visible = [];
    private int _pixelSize = 64;
    private int _typePixelSize = 48;
    private int _lifetime;
    private long _bytes;

    public Func<FileSystemEntry, int, CancellationToken, Task<ThumbnailResult?>>? ThumbnailProvider { get; set; }
    public event Action? Changed;
    internal int PendingCount => _requests.Count;
    internal long CachedBytes => _bytes;

    public Bitmap? Get(FileSystemEntry entry)
    {
        var key = ImageKey.For(entry, _pixelSize);
        if (_cache.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Bitmap;
        }
        return _types.GetValueOrDefault(TypeKey.For(entry, _typePixelSize));
    }

    public void UpdateVisible(IReadOnlyList<FileSystemEntry> entries, int pixelSize, int typePixelSize = 48)
    {
        _pixelSize = pixelSize;
        _typePixelSize = typePixelSize;
        _visible = entries.Select(e => ImageKey.For(e, pixelSize)).ToHashSet();
        foreach (var (key, cancellation) in _requests.ToArray())
        {
            if (_visible.Contains(key)) continue;
            _requests.Remove(key);
            cancellation.Cancel();
        }
        foreach (var entry in entries)
        {
            var type = TypeKey.For(entry, typePixelSize);
            if (!_types.ContainsKey(type) && _pendingTypes.Add(type))
                _ = LoadTypeAsync(type, _lifetime);
            var key = ImageKey.For(entry, pixelSize);
            if (_cache.ContainsKey(key) || _requests.ContainsKey(key)
                || _failed.TryGetValue(key, out var until) && until > DateTime.UtcNow) continue;
            if (string.IsNullOrEmpty(key.Source)
                && (entry.IsDirectory || entry.IsVirtual || !Path.IsPathRooted(entry.FullPath) || ThumbnailProvider == null)) continue;
            var cancellation = new CancellationTokenSource();
            _requests[key] = cancellation;
            _ = LoadThumbnailAsync(entry, key, cancellation);
        }
    }

    private async Task LoadTypeAsync(TypeKey key, int lifetime)
    {
        try
        {
            var bitmap = await Task.Run(async () =>
            {
                await DecodeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!key.IsDirectory) return SvgIconCache.GetFileIcon(key.Icon, key.Extension, key.Pixels);
                    return key.Icon.StartsWith("ai-", StringComparison.Ordinal)
                        ? SvgIconCache.GetAiIcon(key.Icon, key.Pixels)
                        : SvgIconCache.GetFolderIcon(key.Pixels);
                }
                finally { DecodeGate.Release(); }
            });
            if (lifetime != _lifetime) return;
            // These bitmaps belong to SvgIconCache. Thumbnail eviction never disposes them.
            if (_types.Count >= 256) _types.Clear();
            _types[key] = bitmap;
            Changed?.Invoke();
        }
        catch (Exception) { /* The Fluent geometry remains available as a fallback. */ }
        finally { if (lifetime == _lifetime) _pendingTypes.Remove(key); }
    }

    private async Task LoadThumbnailAsync(FileSystemEntry entry, ImageKey key, CancellationTokenSource cancellation)
    {
        Bitmap? bitmap = null;
        var token = cancellation.Token;
        var provider = ThumbnailProvider;
        try
        {
            // Scrolling past a row cancels it before a Quick Look helper is queued.
            await Task.Delay(100, token);
            bitmap = await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                byte[]? bytes = null;
                if (!entry.IsDirectory && !entry.IsVirtual && Path.IsPathRooted(entry.FullPath) && provider != null)
                {
                    var result = await provider(entry, key.Pixels, token).ConfigureAwait(false);
                    bytes = result?.Bytes;
                }
                token.ThrowIfCancellationRequested();
                if (bytes is not { Length: > 0 } && !string.IsNullOrEmpty(key.Source))
                {
                    if (key.Source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        var comma = key.Source.IndexOf(',');
                        if (comma >= 0) bytes = Convert.FromBase64String(key.Source[(comma + 1)..]);
                    }
                    else if (File.Exists(key.Source)) bytes = await File.ReadAllBytesAsync(key.Source, token).ConfigureAwait(false);
                }
                if (bytes is not { Length: > 0 }) return null;
                await DecodeGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    using var stream = new MemoryStream(bytes);
                    return Bitmap.DecodeToWidth(stream, key.Pixels);
                }
                finally { DecodeGate.Release(); }
            }, token);
            if (token.IsCancellationRequested || !_requests.TryGetValue(key, out var active)
                || !ReferenceEquals(active, cancellation) || !_visible.Contains(key)) return;
            if (bitmap == null) { MarkFailed(key); return; }
            Add(key, bitmap);
            bitmap = null;
            Changed?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!token.IsCancellationRequested) MarkFailed(key);
        }
        finally
        {
            bitmap?.Dispose();
            if (_requests.TryGetValue(key, out var active) && ReferenceEquals(active, cancellation)) _requests.Remove(key);
            cancellation.Dispose();
        }
    }

    private void MarkFailed(ImageKey key)
    {
        if (_failed.Count >= 1024) _failed.Clear();
        _failed[key] = DateTime.UtcNow.AddSeconds(30);
    }
    private void Add(ImageKey key, Bitmap bitmap)
    {
        var bytes = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
        _cache[key] = _lru.AddFirst(new CachedImage(key, bitmap, bytes));
        _bytes += bytes;
        while (_bytes > MaxBytes && _lru.Last is { } last)
        {
            _cache.Remove(last.Value.Key);
            _lru.RemoveLast();
            _bytes -= last.Value.Bytes;
            last.Value.Bitmap.Dispose();
        }
    }

    public void Clear()
    {
        _lifetime++;
        foreach (var cancellation in _requests.Values) cancellation.Cancel();
        _requests.Clear();
        _visible.Clear();
        foreach (var item in _lru) item.Bitmap.Dispose();
        _lru.Clear();
        _cache.Clear();
        _types.Clear();
        _pendingTypes.Clear();
        _failed.Clear();
        _bytes = 0;
    }

    private readonly record struct TypeKey(string Icon, string Extension, bool IsDirectory, int Pixels)
    {
        public static TypeKey For(FileSystemEntry e, int pixels) => new(e.IconKey, e.Extension, e.IsDirectory, pixels);
    }
    private readonly record struct ImageKey(string Path, DateTime Modified, long Size, string? Source, int Pixels)
    {
        public static ImageKey For(FileSystemEntry e, int pixels) => new(e.FullPath, e.LastModified, e.Size,
            string.IsNullOrWhiteSpace(e.ThumbnailUrl) ? e.IconUrl : e.ThumbnailUrl, pixels);
    }
    private sealed record CachedImage(ImageKey Key, Bitmap Bitmap, long Bytes);
}
