using System.Runtime.InteropServices;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public static class MacApplicationIconService
{
    public static void Apply()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var iconPath = FindIconPath();
        if (iconPath == null)
            return;

        try
        {
            dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", 1);

            var appClass = objc_getClass("NSApplication");
            var imageClass = objc_getClass("NSImage");
            if (appClass == IntPtr.Zero || imageClass == IntPtr.Zero)
                return;

            var app = objc_msgSend(appClass, sel_registerName("sharedApplication"));
            var image = objc_msgSend_initWithContentsOfFile(
                objc_msgSend(imageClass, sel_registerName("alloc")),
                sel_registerName("initWithContentsOfFile:"),
                CreateString(iconPath));
            if (app == IntPtr.Zero || image == IntPtr.Zero)
                return;

            objc_msgSend_void(app, sel_registerName("setApplicationIconImage:"), image);
        }
        catch
        {
        }
    }

    private static string? FindIconPath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "appicon.icns"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "Resources", "appicon.icns")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "Assets", "appicon.icns")),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "appicon.icns"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static IntPtr CreateString(string value)
    {
        var stringClass = objc_getClass("NSString");
        return objc_msgSend_string(stringClass, sel_registerName("stringWithUTF8String:"), value);
    }

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void(IntPtr receiver, IntPtr selector, IntPtr value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_string(
        IntPtr receiver,
        IntPtr selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_initWithContentsOfFile(
        IntPtr receiver,
        IntPtr selector,
        IntPtr path);

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern IntPtr dlopen(string path, int mode);
}
