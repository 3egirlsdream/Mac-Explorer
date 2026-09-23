using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace MacExplorer.Platforms.MacOS;

internal static class MacWindowChrome
{
    private const string NativeDragLibrary = "libMacExplorerNativeDrag";
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    // Keep this aligned with the previously verified Mac Catalyst implementation:
    // NSVisualEffectMaterialHUDWindow provides the continuous window material.
    private const nint NsVisualEffectMaterialHudWindow = 13;
    private const nint NsVisualEffectBlendingModeBehindWindow = 0;
    private const nuint NsViewWidthSizable = 2;
    private const nuint NsViewHeightSizable = 16;
    private static readonly Dictionary<IntPtr, IntPtr> VisualEffectViews = [];
    private static readonly object VisualEffectViewsLock = new();

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

    public static bool IsCurrentMouseDoubleClick() =>
        OperatingSystem.IsMacOS() && MacExplorerCurrentLeftMouseClickCount() == 2;

    public static void SetVibrancy(TopLevel topLevel, bool enabled, double alpha)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var nsView = GetNSView(topLevel);
        if (nsView == IntPtr.Zero)
            return;

        try
        {
            MakeTransparent(topLevel);

            var nsWindow = SendIntPtr(nsView, "window");
            var contentView = nsWindow == IntPtr.Zero ? IntPtr.Zero : SendIntPtr(nsWindow, "contentView");
            if (contentView == IntPtr.Zero)
                return;

            var effectView = GetOrCreateVisualEffectView(nsView, contentView);
            if (effectView != IntPtr.Zero)
            {
                SendDouble(effectView, "setAlphaValue:", Math.Clamp(alpha, 0, 1));
                SendBool(effectView, "setHidden:", !enabled);
            }
        }
        catch (DllNotFoundException)
        {
            // Native vibrancy is optional; the managed window remains usable without it.
        }
        catch (EntryPointNotFoundException)
        {
            // Same fallback as above on restricted or incompatible runtimes.
        }
    }

    public static void RemoveVibrancy(TopLevel topLevel)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var nsView = GetNSView(topLevel);
        if (nsView == IntPtr.Zero)
            return;

        IntPtr effectView;
        lock (VisualEffectViewsLock)
        {
            if (!VisualEffectViews.Remove(nsView, out effectView))
                return;
        }

        try
        {
            SendVoid(effectView, "removeFromSuperview");
        }
        catch (DllNotFoundException)
        {
            // The window is already closing; there is no managed fallback work to do.
        }
        catch (EntryPointNotFoundException)
        {
            // Same as above.
        }
    }

    private static IntPtr GetOrCreateVisualEffectView(IntPtr nsView, IntPtr contentView)
    {
        lock (VisualEffectViewsLock)
        {
            if (VisualEffectViews.TryGetValue(nsView, out var existing))
                return existing;

            var visualEffectClass = GetClass("NSVisualEffectView");
            if (visualEffectClass == IntPtr.Zero)
                return IntPtr.Zero;

            var effectView = SendIntPtr(SendIntPtr(visualEffectClass, "alloc"), "init");
            if (effectView == IntPtr.Zero)
                return IntPtr.Zero;

            SendInteger(effectView, "setMaterial:", NsVisualEffectMaterialHudWindow);
            SendInteger(effectView, "setBlendingMode:", NsVisualEffectBlendingModeBehindWindow);
            SendInteger(effectView, "setState:", 0);
            SendRect(effectView, "setFrame:", GetRect(contentView, "bounds"));
            SendUnsignedInteger(effectView, "setAutoresizingMask:", NsViewWidthSizable | NsViewHeightSizable);
            SendSubviewBelow(contentView, effectView);
            SendVoid(effectView, "release");
            VisualEffectViews[nsView] = effectView;
            return effectView;
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

    private static void SendInteger(IntPtr receiver, string selector, nint value)
    {
        if (receiver != IntPtr.Zero)
            objc_msgSend_nint(receiver, GetSelector(selector), value);
    }

    private static void SendUnsignedInteger(IntPtr receiver, string selector, nuint value)
    {
        if (receiver != IntPtr.Zero)
            objc_msgSend_nuint(receiver, GetSelector(selector), value);
    }

    private static void SendDouble(IntPtr receiver, string selector, double value)
    {
        if (receiver != IntPtr.Zero)
            objc_msgSend_double(receiver, GetSelector(selector), value);
    }

    private static NSRect GetRect(IntPtr receiver, string selector)
        => receiver == IntPtr.Zero ? default : objc_msgSend_getRect(receiver, GetSelector(selector));

    private static void SendRect(IntPtr receiver, string selector, NSRect value)
    {
        if (receiver != IntPtr.Zero)
            objc_msgSend_setRect(receiver, GetSelector(selector), value);
    }

    private static void SendSubviewBelow(IntPtr receiver, IntPtr subview)
    {
        if (receiver != IntPtr.Zero && subview != IntPtr.Zero)
            objc_msgSend_addSubviewBelow(receiver, GetSelector("addSubview:positioned:relativeTo:"), subview, -1, IntPtr.Zero);
    }

    [DllImport(LibObjC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(NativeDragLibrary)]
    private static extern int MacExplorerCurrentLeftMouseClickCount();

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

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_nint(IntPtr receiver, IntPtr selector, nint value);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_nuint(IntPtr receiver, IntPtr selector, nuint value);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_double(IntPtr receiver, IntPtr selector, double value);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern NSRect objc_msgSend_getRect(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_setRect(IntPtr receiver, IntPtr selector, NSRect value);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_addSubviewBelow(
        IntPtr receiver,
        IntPtr selector,
        IntPtr subview,
        nint positioned,
        IntPtr relativeTo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

}
