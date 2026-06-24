using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace MacExplorer.Platforms.MacOS;

internal static class MacWindowChrome
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private const string NativeHelper = "MacExplorerNativeDrag";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CGPoint
    {
        public readonly double X;
        public readonly double Y;
    }

    public static void MakeTransparent(TopLevel topLevel)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var nsView = GetNSView(topLevel);
        if (nsView == IntPtr.Zero)
            return;

        try
        {
            var nsWindow = SendIntPtr(nsView, "window");
            if (nsWindow == IntPtr.Zero)
                return;

            var clearColor = SendIntPtr(GetClass("NSColor"), "clearColor");
            var clearCgColor = clearColor == IntPtr.Zero ? IntPtr.Zero : SendIntPtr(clearColor, "CGColor");

            SendBool(nsWindow, "setOpaque:", false);
            SendIntPtrArg(nsWindow, "setBackgroundColor:", clearColor);
            SendBool(nsWindow, "setHasShadow:", false);

            ApplyClearLayer(nsView, clearCgColor);

            var contentView = SendIntPtr(nsWindow, "contentView");
            if (contentView != IntPtr.Zero && contentView != nsView)
                ApplyClearLayer(contentView, clearCgColor);

            SendVoid(nsWindow, "invalidateShadow");
        }
        catch (DllNotFoundException)
        {
            // Non-macOS or restricted runtime; Avalonia's managed transparency hint remains in effect.
        }
        catch (EntryPointNotFoundException)
        {
            // Same fallback as above.
        }
    }

    public static bool TryGetPointerScreenPosition(out Point position)
    {
        position = default;
        if (!OperatingSystem.IsMacOS())
            return false;

        var cgEvent = CGEventCreate(IntPtr.Zero);
        if (cgEvent == IntPtr.Zero)
            return false;

        try
        {
            var location = CGEventGetLocation(cgEvent);
            position = new Point(location.X, location.Y);
            return true;
        }
        finally
        {
            CFRelease(cgEvent);
        }
    }

    public static bool TrySetWindowFrame(
        TopLevel topLevel,
        double width,
        double height,
        bool keepRightEdge,
        bool keepTopEdge)
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        var nsView = GetNSView(topLevel);
        if (nsView == IntPtr.Zero)
            return false;

        try
        {
            return MacExplorerSetWindowFrame(
                nsView,
                width,
                height,
                keepRightEdge ? 1 : 0,
                keepTopEdge ? 1 : 0) != 0;
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

    private static void ApplyClearLayer(IntPtr nsView, IntPtr clearCgColor)
    {
        SendBool(nsView, "setWantsLayer:", true);

        var layer = SendIntPtr(nsView, "layer");
        if (layer == IntPtr.Zero)
            return;

        SendBool(layer, "setOpaque:", false);
        if (clearCgColor != IntPtr.Zero)
            SendIntPtrArg(layer, "setBackgroundColor:", clearCgColor);
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

    private static IntPtr GetClass(string name)
        => objc_getClass(name);

    private static IntPtr GetSelector(string name)
        => sel_registerName(name);

    private static IntPtr SendIntPtr(IntPtr receiver, string selector)
        => receiver == IntPtr.Zero ? IntPtr.Zero : objc_msgSend(receiver, GetSelector(selector));

    private static void SendVoid(IntPtr receiver, string selector)
    {
        if (receiver != IntPtr.Zero)
            objc_msgSend_void(receiver, GetSelector(selector));
    }

    private static void SendBool(IntPtr receiver, string selector, bool value)
    {
        if (receiver != IntPtr.Zero)
            objc_msgSend_bool(receiver, GetSelector(selector), value);
    }

    private static void SendIntPtrArg(IntPtr receiver, string selector, IntPtr value)
    {
        if (receiver != IntPtr.Zero && value != IntPtr.Zero)
            objc_msgSend_intptr(receiver, GetSelector(selector), value);
    }

    [DllImport(LibObjC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(LibObjC)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_bool(
        IntPtr receiver,
        IntPtr selector,
        [MarshalAs(UnmanagedType.I1)] bool value);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_intptr(IntPtr receiver, IntPtr selector, IntPtr value);

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGEventCreate(IntPtr source);

    [DllImport(CoreGraphics)]
    private static extern CGPoint CGEventGetLocation(IntPtr cgEvent);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    [DllImport(NativeHelper, EntryPoint = "MacExplorerSetWindowFrame")]
    private static extern int MacExplorerSetWindowFrame(
        IntPtr nsView,
        double width,
        double height,
        int keepRightEdge,
        int keepTopEdge);
}
