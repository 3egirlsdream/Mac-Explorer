using Avalonia.Media.Imaging;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Controls;

/// <summary>Viewport requests and owned thumbnails; no disk work or decoding in Render.</summary>
internal sealed class FastFileListImages : IIdleMemoryCache, IMemoryDiagnosticSource
{
    private const long MaxBytes = 16L * 1024 * 1024;
    private static readonly SemaphoreSlim DecodeGate = new(4);
    private readonly Dictionary<ImageKey, CancellationTokenSource> _requests = [];
    private readonly Dictionary<ImageKey, DateTime> _failed = [];
    private readonly Dictionary<ImageKey, LinkedListNode<CachedImage>> _cache = [];
    private readonly LinkedList<CachedImage> _lru = new();
    private readonly Dictionary<TypeKey, Bitmap> _types = [];
    private readonly HashSet<TypeKey> _pendingTypes = [];
    private HashSet<ImageKey> _visible = [];
    private bool _hasVisibleEntries;
    private int _pixelSize = 64;
    private int _typePixelSize = 48;
    private bool _reuseSizes;
    private int _lifetime;
    private long _bytes;
    private CancellationTokenSource? _coverSchedule;
    private DateTime _lastVisibleUse = DateTime.UtcNow;
    private FolderPhotoCoverStyle _folderStyle;
    internal FolderPhotoCoverStyle FolderStyle
    {
        get => _folderStyle;
        set
        {
            if (_folderStyle == value) return;
            _folderStyle = value;
            foreach (var key in _types.Keys.Where(key => key.IsDirectory && !key.Icon.StartsWith("ai-", StringComparison.Ordinal)).ToArray())
                _types.Remove(key);
        }
    }

    public FastFileListImages() { MemoryCacheMaintenance.Register(this); MemoryDiagnostics.Register(this); }
    public MemoryDiagnosticState GetMemoryState() => new("file-list-images", _cache.Count, _bytes, _requests.Count);

    public Func<FileSystemEntry, int, CancellationToken, Task<ThumbnailResult?>>? ThumbnailProvider { get; set; }
    internal bool TypeIconsOnly { get; set; }
    internal bool ShowArchiveThumbnails { get; set; } = true;
    public Func<FileSystemEntry, int, CancellationToken, Task<ThumbnailResult?>>? FolderCoverProvider { get; set; }
    public event Action? Changed;
    internal int PendingCount => _requests.Count;
    internal long CachedBytes => _bytes;

    public Bitmap? Get(FileSystemEntry entry, bool folderCoversEnabled = false)
        => Get(entry, folderCoversEnabled, out _);

    public Bitmap? Get(FileSystemEntry entry, bool folderCoversEnabled, out bool isThumbnail)
    {
        isThumbnail = false;
        if (TypeIconsOnly || !ShowArchiveThumbnails && entry.ArchiveThumbnailVersion != null)
            return _types.GetValueOrDefault(TypeKey.For(entry, _typePixelSize, _folderStyle));
        var key = ImageKey.ForView(entry, _pixelSize, folderCoversEnabled && FolderCoverProvider != null);
        var node = FindThumbnail(key, allowSmaller: true);
        if (node != null)
        {
            node.Value.LastUsed = DateTime.UtcNow;
            isThumbnail = !entry.IsDirectory;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Bitmap;
        }
        return _types.GetValueOrDefault(TypeKey.For(entry, _typePixelSize, _folderStyle));
    }

    public void UpdateVisible(IReadOnlyList<FileSystemEntry> entries, int pixelSize, int typePixelSize = 48,
        bool folderCoversEnabled = false, bool reuseSizes = false)
    {
        _pixelSize = pixelSize;
        _typePixelSize = typePixelSize;
        _reuseSizes = reuseSizes;
        var loadCovers = folderCoversEnabled && FolderCoverProvider != null;
        HashSet<ImageKey> nextVisible = TypeIconsOnly ? [] : entries
            .Where(entry => ShowArchiveThumbnails || entry.ArchiveThumbnailVersion == null)
            .Select(e => ImageKey.ForView(e, pixelSize, loadCovers)).ToHashSet();
        var viewportChanged = !_visible.SetEquals(nextVisible);
        _visible = nextVisible;
        _hasVisibleEntries = entries.Count > 0;
        if (_hasVisibleEntries) _lastVisibleUse = DateTime.UtcNow;
        if (viewportChanged && _coverSchedule != null)
        {
            _coverSchedule.Cancel();
            _coverSchedule = null;
        }
        foreach (var (key, cancellation) in _requests.ToArray())
        {
            if (_visible.Contains(key)) continue;
            _requests.Remove(key);
            cancellation.Cancel();
        }
        List<FileSystemEntry>? coversToSchedule = null;
        foreach (var entry in entries)
        {
            var type = TypeKey.For(entry, typePixelSize, _folderStyle);
            if (!_types.ContainsKey(type) && _pendingTypes.Add(type))
                _ = LoadTypeAsync(type, _lifetime);
            if (TypeIconsOnly || !ShowArchiveThumbnails && entry.ArchiveThumbnailVersion != null) continue;
            var key = ImageKey.ForView(entry, pixelSize, loadCovers);
            if (key.IsFolderCover)
            {
                if (_coverSchedule == null && FindThumbnail(key) == null && !_requests.ContainsKey(key)
                    && (!_failed.TryGetValue(key, out var coverUntil) || coverUntil <= DateTime.UtcNow))
                    (coversToSchedule ??= []).Add(entry);
                continue;
            }
            if (FindThumbnail(key) != null || _requests.ContainsKey(key)
                || _failed.TryGetValue(key, out var until) && until > DateTime.UtcNow) continue;
            if (string.IsNullOrEmpty(key.Source) && !key.IsFolderCover
                && (!CanRequestThumbnail(entry) || ThumbnailProvider == null)) continue;
            var cancellation = new CancellationTokenSource();
            _requests[key] = cancellation;
            _ = LoadThumbnailAsync(entry, key, cancellation, 100);
        }
        if (loadCovers && _coverSchedule == null && coversToSchedule is { Count: > 0 })
        {
            var schedule = new CancellationTokenSource();
            _coverSchedule = schedule;
            _ = ScheduleFolderCoversAsync(coversToSchedule.ToArray(), pixelSize, schedule);
        }
    }

    private async Task ScheduleFolderCoversAsync(FileSystemEntry[] entries, int pixels, CancellationTokenSource schedule)
    {
        try
        {
            await Task.Delay(180, schedule.Token);
            foreach (var entry in entries)
            {
                schedule.Token.ThrowIfCancellationRequested();
                var key = ImageKey.ForView(entry, pixels, true);
                if (!key.IsFolderCover || !_visible.Contains(key) || FindThumbnail(key) != null || _requests.ContainsKey(key)
                    || _failed.TryGetValue(key, out var until) && until > DateTime.UtcNow) continue;
                var request = new CancellationTokenSource();
                _requests[key] = request;
                _ = LoadThumbnailAsync(entry, key, request, 0);
                await Task.Delay(18, schedule.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_coverSchedule, schedule)) _coverSchedule = null;
            schedule.Dispose();
        }
    }

    private LinkedListNode<CachedImage>? FindThumbnail(ImageKey key, bool allowSmaller = false)
    {
        if (_cache.TryGetValue(key, out var node)) return node;
        if (!_reuseSizes || _cache.Count == 0) return null;
        // The cache uses at most eight pixel buckets. Keep the file revision and
        // thumbnail source in every lookup, including the temporary fallback.
        for (var pixels = key.Pixels + 32; pixels <= 256; pixels += 32)
            if (_cache.TryGetValue(key with { Pixels = pixels }, out node)) return node;
        if (allowSmaller)
            for (var pixels = key.Pixels - 32; pixels >= 32; pixels -= 32)
                if (_cache.TryGetValue(key with { Pixels = pixels }, out node)) return node;
        return null;
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
                        : SvgIconCache.GetFolderIcon(key.Pixels, key.Style);
                }
                finally { DecodeGate.Release(); }
            });
            if (lifetime != _lifetime) return;
            if (key.IsDirectory && !key.Icon.StartsWith("ai-", StringComparison.Ordinal) && key.Style != _folderStyle) return;
            // These bitmaps belong to SvgIconCache. Thumbnail eviction never disposes them.
            if (_types.Count >= 256) _types.Clear();
            _types[key] = bitmap;
            Changed?.Invoke();
        }
        catch (Exception) { /* The Fluent geometry remains available as a fallback. */ }
        finally { if (lifetime == _lifetime) _pendingTypes.Remove(key); }
    }

    private async Task LoadThumbnailAsync(FileSystemEntry entry, ImageKey key, CancellationTokenSource cancellation, int delayMs)
    {
        Bitmap? bitmap = null;
        var token = cancellation.Token;
        var provider = ThumbnailProvider;
        try
        {
            // Scrolling past an entry cancels it before a Quick Look helper is queued.
            await Task.Delay(delayMs, token);
            bitmap = await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                byte[]? bytes = null;
                if (key.IsFolderCover && FolderCoverProvider != null)
                {
                    var result = await FolderCoverProvider(entry, key.Pixels, token).ConfigureAwait(false);
                    bytes = result?.Bytes;
                }
                else if (CanRequestThumbnail(entry) && provider != null)
                {
                    var result = await provider(entry, key.Pixels, token).ConfigureAwait(false);
                    bytes = result?.Bytes;
                }
                token.ThrowIfCancellationRequested();
                if (bytes is not { Length: > 0 } && !string.IsNullOrEmpty(key.Source))
                {
                    bytes = await BoundedImageDecoder.ReadSourceAsync(key.Source, token).ConfigureAwait(false);
                }
                if (bytes is not { Length: > 0 }) return null;
                return BoundedImageDecoder.Decode(bytes, key.Pixels, token);
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

    private static bool CanRequestThumbnail(FileSystemEntry entry) => !entry.IsDirectory && !entry.IsVirtual
        && (Path.IsPathRooted(entry.FullPath) || entry.ArchiveThumbnailVersion != null);

    private void MarkFailed(ImageKey key)
    {
        if (_failed.Count >= 1024) _failed.Clear();
        _failed[key] = DateTime.UtcNow.Add(key.IsFolderCover ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(30));
    }

    public void InvalidateFolderCovers()
    {
        CancelFolderCoverRequests();
        foreach (var key in _cache.Keys.Where(key => key.IsFolderCover).ToArray())
        {
            var node = _cache[key];
            _lru.Remove(node);
            _cache.Remove(key);
            _bytes -= node.Value.Bytes;
            node.Value.Bitmap.Dispose();
        }
        foreach (var key in _failed.Keys.Where(key => key.IsFolderCover).ToArray()) _failed.Remove(key);
        Changed?.Invoke();
    }

    public void CancelFolderCoverRequests()
    {
        _coverSchedule?.Cancel();
        _coverSchedule = null;
        foreach (var (key, cancellation) in _requests.ToArray())
        {
            if (!key.IsFolderCover) continue;
            _requests.Remove(key);
            cancellation.Cancel();
        }
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
        _coverSchedule?.Cancel();
        _coverSchedule = null;
        foreach (var cancellation in _requests.Values) cancellation.Cancel();
        _requests.Clear();
        _visible.Clear();
        _hasVisibleEntries = false;
        foreach (var item in _lru) item.Bitmap.Dispose();
        _lru.Clear();
        _cache.Clear();
        _types.Clear();
        _pendingTypes.Clear();
        _failed.Clear();
        _bytes = 0;
    }

    public void TrimMemory(DateTime unusedBefore)
    {
        foreach (var node in _cache.Values.Where(n => n.Value.LastUsed < unusedBefore
            && !_visible.Contains(n.Value.Key)
            && (!_reuseSizes || !_visible.Contains(n.Value.Key with { Pixels = _pixelSize }))).ToArray())
        {
            _cache.Remove(node.Value.Key); _lru.Remove(node); _bytes -= node.Value.Bytes;
            node.Value.Bitmap.Dispose();
        }
        if (!_hasVisibleEntries && _lastVisibleUse < unusedBefore) { _types.Clear(); _failed.Clear(); }
    }

    private readonly record struct TypeKey(string Icon, string Extension, bool IsDirectory, int Pixels, FolderPhotoCoverStyle Style)
    {
        public static TypeKey For(FileSystemEntry entry, int pixels, FolderPhotoCoverStyle style)
        {
            var source = entry.DetailsIconSource;
            var badge = source.IsDirectory ? "" : FileIconRenderer.GetBadgeExtension(source.IconKey, source.Extension);
            return new(source.IconKey, badge, source.IsDirectory, pixels,
                source.IsDirectory && !source.IconKey.StartsWith("ai-", StringComparison.Ordinal) ? style : FolderPhotoCoverStyle.Default);
        }
    }
    private readonly record struct ImageKey(string Path, DateTime Modified, long Size, string? Source, int Pixels,
        bool IsFolderCover, string? ArchiveVersion)
    {
        public static ImageKey For(FileSystemEntry e, int pixels) => ForView(e, pixels, false);
        public static ImageKey ForView(FileSystemEntry e, int pixels, bool folderCoversEnabled) => new(
            e.FullPath, e.LastModified, e.Size,
            string.IsNullOrWhiteSpace(e.ThumbnailUrl) ? e.IconUrl : e.ThumbnailUrl, pixels,
            folderCoversEnabled && e.IsFolder && e.IsReadable && !e.IsSymbolicLink && !e.IsVirtual
                && System.IO.Path.IsPathFullyQualified(e.FullPath) && e.DetailsIconSource.IsDirectory,
            e.ArchiveThumbnailVersion);
    }
    private sealed record CachedImage(ImageKey Key, Bitmap Bitmap, long Bytes)
    {
        public DateTime LastUsed = DateTime.UtcNow;
    }
}
