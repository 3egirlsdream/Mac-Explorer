using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Converters;
using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.Controls;

/// <summary>Visible-only thumbnail loading; fallback images remain owned by the shared icon cache.</summary>
public sealed class HomeFileImage : Image
{
    private static readonly FileEntryToIconConverter Icons = new();
    private readonly FileSystemEntry _entry;
    private readonly Func<FileSystemEntry, int, CancellationToken, Task<ThumbnailResult?>> _provider;
    private CancellationTokenSource? _request;
    private HomeThumbnailCache.Lease? _lease;
    private HomeThumbnailCache _cache;
    private readonly bool _ownsCache;
    private Visual[] _ancestors = [];
    private bool _attached;
    private bool _inViewport;
    private bool _attempted;
    private int _pixels;

    public HomeFileImage(FileSystemEntry entry, double size,
        Func<FileSystemEntry, int, CancellationToken, Task<ThumbnailResult?>> provider, HomeThumbnailCache? cache = null)
    {
        _cache = cache ?? new HomeThumbnailCache();
        _ownsCache = cache == null;
        _entry = entry;
        _provider = provider;
        Width = Height = size;
        Stretch = Avalonia.Media.Stretch.Uniform;
        EffectiveViewportChanged += (_, e) =>
        {
            _inViewport = e.EffectiveViewport.Intersects(new Rect(Bounds.Size));
            UpdateRequest();
        };
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            _entry.PropertyChanged += OnEntryChanged;
            _ancestors = this.GetVisualAncestors().ToArray();
            foreach (var ancestor in _ancestors) ancestor.PropertyChanged += OnAncestorChanged;
            ShowFallback();
            _pixels = PixelSize();
            _lease = _cache.TryAcquire(_entry, _pixels);
            _attempted = _lease != null;
            if (_lease?.Bitmap is { } cached) Source = cached;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            _entry.PropertyChanged -= OnEntryChanged;
            foreach (var ancestor in _ancestors) ancestor.PropertyChanged -= OnAncestorChanged;
            _ancestors = [];
            Cancel();
            Source = null;
            _lease?.Dispose();
            _lease = null;
            _attempted = false;
            _inViewport = false;
            if (_ownsCache) { _cache.Dispose(); _cache = new HomeThumbnailCache(); }
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_entry != null && change.Property == IsVisibleProperty) UpdateRequest();
    }

    private void OnAncestorChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty) UpdateRequest();
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(FileSystemEntry.ThumbnailUrl) or nameof(FileSystemEntry.IconUrl))) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_attached) return;
            Cancel(); _attempted = false;
            UpdateRequest();
        });
    }

    private void ShowFallback()
    {
        Source = Icons.Convert(_entry.DetailsIconSource, typeof(Avalonia.Media.IImage),
            FileThumbnailSizing.GetPixelSize(Width, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1),
            CultureInfo.InvariantCulture) as Avalonia.Media.IImage;
    }

    private void UpdateRequest()
    {
        if (!_attached || !_inViewport || !IsEffectivelyVisible)
        {
            Cancel();
            if (_lease != null)
            {
                ShowFallback(); _lease.Dispose(); _lease = null; _attempted = false;
            }
            return;
        }
        var pixels = PixelSize();
        if (_pixels != pixels) { Cancel(); _attempted = false; _pixels = pixels; }
        if (_entry.IsDirectory || !_entry.IsReadable || _entry.IsVirtual || _attempted || _request != null) return;
        _request = new CancellationTokenSource();
        _ = LoadAsync(_request, pixels);
    }

    private int PixelSize() => FileThumbnailSizing.GetPixelSize(Math.Max(48, Width), TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);

    private async Task LoadAsync(CancellationTokenSource request, int pixels)
    {
        var token = request.Token;
        HomeThumbnailCache.Lease? lease = null;
        try
        {
            lease = _cache.TryAcquire(_entry, pixels) ?? await _cache.AcquireAsync(_entry, pixels, _provider, token);
            if (token.IsCancellationRequested || !ReferenceEquals(_request, request) || !_attached) return;
            _attempted = true;
            if (lease.Bitmap is { } bitmap) Source = bitmap;
            else ShowFallback();
            _lease?.Dispose(); _lease = lease; lease = null;
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            lease?.Dispose();
            if (ReferenceEquals(_request, request)) _request = null;
            request.Dispose();
        }
    }

    private void Cancel()
    {
        _request?.Cancel();
        _request = null;
    }
}
