using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using Svg.Skia;

namespace MacExplorer.Services.Impl;

/// <summary>Immutable source shared by render jobs; replacement releases it after the last job finishes.</summary>
internal sealed class BackgroundThemeArtwork(SKBitmap? texture, Bitmap preview, byte[] png, Bitmap? darkPreview = null) : IDisposable
{
    private int _references = 1;
    public SKBitmap? Texture { get; } = texture;
    public Bitmap Preview { get; } = preview;
    public Bitmap DarkPreview => darkPreview ?? Preview;
    public byte[] Png { get; private set; } = png;
    public void ReleaseEncoded() => Png = [];
    public BackgroundThemeArtwork Retain() { Interlocked.Increment(ref _references); return this; }

    public static BackgroundThemeArtwork Decode(byte[] bytes, CancellationToken token)
    {
        var decoded = BoundedImageDecoder.DecodeSkia(bytes, 2048, token) ?? throw new InvalidDataException();
        try
        {
            using var encoded = decoded.Encode(SKEncodedImageFormat.Png, 90);
            return new(decoded, CreateThumbnail(decoded), encoded.ToArray());
        }
        catch { decoded.Dispose(); throw; }
    }

    public static SKBitmap DecodePresetTexture(string asset, CancellationToken token)
    {
        using var stream = AssetLoader.Open(new Uri("avares://MacExplorer/Assets/BackgroundThemes/" + asset));
        if (!asset.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            using var data = new MemoryStream();
            stream.CopyTo(data);
            return BoundedImageDecoder.DecodeSkia(data.ToArray(), 2048, token) ?? throw new InvalidDataException();
        }
        // Only packaged SVGs use this path; imported files retain bounded raster decoding.
        using var svg = new SKSvg();
        svg.Load(stream);
        if (svg.Picture is not { } picture || picture.CullRect.Width <= 0 || picture.CullRect.Height <= 0)
            throw new InvalidDataException();
        var scale = 1600 / Math.Max(picture.CullRect.Width, picture.CullRect.Height);
        var bitmap = new SKBitmap(new SKImageInfo((int)Math.Ceiling(picture.CullRect.Width * scale),
            (int)Math.Ceiling(picture.CullRect.Height * scale), SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale);
        canvas.Translate(-picture.CullRect.Left, -picture.CullRect.Top);
        canvas.DrawPicture(picture);
        return bitmap;
    }

    public static Bitmap ToBitmap(SKBitmap bitmap) => new(
        bitmap.ColorType == SKColorType.Rgba8888 ? PixelFormat.Rgba8888 : PixelFormat.Bgra8888, AlphaFormat.Premul,
        bitmap.GetPixels(), new PixelSize(bitmap.Width, bitmap.Height), new Vector(96, 96), bitmap.RowBytes);

    private static Bitmap CreateThumbnail(SKBitmap source)
    {
        var size = BoundedImageDecoder.Fit(source.Width, source.Height, 512);
        using var small = source.Resize(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear));
        return ToBitmap(small);
    }

    public void Dispose()
    {
        if (Interlocked.Decrement(ref _references) != 0) return;
        Preview.Dispose(); darkPreview?.Dispose(); Texture?.Dispose();
    }
}
