using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using MacExplorer;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public sealed class MacDockMenuService
{
    private readonly IFrequentFolderService _frequentFolderService;
    private IReadOnlyList<(string Name, string Path)> _frequentFolders = [];
    private IntPtr _delegateHandle;

    private static MacDockMenuService? _instance;

    public MacDockMenuService(IFrequentFolderService frequentFolderService)
    {
        _frequentFolderService = frequentFolderService;
    }

    public void Register()
    {
        if (!OperatingSystem.IsMacOS() || _instance != null) return;
        _instance = this;
        _ = RefreshFrequentFoldersAsync();

        dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", 1);
        var appClass = objc_getClass("NSApplication");
        if (appClass == IntPtr.Zero) return;

        var app = Send(appClass, "sharedApplication");
        _delegateHandle = Send(app, "delegate");
        if (_delegateHandle == IntPtr.Zero) return;

        var delegateClass = object_getClass(_delegateHandle);
        unsafe
        {
            class_replaceMethod(
                delegateClass,
                sel_registerName("applicationDockMenu:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr>)&OnApplicationDockMenu,
                "@@:@");
            class_addMethod(
                delegateClass,
                sel_registerName("macExplorerOpenWindow:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnOpenWindow,
                "v@:@");
            class_addMethod(
                delegateClass,
                sel_registerName("macExplorerOpenFolder:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnOpenFolder,
                "v@:@");
            class_replaceMethod(
                delegateClass,
                sel_registerName("applicationShouldHandleReopen:hasVisibleWindows:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte, byte>)&OnShouldHandleReopen,
                "B@:@B");
        }
    }

    private async Task RefreshFrequentFoldersAsync()
    {
        try
        {
            var folders = await _frequentFolderService.GetTopFoldersAsync(6);
            _frequentFolders = folders
                .Where(folder => Directory.Exists(folder.Path))
                .Select(folder => (folder.Name, folder.Path))
                .ToArray();
        }
        catch
        {
            _frequentFolders = [];
        }
    }

    [UnmanagedCallersOnly]
    private static IntPtr OnApplicationDockMenu(IntPtr self, IntPtr selector, IntPtr application)
    {
        try
        {
            return _instance?.BuildMenu() ?? IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly]
    private static byte OnShouldHandleReopen(IntPtr self, IntPtr selector, IntPtr application, byte hasVisibleWindows)
    {
        try
        {
            if (hasVisibleWindows == 0)
                Dispatcher.UIThread.Post(() => App.OpenNewWindow());
        }
        catch
        {
        }
        return 1;
    }

    [UnmanagedCallersOnly]
    private static void OnOpenWindow(IntPtr self, IntPtr selector, IntPtr sender)
    {
        Dispatcher.UIThread.Post(() => App.OpenNewWindow());
    }

    [UnmanagedCallersOnly]
    private static void OnOpenFolder(IntPtr self, IntPtr selector, IntPtr sender)
    {
        try
        {
            var representedObject = Send(sender, "representedObject");
            var utf8 = Send(representedObject, "UTF8String");
            var path = utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                Dispatcher.UIThread.Post(() => App.OpenNewWindow(path));
        }
        catch
        {
        }
    }

    private IntPtr BuildMenu()
    {
        _ = RefreshFrequentFoldersAsync();
        var menuClass = objc_getClass("NSMenu");
        var menu = Send(Send(menuClass, "alloc"), "init");
        if (menu == IntPtr.Zero) return IntPtr.Zero;

        AddMenuItem(menu, "快速访达", "macExplorerOpenWindow:", null);
        AddSeparator(menu);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddMenuItem(menu, "文稿", "macExplorerOpenFolder:", Path.Combine(home, "Documents"));
        AddMenuItem(menu, "下载", "macExplorerOpenFolder:", Path.Combine(home, "Downloads"));
        AddMenuItem(menu, "应用程序", "macExplorerOpenFolder:", "/Applications");

        if (_frequentFolders.Count > 0)
        {
            AddSeparator(menu);
            foreach (var folder in _frequentFolders)
                AddMenuItem(menu, folder.Name, "macExplorerOpenFolder:", folder.Path);
        }

        return menu;
    }

    private void AddMenuItem(IntPtr menu, string title, string action, string? path)
    {
        var itemClass = objc_getClass("NSMenuItem");
        var item = objc_msgSend_initMenuItem(
            Send(itemClass, "alloc"),
            sel_registerName("initWithTitle:action:keyEquivalent:"),
            CreateString(title),
            sel_registerName(action),
            CreateString(string.Empty));
        if (item == IntPtr.Zero) return;

        SendVoid(item, "setTarget:", _delegateHandle);
        if (path != null)
            SendVoid(item, "setRepresentedObject:", CreateString(path));
        SendVoid(menu, "addItem:", item);
    }

    private static void AddSeparator(IntPtr menu)
    {
        var itemClass = objc_getClass("NSMenuItem");
        SendVoid(menu, "addItem:", Send(itemClass, "separatorItem"));
    }

    private static IntPtr CreateString(string value)
    {
        var stringClass = objc_getClass("NSString");
        return objc_msgSend_string(stringClass, sel_registerName("stringWithUTF8String:"), value);
    }

    private static IntPtr Send(IntPtr receiver, string selector)
        => receiver == IntPtr.Zero ? IntPtr.Zero : objc_msgSend(receiver, sel_registerName(selector));

    private static void SendVoid(IntPtr receiver, string selector, IntPtr value)
    {
        if (receiver != IntPtr.Zero)
            objc_msgSend_void(receiver, sel_registerName(selector), value);
    }

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr object_getClass(IntPtr value);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr class_replaceMethod(IntPtr cls, IntPtr name, IntPtr implementation, string types);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern bool class_addMethod(IntPtr cls, IntPtr name, IntPtr implementation, string types);

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
    private static extern IntPtr objc_msgSend_initMenuItem(
        IntPtr receiver,
        IntPtr selector,
        IntPtr title,
        IntPtr action,
        IntPtr keyEquivalent);

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern IntPtr dlopen(string path, int mode);
}
