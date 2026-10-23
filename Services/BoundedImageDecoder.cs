using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace MacExplorer.Services;

/// <summary>Checks codec dimensions before allocating pixels. Shared by every raster preview.</summary>
internal static class BoundedImageDecoder
{
    internal const int MaxEncodedBytes = 32 * 1024 * 1024;
    internal const int MaxOutputEdge = 2048;
    internal const long MaxWorkingBytes = 128L * 1024 * 1024;
    // At most two decoders, each with a 128 MiB estimated working-set allowance.
    // This includes decoded pixels, resize/output copies and encoded input; native codecs
    // can have additional internal allocations, so it is not a process memory limit.
    private static readonly SemaphoreSlim Gate = new(2);
    private static int _active, _waiting;
    private static long _reserved, _decoded, _rejected;
    internal static object Statistics => new { active = Volatile.Read(ref _active), waiting = Volatile.Read(ref _waiting),
        reservedBytes = Interlocked.Read(ref _reserved), decoded = Interlocked.Read(ref _decoded), rejected = Interlocked.Read(ref _rejected) };

    public static Bitmap? Decode(byte[] bytes, int maxEdge, CancellationToken token = default)
        => DecodeCore(bytes, maxEdge, token, bitmap => new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul,
            bitmap.GetPixels(), new PixelSize(bitmap.Width, bitmap.Height), new Vector(96, 96), bitmap.RowBytes));

    public static SKBitmap? DecodeSkia(byte[] bytes, int maxEdge, CancellationToken token = default)
        => DecodeCore(bytes, maxEdge, token, bitmap => bitmap.Copy());

    public static byte[]? DecodePng(byte[] bytes, int maxEdge, CancellationToken token = default)
        => DecodeCore(bytes, maxEdge, token, bitmap =>
        {
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 90);
            return encoded?.ToArray();
        });

    public static async Task<byte[]?> ReadSourceAsync(string source, CancellationToken token = default)
    {
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = source.IndexOf(',');
            if (comma < 0 || source.Length - comma - 1 > MaxEncodedBytes / 3 * 4) return null;
            token.ThrowIfCancellationRequested();
            return Convert.FromBase64String(source[(comma + 1)..]);
        }
        if (!File.Exists(source)) return null;
        await using var input = File.OpenRead(source);
        var length = input.Length;
        if (length > MaxEncodedBytes) return null;
        // Read exactly the observed length: a growing file cannot bypass the input bound.
        var bytes = new byte[(int)length];
        await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }

    internal static SKSizeI Fit(int width, int height, int maxEdge)
    {
        if (width <= 0 || height <= 0) return default;
        var scale = Math.Min(1d, Math.Clamp(maxEdge, 1, MaxOutputEdge) / (double)Math.Max(width, height));
        return new(Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));
    }

    private static T? DecodeCore<T>(byte[] bytes, int maxEdge, CancellationToken token, Func<SKBitmap, T?> convert) where T : class
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length == 0 || bytes.Length > MaxEncodedBytes) return null;
        Interlocked.Increment(ref _waiting);
        try { Gate.Wait(token); }
        finally { Interlocked.Decrement(ref _waiting); }
        Interlocked.Increment(ref _active);
        long reservation = 0;
        try
        {
            using var stream = new SKMemoryStream(bytes);
            using var codec = SKCodec.Create(stream);
            // DNG's container is not a developed RAW photograph. The native/LibRaw path handles it.
            if (codec == null || codec.EncodedFormat == SKEncodedImageFormat.Dng) return null;
            var info = codec.Info;
            var target = Fit(info.Width, info.Height, maxEdge);
            if (target.Width == 0) return null;
            var scale = Math.Min(1f, Math.Clamp(maxEdge, 1, MaxOutputEdge) / (float)Math.Max(info.Width, info.Height));
            var sampled = codec.GetScaledDimensions(scale);
            // PNG and some other codecs return the original dimensions here. Reject an
            // oversized decode instead of first allocating the original and then resizing.
            var sampledPixels = (long)Math.Max(0, sampled.Width) * Math.Max(0, sampled.Height);
            var estimate = sampledPixels > MaxWorkingBytes / 8 ? MaxWorkingBytes + 1
                : sampledPixels * 8 + (long)target.Width * target.Height * 12 + bytes.Length * 2L;
            if (sampled.Width <= 0 || sampled.Height <= 0 || estimate > MaxWorkingBytes)
            {
                Interlocked.Increment(ref _rejected);
                MemoryDiagnostics.Record("image.decode_rejected", new { format = codec.EncodedFormat.ToString(), info.Width, info.Height,
                    sampledWidth = sampled.Width, sampledHeight = sampled.Height, encodedBytes = bytes.Length, estimatedBytes = estimate });
                return null;
            }
            reservation = estimate;
            Interlocked.Add(ref _reserved, reservation);
            token.ThrowIfCancellationRequested();
            using var decoded = new SKBitmap(new SKImageInfo(sampled.Width, sampled.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
            if (result != SKCodecResult.Success) return null;
            token.ThrowIfCancellationRequested();
            using var resized = sampled == target ? null : decoded.Resize(
                new SKImageInfo(target.Width, target.Height, SKColorType.Bgra8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            if (sampled != target && resized == null) return null;
            var output = convert(resized ?? decoded);
            Interlocked.Increment(ref _decoded);
            if (estimate >= 32L * 1024 * 1024)
                MemoryDiagnostics.Record("image.large_decode", new { format = codec.EncodedFormat.ToString(), info.Width, info.Height,
                    targetWidth = target.Width, targetHeight = target.Height, estimatedBytes = estimate });
            return output;
        }
        finally
        {
            Interlocked.Add(ref _reserved, -reservation);
            Interlocked.Decrement(ref _active);
            Gate.Release();
        }
    }
}
