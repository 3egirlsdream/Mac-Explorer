using System.Runtime.InteropServices;
using Foundation;
using ObjCRuntime;
using FKFinder.Services;

namespace FKFinder.Platforms.MacCatalyst.Handlers;

/// <summary>
/// Manages the macOS Dock right-click menu for FKFinder.
/// All methods (applicationDockMenu:, openNewWindow:, navigateToFolder:) are injected
/// directly onto the NSApplication delegate's class to ensure Dock menu action dispatch works.
/// </summary>
public static class DockMenuHelper
{
    // ── ObjC runtime ──
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "class_addMethod")]
    private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "class_replaceMethod")]
    private static extern IntPtr class_replaceMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "object_getClass")]
    private static extern IntPtr object_getClass(IntPtr obj);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "class_getName")]
    private static extern IntPtr class_getName(IntPtr cls);

    [DllImport("/usr/lib/libdl.dylib")]
    private static extern IntPtr dlopen(string path, int mode);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_initMenuItem(
        IntPtr receiver, IntPtr selector,
        IntPtr title, IntPtr action, IntPtr keyEquivalent);

    // Delegate types for injected methods
    private delegate IntPtr DockMenuFn(IntPtr self, IntPtr sel, IntPtr app);
    private delegate void ActionFn(IntPtr self, IntPtr sel, IntPtr sender);

    // Must be kept alive to prevent GC
    private static DockMenuFn? _dockMenuFn;
    private static ActionFn? _openNewWindowFn;
    private static ActionFn? _navigateToFolderFn;

    private static IFrequentFolderService? _frequentFolderService;
    private static Action? _openNewWindowAction;
    private static NavigationBridge? _navigationBridge;
    private static IntPtr _delegateHandle;

    public static void Register(IFrequentFolderService frequentFolderService, NavigationBridge navigationBridge, Action openNewWindowAction)
    {
        _frequentFolderService = frequentFolderService;
        _openNewWindowAction = openNewWindowAction;
        _navigationBridge = navigationBridge;

        try
        {
            // Mac Catalyst: load AppKit so NSApplication/NSMenu/NSMenuItem are available
            dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", 1);

            var nsAppClass = objc_getClass("NSApplication");
            if (nsAppClass == IntPtr.Zero) { Log("NSApplication class NOT found"); return; }

            var nsApp = objc_msgSend(nsAppClass, Selector.GetHandle("sharedApplication"));
            if (nsApp == IntPtr.Zero) { Log("sharedApplication is null"); return; }

            var currentDelegate = objc_msgSend(nsApp, Selector.GetHandle("delegate"));
            if (currentDelegate == IntPtr.Zero) { Log("delegate is null"); return; }

            _delegateHandle = currentDelegate;
            var delegateClass = object_getClass(currentDelegate);

            var classNamePtr = class_getName(delegateClass);
            Log($"delegate class = {Marshal.PtrToStringAnsi(classNamePtr)}");

            // 1) Inject applicationDockMenu:
            _dockMenuFn = OnApplicationDockMenu;
            class_replaceMethod(delegateClass,
                Selector.GetHandle("applicationDockMenu:"),
                Marshal.GetFunctionPointerForDelegate(_dockMenuFn),
                "@@:@");

            // 2) Inject openNewWindow: action handler
            _openNewWindowFn = OnOpenNewWindow;
            class_addMethod(delegateClass,
                Selector.GetHandle("openNewWindow:"),
                Marshal.GetFunctionPointerForDelegate(_openNewWindowFn),
                "v@:@");

            // 3) Inject navigateToFolder: action handler
            _navigateToFolderFn = OnNavigateToFolder;
            class_addMethod(delegateClass,
                Selector.GetHandle("navigateToFolder:"),
                Marshal.GetFunctionPointerForDelegate(_navigateToFolderFn),
                "v@:@");

            Log("Dock menu registered successfully");
        }
        catch (Exception ex)
        {
            Log($"Failed to register - {ex.Message}\n{ex.StackTrace}");
        }
    }

    // ── Injected ObjC methods (called as C function pointers) ──

    private static IntPtr OnApplicationDockMenu(IntPtr self, IntPtr sel, IntPtr app)
    {
        try { return BuildDockMenu(); }
        catch (Exception ex) { Log($"Error building menu - {ex.Message}"); return IntPtr.Zero; }
    }

    private static void OnOpenNewWindow(IntPtr self, IntPtr sel, IntPtr sender)
    {
        Console.WriteLine("[FKFinder] DockMenu: openNewWindow: action triggered!");
        Log("openNewWindow: action triggered");
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Console.WriteLine("[FKFinder] DockMenu: executing openNewWindow on main thread");
            SetPendingQuickAccessFocus();
            TriggerOpenNewWindow();
        });
    }

    private static void OnNavigateToFolder(IntPtr self, IntPtr sel, IntPtr sender)
    {
        Console.WriteLine("[FKFinder] DockMenu: navigateToFolder: action triggered!");
        Log("navigateToFolder: action triggered");
        try
        {
            var repObj = objc_msgSend(sender, Selector.GetHandle("representedObject"));
            if (repObj != IntPtr.Zero)
            {
                var path = NSString.FromHandle(repObj)?.ToString();
                Console.WriteLine($"[FKFinder] DockMenu: navigateToFolder path = {path}");
                if (!string.IsNullOrEmpty(path))
                {
                    MainThread.BeginInvokeOnMainThread(() => TriggerNavigateInNewWindow(path));
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[FKFinder] DockMenu: navigateToFolder error - {ex.Message}"); }
    }

    // ── Menu building ──

    private static IntPtr BuildDockMenu()
    {
        var nsMenuClass = objc_getClass("NSMenu");
        if (nsMenuClass == IntPtr.Zero) return IntPtr.Zero;

        var menu = objc_msgSend(nsMenuClass, Selector.GetHandle("alloc"));
        menu = objc_msgSend(menu, Selector.GetHandle("init"));

        // 1. "快速访达" — target is the NSApp delegate (where we injected methods)
        var quickItem = CreateMenuItem("快速访达", "openNewWindow:");
        if (quickItem != IntPtr.Zero)
        {
            objc_msgSend_void_IntPtr(quickItem, Selector.GetHandle("setTarget:"), _delegateHandle);
            AddItemToMenu(menu, quickItem);
        }

        // 2. Separator
        AddSeparatorToMenu(menu);

        // 3. Frequent folders (max 6)
        var folders = _frequentFolderService?.GetTopFoldersAsync(6).GetAwaiter().GetResult();
        if (folders != null && folders.Count > 0)
        {
            foreach (var folder in folders)
            {
                var item = CreateMenuItem(folder.Name, "navigateToFolder:");
                if (item != IntPtr.Zero)
                {
                    objc_msgSend_void_IntPtr(item, Selector.GetHandle("setTarget:"), _delegateHandle);
                    var nsPath = new NSString(folder.Path);
                    objc_msgSend_void_IntPtr(item, Selector.GetHandle("setRepresentedObject:"), nsPath.Handle);
                    AddItemToMenu(menu, item);
                }
            }
        }

        return menu;
    }

    private static IntPtr CreateMenuItem(string title, string action)
    {
        var nsMenuItemClass = objc_getClass("NSMenuItem");
        if (nsMenuItemClass == IntPtr.Zero) return IntPtr.Zero;

        var nsTitle = new NSString(title);
        var keyEquiv = new NSString("");

        return objc_msgSend_initMenuItem(
            objc_msgSend(nsMenuItemClass, Selector.GetHandle("alloc")),
            Selector.GetHandle("initWithTitle:action:keyEquivalent:"),
            nsTitle.Handle,
            Selector.GetHandle(action),
            keyEquiv.Handle);
    }

    private static void AddItemToMenu(IntPtr menu, IntPtr item)
        => objc_msgSend_void_IntPtr(menu, Selector.GetHandle("addItem:"), item);

    private static void AddSeparatorToMenu(IntPtr menu)
    {
        var cls = objc_getClass("NSMenuItem");
        if (cls == IntPtr.Zero) return;
        AddItemToMenu(menu, objc_msgSend(cls, Selector.GetHandle("separatorItem")));
    }

    // ── Public triggers ──

    public static void TriggerOpenNewWindow() => _openNewWindowAction?.Invoke();

    public static void SetPendingQuickAccessFocus()
    {
        if (_navigationBridge != null)
            _navigationBridge.PendingQuickAccessFocus = true;
    }

    public static void TriggerNavigateInNewWindow(string path)
    {
        if (_navigationBridge != null)
            _navigationBridge.PendingNavigationPath = path;
        _openNewWindowAction?.Invoke();
    }

    private static void Log(string msg)
    {
        Console.WriteLine($"[FKFinder] DockMenuHelper: {msg}");
        System.Diagnostics.Debug.WriteLine($"DockMenuHelper: {msg}");
    }
}
