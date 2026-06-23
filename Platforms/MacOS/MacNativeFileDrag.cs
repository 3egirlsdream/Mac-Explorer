using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;

namespace MacExplorer.Platforms.MacOS;

internal static class MacNativeFileDrag
{
    public static bool TryBeginFileDrag(
        TopLevel topLevel,
        Point point,
        IReadOnlyList<string> paths,
        string? previewPath,
        DragDropEffects allowedEffects)
    {
        if (!OperatingSystem.IsMacOS() || paths.Count == 0)
            return false;

        var nsView = GetNSView(topLevel);
        if (nsView == IntPtr.Zero)
            return false;

        var payload = EncodeNullSeparatedUtf8(paths);
        if (payload.Length == 0)
            return false;

        try
        {
            return MacExplorerBeginFileDrag(
                nsView,
                point.X,
                point.Y,
                payload,
                payload.Length,
                previewPath,
                ToNSDragOperation(allowedEffects)) != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
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

    [DllImport("MacExplorerNativeDrag", EntryPoint = "MacExplorerBeginFileDrag")]
    private static extern int MacExplorerBeginFileDrag(
        IntPtr nsView,
        double x,
        double y,
        byte[] pathsUtf8,
        int pathsByteLength,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? previewPath,
        int operationMask);
}
