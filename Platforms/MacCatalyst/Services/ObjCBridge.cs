using System.Runtime.InteropServices;

namespace MacExplorer.Platforms.MacCatalyst.Services;

/// <summary>
/// Minimal P/Invoke bridge for Objective-C runtime calls, replacing Xamarin's ObjCRuntime bindings.
/// </summary>
public static class ObjCBridge
{
    private const string LibObjC = "/usr/lib/libobjc.dylib";

    [DllImport(LibObjC, EntryPoint = "objc_getClass")]
    public static extern IntPtr GetClass(string name);

    [DllImport(LibObjC, EntryPoint = "sel_registerName")]
    public static extern IntPtr RegisterSelector(string name);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendMessage(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendMessageVoid(IntPtr receiver, IntPtr selector, bool arg);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendMessageIntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendMessageVoidIntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
}
