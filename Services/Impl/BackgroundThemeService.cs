using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MacExplorer.Models;
using SkiaSharp;

namespace MacExplorer.Services.Impl;

/// <summary>One bounded, managed image and one blurred texture shared by all main windows.</summary>
public sealed class BackgroundThemeService : IDisposable
{
    internal const string SettingKey = "background_theme";
    private readonly ISettingsService _settings;
    private readonly string _directory;
    private Artwork? _artwork;
    private Task? _initialization;
    private int _generation;
    private bool _disposed;
    public event Action? Changed;
    public BackgroundThemeOptions Options { get; private set; }
    public Bitmap? Image => _artwork?.Image;
    internal Bitmap? FrostedImage => _artwork?.Frosted;
    public bool IsActive => Options.Enabled && Image != null;
    public string? ErrorKey { get; private set; }

    public BackgroundThemeService(ISettingsService settings)
        : this(settings, Path.Combine(RuntimePaths.DataDirectory, "Themes")) { }

    internal BackgroundThemeService(ISettingsService settings, string directory)
    {
        _settings = settings;
        _directory = directory;
        try { Options = (JsonSerializer.Deserialize<BackgroundThemeOptions>(settings.Get(SettingKey) ?? "{}") ?? new()).Normalize(); }
        catch (JsonException) { Options = new(); }
    }

    public Task InitializeAsync() => _initialization ??= LoadAsync();

    private async Task LoadAsync()
    {
        if (Options.ImageFile.Length == 0) return;
        var generation = _generation;
        Artwork? artwork = null;
        try
        {
            var bytes = await BoundedImageDecoder.ReadSourceAsync(Path.Combine(_directory, Options.ImageFile));
            if (bytes == null) throw new InvalidDataException();
            artwork = await Task.Run(() => Artwork.Decode(bytes, CancellationToken.None));
            if (artwork == null) throw new InvalidDataException();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || generation != _generation) return;
                artwork.ReleaseEncoded();
                _artwork = artwork; artwork = null;
                Changed?.Invoke();
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || generation != _generation) return;
                ErrorKey = "background.unavailable";
                Changed?.Invoke();
            });
        }
        finally { artwork?.Dispose(); }
    }

    public async Task ImportAsync(Stream source, string displayName, CancellationToken token = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        await InitializeAsync();
        token.ThrowIfCancellationRequested();
        var generation = ++_generation;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + count > BoundedImageDecoder.MaxEncodedBytes) throw new InvalidDataException();
            buffer.Write(chunk, 0, count);
        }
        Artwork? artwork = await Task.Run(() => Artwork.Decode(buffer.ToArray(), token), token);
        if (artwork == null) throw new InvalidDataException();
        var file = Guid.NewGuid().ToString("N") + ".png";
        var path = Path.Combine(_directory, file);
        try
        {
            Directory.CreateDirectory(_directory);
            await File.WriteAllBytesAsync(path, artwork.Png, token);
            artwork.ReleaseEncoded();
            token.ThrowIfCancellationRequested();
            if (_disposed || generation != _generation) return;
            var previous = Options.ImageFile;
            var old = _artwork;
            _artwork = artwork; artwork = null;
            Options = (Options with { Enabled = true, ImageFile = file, DisplayName = displayName }).Normalize();
            ErrorKey = null;
            Save();
            old?.Dispose();
            DeleteManagedFile(previous);
        }
        finally
        {
            artwork?.Dispose();
            if (Options.ImageFile != file) DeleteManagedFile(file);
        }
    }

    public void Update(bool enabled, BackgroundImageLayout layout, int strength)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var options = (Options with { Enabled = enabled, Layout = layout, Strength = strength }).Normalize();
        if (options == Options) return;
        Options = options;
        Save(deferred: true);
    }

    public void Reset()
    {
        Dispatcher.UIThread.VerifyAccess();
        ++_generation;
        var previous = Options.ImageFile;
        var old = _artwork;
        _artwork = null;
        Options = new();
        ErrorKey = null;
        Save();
        old?.Dispose();
        DeleteManagedFile(previous);
    }

    private void Save(bool deferred = false)
    {
        var json = JsonSerializer.Serialize(Options);
        if (deferred) _settings.SetDeferred(SettingKey, json);
        else _settings.Set(SettingKey, json);
        Changed?.Invoke();
    }

    private void DeleteManagedFile(string file)
    {
        // Only files created by this service may be removed, never the imported source.
        if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out _) || Path.GetExtension(file) != ".png") return;
        try { File.Delete(Path.Combine(_directory, Path.GetFileName(file))); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _disposed = true; ++_generation;
        var old = _artwork; _artwork = null;
        Changed?.Invoke();
        Changed = null;
        old?.Dispose();
    }

    private sealed class Artwork(Bitmap image, Bitmap frosted, byte[] png) : IDisposable
    {
        public Bitmap Image { get; } = image;
        public Bitmap Frosted { get; } = frosted;
        public byte[] Png { get; private set; } = png;
        public void ReleaseEncoded() => Png = [];

        public static Artwork? Decode(byte[] bytes, CancellationToken token)
        {
            using var decoded = BoundedImageDecoder.DecodeSkia(bytes, 2048, token);
            if (decoded == null) return null;
            using var encoded = decoded.Encode(SKEncodedImageFormat.Png, 90);
            var size = BoundedImageDecoder.Fit(decoded.Width, decoded.Height, 1024);
            using var small = decoded.Resize(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear));
            if (small == null) return null;
            using var surface = SKSurface.Create(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var filter = SKImageFilter.CreateBlur(Math.Max(1, size.Height * 5f / 800), Math.Max(1, size.Height * 5f / 800), SKShaderTileMode.Clamp);
            using var paint = new SKPaint { ImageFilter = filter };
            surface.Canvas.DrawBitmap(small, 0, 0, paint);
            using var blurred = surface.Snapshot();
            using var raster = SKBitmap.FromImage(blurred);
            token.ThrowIfCancellationRequested();
            return new Artwork(ToBitmap(decoded), ToBitmap(raster), encoded.ToArray());
        }

        private static Bitmap ToBitmap(SKBitmap bitmap) => new(
            bitmap.ColorType == SKColorType.Rgba8888 ? PixelFormat.Rgba8888 : PixelFormat.Bgra8888, AlphaFormat.Premul,
            bitmap.GetPixels(), new PixelSize(bitmap.Width, bitmap.Height), new Vector(96, 96), bitmap.RowBytes);

        public void Dispose() { Image.Dispose(); Frosted.Dispose(); }
    }
}
