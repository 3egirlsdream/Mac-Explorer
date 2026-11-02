using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MacExplorer.Models;
using SkiaSharp;

namespace MacExplorer.Services.Impl;

/// <summary>A bounded window composition and its blur, produced from exactly the same pixels.</summary>
internal sealed class BackgroundThemeComposition(Bitmap image, Bitmap frosted, Color[] titleColors) : IDisposable
{
    public Bitmap Image { get; } = image;
    public Bitmap Frosted { get; } = frosted;
    public Color[] TitleColors { get; } = titleColors;

    public static BackgroundThemeComposition Create(SKBitmap? texture, BackgroundThemeOptions options,
        Size window, bool dark, double scaling, CancellationToken token)
    {
        var size = BoundedImageDecoder.Fit(Math.Max(1, (int)Math.Ceiling(window.Width * scaling)),
            Math.Max(1, (int)Math.Ceiling(window.Height * scaling)), 2048);
        using var composed = Render(texture, options, size.Width, size.Height, dark, token);
        token.ThrowIfCancellationRequested();
        var blurSize = BoundedImageDecoder.Fit(size.Width, size.Height, 1024);
        using var small = composed.Resize(new SKImageInfo(blurSize.Width, blurSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear));
        using var surface = SKSurface.Create(small.Info);
        using var filter = SKImageFilter.CreateBlur((float)(5 * blurSize.Width / window.Width),
            (float)(5 * blurSize.Height / window.Height), SKShaderTileMode.Clamp);
        using var paint = new SKPaint { ImageFilter = filter };
        surface.Canvas.DrawBitmap(small, 0, 0, paint);
        using var snapshot = surface.Snapshot();
        using var blurred = SKBitmap.FromImage(snapshot);
        var titleColors = CreateTitleColors(blurred, options, dark, token);
        token.ThrowIfCancellationRequested();
        var image = BackgroundThemeArtwork.ToBitmap(composed);
        try { return new(image, BackgroundThemeArtwork.ToBitmap(blurred), titleColors); }
        catch { image.Dispose(); throw; }
    }

    internal static Color[] CreateTitleColors(SKBitmap composed, BackgroundThemeOptions options, bool dark, CancellationToken token)
    {
        // Full-color artwork and photos supply broad color averages, never image details.
        // Layered overlay textures can still use their independent palette.
        using var palette = BackgroundThemePresets.Find(options.PresetId)?.Blend == BackgroundTextureBlend.Overlay
            ? Render(null, options, 16, 16, dark, token) : null;
        var colors = new Color[3];
        for (var band = 0; band < colors.Length; band++)
        {
            double red = 0, green = 0, blue = 0;
            if (palette != null)
            {
                var color = palette.GetPixel(band * 15 / 2, 0);
                red = color.Red; green = color.Green; blue = color.Blue;
            }
            else
            {
                for (var y = 0; y < 16; y++)
                    for (var x = 0; x < 24; x++)
                    {
                        var color = composed.GetPixel(Math.Min(composed.Width - 1, (int)((band + (x + 0.5) / 24) * composed.Width / 3)),
                            Math.Min(composed.Height - 1, (int)((y + 0.5) * composed.Height / 48)));
                        red += color.Red / 384d; green += color.Green / 384d; blue += color.Blue / 384d;
                    }
            }
            var neutral = dark ? 27 : 255;
            colors[band] = Color.FromRgb((byte)Math.Round(red * 0.85 + neutral * 0.15),
                (byte)Math.Round(green * 0.85 + neutral * 0.15), (byte)Math.Round(blue * 0.85 + neutral * 0.15));
        }
        return colors;
    }

    internal static SKBitmap Render(SKBitmap? texture, BackgroundThemeOptions options,
        int width, int height, bool dark, CancellationToken token)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        try
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColor.Parse(dark ? "#1B1D21" : "#FFFFFF"));
            var preset = BackgroundThemePresets.Find(options.PresetId);
            if (preset != null)
            {
                // A four-corner palette spans the whole window; only the texture repeats.
                var colors = options.GetColors(preset.Id).GetColors(preset, dark).Select(SKColor.Parse).ToArray();
                using var palette = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Bgra8888, SKAlphaType.Premul));
                for (var i = 0; i < 4; i++) palette.SetPixel(i % 2, i / 2, colors[i]);
                using var gradient = palette.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                    new SKSamplingOptions(SKFilterMode.Linear), SKMatrix.CreateScaleTranslation(width, height, -width / 2f, -height / 2f));
                using var colorPaint = new SKPaint { Shader = gradient };
                canvas.DrawRect(0, 0, width, height, colorPaint);
            }
            token.ThrowIfCancellationRequested();
            if (texture != null)
            {
                var tile = CalculateImageRect(new Size(texture.Width, texture.Height), new Size(width, height), options.Layout);
                using var shader = texture.ToShader(options.Layout == BackgroundImageLayout.HeightTile ? SKShaderTileMode.Repeat : SKShaderTileMode.Clamp,
                    SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear),
                    SKMatrix.CreateScaleTranslation((float)(tile.Width / texture.Width), (float)(tile.Height / texture.Height), (float)tile.X, (float)tile.Y));
                using var texturePaint = new SKPaint
                {
                    Shader = shader,
                    Color = SKColors.White.WithAlpha((byte)Math.Round((preset == null ? options.Strength : options.TextureStrength) * 2.55)),
                    BlendMode = preset?.Blend == BackgroundTextureBlend.Overlay ? SKBlendMode.Overlay : SKBlendMode.SrcOver
                };
                canvas.DrawRect(0, 0, width, height, texturePaint);
            }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    internal static Rect CalculateImageRect(Size image, Size window, BackgroundImageLayout layout)
    {
        if (image.Width <= 0 || image.Height <= 0 || window.Width <= 0 || window.Height <= 0) return default;
        var scale = window.Height / image.Height;
        if (layout == BackgroundImageLayout.Cover)
        {
            scale = Math.Max(scale, window.Width / image.Width);
            var size = image * scale;
            return new Rect((window.Width - size.Width) / 2, (window.Height - size.Height) / 2, size.Width, size.Height);
        }
        return new Rect(0, 0, image.Width * scale, window.Height);
    }

    public void Dispose() { Image.Dispose(); Frosted.Dispose(); }
}
