using System.Runtime.InteropServices;
using SkiaSharp;

namespace MacExplorer.Platforms.MacCatalyst.Services;

// Only the stable LibRaw C API is used. In particular, libraw_data_t stays opaque.
internal static class LibRawThumbnailDecoder
{
    private const string Library = "libraw.dylib";
    // Covers current 100-150 MP cameras (including sensor margins). LibRaw owns
    // its RGB output; our RGBA copy is created only at the requested preview size.
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
            var rawWidth = GetRawWidth(context); var rawHeight = GetRawHeight(context);
            if (!ValidDimensions(rawWidth, rawHeight)) return null;
            MacExplorer.Services.MemoryDiagnostics.Record("image.raw_decode", new { width = rawWidth, height = rawHeight, maxPixelSize });
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

            var scale = Math.Min(1.0, (double)Math.Clamp(maxPixelSize, 32, MacExplorer.Services.BoundedImageDecoder.MaxOutputEdge) / Math.Max(width, height));
            var outputWidth = Math.Max(1, (int)Math.Round(width * scale));
            var outputHeight = Math.Max(1, (int)Math.Round(height * scale));
            using var bitmap = new SKBitmap(new SKImageInfo(outputWidth, outputHeight, SKColorType.Rgba8888, SKAlphaType.Opaque));
            var destination = bitmap.GetPixels();
            if (destination == IntPtr.Zero) return null;
            // Sample the developed RGB buffer directly. A 150 MP source previously
            // allocated another ~600 MiB RGBA image just to shrink it to a thumbnail.
            unsafe
            {
                var rgb = (byte*)image + 16; // libraw_processed_image_t.data
                var rgba = (byte*)destination;
                for (var y = 0; y < outputHeight; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sy = Math.Clamp((y + .5) * height / outputHeight - .5, 0, height - 1);
                    var y0 = (int)sy; var y1 = Math.Min(height - 1, y0 + 1); var fy = sy - y0;
                    var row = rgba + y * bitmap.RowBytes;
                    for (var x = 0; x < outputWidth; x++)
                    {
                        var sx = Math.Clamp((x + .5) * width / outputWidth - .5, 0, width - 1);
                        var x0 = (int)sx; var x1 = Math.Min(width - 1, x0 + 1); var fx = sx - x0;
                        var pixel = row + x * 4;
                        for (var channel = 0; channel < 3; channel++)
                        {
                            var c = colors == 1 ? 0 : channel;
                            var top = rgb[((long)y0 * width + x0) * colors + c] * (1 - fx)
                                + rgb[((long)y0 * width + x1) * colors + c] * fx;
                            var bottom = rgb[((long)y1 * width + x0) * colors + c] * (1 - fx)
                                + rgb[((long)y1 * width + x1) * colors + c] * fx;
                            pixel[channel] = (byte)Math.Round(top * (1 - fy) + bottom * fy);
                        }
                        pixel[3] = 255;
                    }
                }
            }
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
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
