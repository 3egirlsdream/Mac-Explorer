using System.Runtime.InteropServices;
using Foundation;
using ObjCRuntime;

namespace MacExplorer.Platforms.MacCatalyst.Handlers;

/// <summary>
/// Sets the NSWindow title for Mac Catalyst windows via ObjC runtime.
/// macOS automatically shows window titles in the Dock right-click menu.
/// </summary>
public static class WindowTitleHelper
{
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    /// <summary>
    /// Returns the current key NSWindow handle (the frontmost active window).
    /// </summary>
    public static IntPtr GetKeyNSWindow()
    {
        try
        {
            var nsAppClass = Class.GetHandle("NSApplication");
            if (nsAppClass == IntPtr.Zero) return IntPtr.Zero;

            var sharedApp = objc_msgSend(nsAppClass, Selector.GetHandle("sharedApplication"));
            if (sharedApp == IntPtr.Zero) return IntPtr.Zero;

            return objc_msgSend(sharedApp, Selector.GetHandle("keyWindow"));
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Sets the title of an NSWindow.
    /// </summary>
    public static void SetTitle(IntPtr nsWindow, string title)
    {
        if (nsWindow == IntPtr.Zero) return;
        var nsTitle = new NSString(title);
        objc_msgSend_void_IntPtr(nsWindow, Selector.GetHandle("setTitle:"), nsTitle.Handle);
    }
}
