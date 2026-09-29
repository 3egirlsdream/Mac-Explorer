using System.Runtime.InteropServices;
using SkiaSharp;

namespace MacExplorer.Platforms.MacCatalyst.Services;

// Only the stable LibRaw C API is used. In particular, libraw_data_t stays opaque.
internal static class LibRawThumbnailDecoder
{
    private const string Library = "libraw.dylib";
    // Covers current 100-150 MP cameras (including sensor margins), while
    // bounding LibRaw's RGB buffer and the Skia bitmap to about 1.1 GiB total.
    private const int MaxPixels = 160_000_000;
    private const int BitmapImage = 2;
    private static readonly ProgressCallback CancellationCallback = CheckCancellation;

    internal static byte[]? Decode(string path, int maxPixelSize, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IntPtr context = IntPtr.Zero;
        IntPtr image = IntPtr.Zero;
        var handle = GCHandle.Alloc(cancellationToken);
        try
        {
            context = Init(0);
            if (context == IntPtr.Zero) return null;
            SetProgressHandler(context, CancellationCallback, GCHandle.ToIntPtr(handle));
            if (OpenFile(context, path) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!ValidDimensions(GetRawWidth(context), GetRawHeight(context))) return null;
            if (Unpack(context) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
            cancellationToken.ThrowIfCancellationRequested();

            // LibRaw applies the camera orientation while copying the processed bitmap.
            SetOutputColor(context, 1); // sRGB
            SetOutputBits(context, 8);
            if (Process(context) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            image = MakeMemoryImage(context, out var error);
            if (image == IntPtr.Zero || error != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }

            var type = Marshal.ReadInt32(image);
            var height = (ushort)Marshal.ReadInt16(image, 4);
            var width = (ushort)Marshal.ReadInt16(image, 6);
            var colors = (ushort)Marshal.ReadInt16(image, 8);
            var bits = (ushort)Marshal.ReadInt16(image, 10);
            var length = (uint)Marshal.ReadInt32(image, 12);
            if (type != BitmapImage || (colors != 1 && colors != 3) || bits != 8 ||
                !ValidDimensions(width, height) || (long)width * height * colors != length)
                return null;

            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
            var destination = bitmap.GetPixels();
            if (destination == IntPtr.Zero) return null;
            unsafe
            {
                var rgb = (byte*)image + 16; // libraw_processed_image_t.data
                var rgba = (byte*)destination;
                for (var y = 0; y < height; y++)
                {
                    var row = rgba + y * bitmap.RowBytes;
                    for (var x = 0; x < width; x++)
                    {
                        var source = rgb + ((long)y * width + x) * colors;
                        var pixel = row + x * 4;
                        pixel[0] = source[0];
                        pixel[1] = colors == 1 ? source[0] : source[1];
                        pixel[2] = colors == 1 ? source[0] : source[2];
                        pixel[3] = 255;
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var scale = Math.Min(1.0, (double)Math.Max(32, maxPixelSize) / Math.Max(width, height));
            var outputWidth = Math.Max(1, (int)Math.Round(width * scale));
            var outputHeight = Math.Max(1, (int)Math.Round(height * scale));
            using var surface = SKSurface.Create(new SKImageInfo(outputWidth, outputHeight));
            if (surface == null) return null;
            using var skImage = SKImage.FromBitmap(bitmap);
            surface.Canvas.DrawImage(skImage, new SKRect(0, 0, outputWidth, outputHeight),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            using var snapshot = surface.Snapshot();
            using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            cancellationToken.ThrowIfCancellationRequested();
            return encoded?.ToArray();
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
        catch (BadImageFormatException) { return null; }
        finally
        {
            if (image != IntPtr.Zero) ClearMemoryImage(image);
            if (context != IntPtr.Zero) Close(context);
            handle.Free();
        }
    }

    private static bool ValidDimensions(int width, int height) =>
        width > 0 && height > 0 && (long)width * height <= MaxPixels;

    private static int CheckCancellation(IntPtr data, int stage, int iteration, int expected)
    {
        try { return ((CancellationToken)GCHandle.FromIntPtr(data).Target!).IsCancellationRequested ? 1 : 0; }
        catch { return 1; } // No managed exception may cross the native callback boundary.
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ProgressCallback(IntPtr data, int stage, int iteration, int expected);

    [DllImport(Library, EntryPoint = "libraw_init", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr Init(uint flags);
    [DllImport(Library, EntryPoint = "libraw_open_file", CallingConvention = CallingConvention.Cdecl)]
    private static extern int OpenFile(IntPtr context, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Library, EntryPoint = "libraw_get_raw_width", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetRawWidth(IntPtr context);
    [DllImport(Library, EntryPoint = "libraw_get_raw_height", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetRawHeight(IntPtr context);
    [DllImport(Library, EntryPoint = "libraw_unpack", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Unpack(IntPtr context);
    [DllImport(Library, EntryPoint = "libraw_set_output_color", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetOutputColor(IntPtr context, int color);
    [DllImport(Library, EntryPoint = "libraw_set_output_bps", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetOutputBits(IntPtr context, int bits);
    [DllImport(Library, EntryPoint = "libraw_dcraw_process", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Process(IntPtr context);
    [DllImport(Library, EntryPoint = "libraw_dcraw_make_mem_image", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr MakeMemoryImage(IntPtr context, out int error);
    [DllImport(Library, EntryPoint = "libraw_dcraw_clear_mem", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ClearMemoryImage(IntPtr image);
    [DllImport(Library, EntryPoint = "libraw_set_progress_handler", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetProgressHandler(IntPtr context, ProgressCallback callback, IntPtr data);
    [DllImport(Library, EntryPoint = "libraw_close", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Close(IntPtr context);
}
