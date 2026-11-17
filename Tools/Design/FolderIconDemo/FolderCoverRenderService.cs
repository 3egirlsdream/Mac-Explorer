using MacExplorer.Services;
using MacExplorer.Services.Impl;
using SkiaSharp;

namespace FolderIconDemo;

internal readonly record struct FolderCoverMaterial(float Frost, float Blur, bool Dark, bool TwoPhotos);

// Like FolderPhotoCoverService, only two covers may be prepared concurrently.
// Inputs are immutable encoded thumbnails, so cancelled/replaced UI requests
// cannot dispose an image that a background render is still reading.
internal sealed class FolderCoverRenderService
{
    private readonly SemaphoreSlim _gate = new(2);
    private int _active, _maximumActive, _generated;
    public int MaximumActive => Volatile.Read(ref _maximumActive);
    public int Generated => Volatile.Read(ref _generated);

    public Task<ThumbnailResult?> CreateAsync(byte[] cover, byte[]? rear, int pixels,
        FolderCoverMaterial material, CancellationToken token)
        => Task.Run(async () =>
        {
            pixels = Math.Clamp(pixels, 32, 1024);
            await _gate.WaitAsync(token).ConfigureAwait(false);
            var active = Interlocked.Increment(ref _active);
            var maximum = Volatile.Read(ref _maximumActive);
            while (active > maximum)
            {
                var observed = Interlocked.CompareExchange(ref _maximumActive, active, maximum);
                if (observed == maximum) break;
                maximum = observed;
            }
            try
            {
                token.ThrowIfCancellationRequested();
                using var first = DecodePhoto(cover, pixels, token);
                using var second = material.TwoPhotos && rear != null ? DecodePhoto(rear, pixels, token) : null;
                var png = FrostedFolderRenderer.RenderPng(first, second, pixels,
                    material.Frost, material.Blur, material.Dark, token);
                Interlocked.Increment(ref _generated);
                return (ThumbnailResult?)new ThumbnailResult(png, "");
            }
            finally { Interlocked.Decrement(ref _active); _gate.Release(); }
        }, token);

    internal static SKBitmap DecodePhoto(byte[] bytes, int pixels, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var input = new SKMemoryStream(bytes);
        using var codec = SKCodec.Create(input) ?? throw new InvalidDataException("Unsupported cover image.");
        var scale = Math.Min(1f, (float)pixels / Math.Max(codec.Info.Width, codec.Info.Height));
        var sampled = codec.GetScaledDimensions(scale);
        if ((long)sampled.Width * sampled.Height * 8 + bytes.LongLength > 128L * 1024 * 1024)
            throw new InvalidDataException("Cover decode exceeds the working-set limit.");
        using var decoded = new SKBitmap(new SKImageInfo(sampled.Width, sampled.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success)
            throw new InvalidDataException("Unable to decode cover image.");
        token.ThrowIfCancellationRequested();
        var ratio = Math.Min(1f, (float)pixels / Math.Max(sampled.Width, sampled.Height));
        if (ratio >= 1) return decoded.Copy() ?? throw new InvalidDataException("Unable to copy cover image.");
        return decoded.Resize(new SKImageInfo(Math.Max(1, (int)Math.Round(sampled.Width * ratio)),
                Math.Max(1, (int)Math.Round(sampled.Height * ratio)), SKColorType.Bgra8888, SKAlphaType.Premul),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
            ?? throw new InvalidDataException("Unable to resize cover image.");
    }
}
