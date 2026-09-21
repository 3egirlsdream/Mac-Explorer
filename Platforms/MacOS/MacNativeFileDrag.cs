using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MacExplorer.Views;

namespace MacExplorer.Platforms.MacOS;

internal static class MacNativeFileDrag
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DragCallback(IntPtr context, int phase, double x, double y, int operation);
    private static readonly DragCallback SessionCallback = OnSession;

    private static void OnSession(IntPtr context, int phase, double x, double y, int operation)
    {
        var handle = GCHandle.FromIntPtr(context);
        try
        {
            if (handle.Target is Action<FileDragSessionEvent> callback)
                callback(new((FileDragPhase)phase, new Point(x, y),
                    (operation & 1) != 0 ? DragDropEffects.Copy : (operation & 16) != 0 ? DragDropEffects.Move : DragDropEffects.None));
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError($"Native drag callback: {ex}"); }
        finally { if (phase == (int)FileDragPhase.Ended) handle.Free(); }
    }
    public static void Prepare(Bitmap preview)
    {
        using var pixels = CopyPreviewPixels(preview);
        using var buffer = pixels.Lock();
        MacExplorerPrepareFileDragPixels(buffer.Address, buffer.Size.Width, buffer.Size.Height, buffer.RowBytes);
    }

    private static WriteableBitmap CopyPreviewPixels(Bitmap preview)
    {
        var pixels = new WriteableBitmap(preview.PixelSize, new Vector(96, 96),
            PixelFormat.Rgba8888, AlphaFormat.Premul);
        try
        {
            using var buffer = pixels.Lock();
            preview.CopyPixels(buffer);
            return pixels;
        }
        catch
        {
            pixels.Dispose();
            throw;
        }
    }

    public static bool TryBeginFileDrag(
        TopLevel topLevel,
        Point point,
        IReadOnlyList<string> paths,
        Bitmap preview,
        DragDropEffects allowedEffects,
        Action<FileDragSessionEvent>? callback = null)
    {
        if (!OperatingSystem.IsMacOS() || paths.Count == 0)
            return false;

        var nsView = GetNSView(topLevel);
        if (nsView == IntPtr.Zero)
            return false;

        var payload = EncodeNullSeparatedUtf8(paths);
        if (payload.Length == 0)
            return false;

        GCHandle callbackHandle = default;
        var started = false;
        try
        {
            if (callback != null) callbackHandle = GCHandle.Alloc(callback);
            using var pixels = CopyPreviewPixels(preview);
            using var buffer = pixels.Lock();
            // AppKit copies these pixels before returning; no encoded image or file is needed.
            started = MacExplorerBeginFileDragPixels(
                nsView,
                point.X,
                point.Y,
                payload,
                payload.Length,
                buffer.Address,
                buffer.Size.Width,
                buffer.Size.Height,
                buffer.RowBytes,
                ToNSDragOperation(allowedEffects),
                callback == null ? IntPtr.Zero : GCHandle.ToIntPtr(callbackHandle),
                callback == null ? null : SessionCallback) != 0;
            return started;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally { if (!started && callbackHandle.IsAllocated) callbackHandle.Free(); }
    }

    private static IntPtr GetNSView(TopLevel topLevel)
    {
        var handle = topLevel.TryGetPlatformHandle();
        if (handle is IMacOSTopLevelPlatformHandle macHandle)
            return macHandle.NSView;

        return handle != null && string.Equals(handle.HandleDescriptor, "NSView", StringComparison.Ordinal)
            ? handle.Handle
            : IntPtr.Zero;
    }

    private static byte[] EncodeNullSeparatedUtf8(IEnumerable<string> paths)
    {
        using var stream = new MemoryStream();
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var bytes = Encoding.UTF8.GetBytes(path);
            stream.Write(bytes);
            stream.WriteByte(0);
        }

        return stream.ToArray();
    }

    private static int ToNSDragOperation(DragDropEffects effects)
    {
        var operations = 0;
        if (effects.HasFlag(DragDropEffects.Copy))
            operations |= 1; // NSDragOperationCopy
        if (effects.HasFlag(DragDropEffects.Link))
            operations |= 2; // NSDragOperationLink
        if (effects.HasFlag(DragDropEffects.Move))
            operations |= 16; // NSDragOperationMove
        return operations;
    }

    [DllImport("MacExplorerNativeDrag", EntryPoint = "MacExplorerPrepareFileDragPixels")]
    private static extern void MacExplorerPrepareFileDragPixels(
        IntPtr previewPixels, int previewWidth, int previewHeight, int previewStride);

    [DllImport("MacExplorerNativeDrag", EntryPoint = "MacExplorerBeginFileDragPixels")]
    private static extern int MacExplorerBeginFileDragPixels(
        IntPtr nsView,
        double x,
        double y,
        byte[] pathsUtf8,
        int pathsByteLength,
        IntPtr previewPixels,
        int previewWidth,
        int previewHeight,
        int previewStride,
        int operationMask, IntPtr context, DragCallback? callback);
}
