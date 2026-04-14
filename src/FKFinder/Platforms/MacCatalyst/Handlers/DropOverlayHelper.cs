using System.Runtime.InteropServices;
using CoreFoundation;
using CoreGraphics;
using FKFinder.Services;
using Foundation;
using ObjCRuntime;
using WebKit;

namespace FKFinder.Platforms.MacCatalyst.Handlers;

/// <summary>
/// Adds a transparent NSView overlay on top of each NSWindow's contentView,
/// registered as an NSDraggingDestination. This intercepts drag events at the
/// AppKit layer before the Mac Catalyst bridge's UINSView can swallow them,
/// working around WebKit sandbox extension failures that prevent HTML5 drop
/// events from firing for external/cross-window drags.
/// </summary>
public static class DropOverlayHelper
{
    // ── ObjC runtime P/Invoke ──

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_bool(IntPtr receiver, IntPtr selector, bool arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_nuint(IntPtr receiver, IntPtr selector, nuint arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern CGRect objc_msgSend_ret_CGRect(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_CGRect(IntPtr receiver, IntPtr selector, CGRect rect);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern CGPoint objc_msgSend_ret_CGPoint(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_count(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_objectAtIndex(IntPtr receiver, IntPtr selector, nuint index);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, int extraBytes);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern void objc_registerClassPair(IntPtr cls);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "class_addMethod")]
    private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "object_getClass")]
    private static extern IntPtr object_getClass(IntPtr obj);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "class_getName")]
    private static extern IntPtr class_getName(IntPtr cls);

    [DllImport("/usr/lib/libdl.dylib")]
    private static extern IntPtr dlopen(string path, int mode);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_addSubview_positioned(
        IntPtr receiver, IntPtr selector, IntPtr view, nint place, IntPtr relativeTo);

    // ── Delegate types for NSDraggingDestination callbacks ──
    // Must be stored as static fields to prevent GC collection.

    // draggingEntered: / draggingUpdated: → returns NSDragOperation (UInt64 on arm64)
    private delegate nuint DragOperationFn(IntPtr self, IntPtr sel, IntPtr draggingInfo);
    // draggingExited: → void
    private delegate void DragExitedFn(IntPtr self, IntPtr sel, IntPtr draggingInfo);
    // prepareForDragOperation: → BOOL
    private delegate byte PrepareDragFn(IntPtr self, IntPtr sel, IntPtr draggingInfo);
    // performDragOperation: → BOOL
    private delegate byte PerformDragFn(IntPtr self, IntPtr sel, IntPtr draggingInfo);

    // Static delegate instances (prevent GC)
    private static DragOperationFn? _draggingEnteredFn;
    private static DragOperationFn? _draggingUpdatedFn;
    private static DragExitedFn? _draggingExitedFn;
    private static PrepareDragFn? _prepareDragFn;
    private static PerformDragFn? _performDragFn;

    // ── State ──
    private static IntPtr _overlayClass;
    private static bool _registered;
    private static NSObject? _notificationObserver;
    private static IDragDropBridge? _bridge;

    /// <summary>NSWindow handle → overlay NSView handle.</summary>
    private static readonly Dictionary<IntPtr, IntPtr> _windowOverlays = new();

    /// <summary>NSWindow handle → WKWebView (weak).</summary>
    private static readonly Dictionary<IntPtr, WeakReference<WKWebView>> _windowToWebView = new();

    private const nuint NSDragOperationGeneric = 4;
    private const nuint NSDragOperationNone = 0;

    private static readonly string LogPath = "/tmp/fkfinder-drag.log";

    /// <summary>
    /// Fired when a new NSWindow is created and the overlay is installed.
    /// Parameters: NSWindow handle
    /// </summary>
    public static event Action<IntPtr>? WindowCreated;

    // ── Public API ──

    /// <summary>
    /// Register the overlay helper. Call once at app startup.
    /// Creates the custom ObjC class and listens for new window notifications.
    /// </summary>
    public static void Register(IDragDropBridge bridge)
    {
        _bridge = bridge;

        if (_registered) return;
        _registered = true;

        try
        {
            dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", 1);

            CreateOverlayClass();

            // Listen for new NSWindow creation (same notification as VibrancyHelper)
            _notificationObserver = NSNotificationCenter.DefaultCenter.AddObserver(
                new NSString("UISBHSDidCreateWindowForSceneNotification"),
                OnWindowCreatedForScene);

            Log("Registered for window notifications");
        }
        catch (Exception ex)
        {
            Log($"Register failed: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// Register a WKWebView for a given NSWindow so we can call JS to determine
    /// the drop target folder from mouse coordinates.
    /// </summary>
    public static void RegisterWebView(IntPtr nsWindow, WKWebView webView)
    {
        if (nsWindow == IntPtr.Zero) return;
        _windowToWebView[nsWindow] = new WeakReference<WKWebView>(webView);
        Log($"Registered WKWebView for window {nsWindow}");
    }

    /// <summary>
    /// Get the WKWebView associated with a given NSWindow.
    /// Returns null if no webView is registered for this window.
    /// </summary>
    public static WKWebView? GetWebViewForWindow(IntPtr nsWindow)
    {
        if (nsWindow == IntPtr.Zero) return null;
        if (_windowToWebView.TryGetValue(nsWindow, out var weakRef))
        {
            if (weakRef.TryGetTarget(out var webView))
                return webView;
        }
        return null;
    }

    /// <summary>
    /// Set the drag-drop bridge after DI services become available.
    /// Call from App.OnWindowActivated once services are ready.
    /// </summary>
    public static void SetBridge(IDragDropBridge bridge)
    {
        _bridge = bridge;
    }

    /// <summary>Show or hide the overlay for a specific NSWindow.</summary>
    public static void SetOverlayHidden(IntPtr nsWindow, bool hidden)
    {
        if (_windowOverlays.TryGetValue(nsWindow, out var overlay))
            objc_msgSend_void_bool(overlay, Selector.GetHandle("setHidden:"), hidden);
    }

    /// <summary>Show or hide the overlay for all tracked windows.</summary>
    public static void SetOverlayHiddenForAllWindows(bool hidden)
    {
        foreach (var kv in _windowOverlays)
            objc_msgSend_void_bool(kv.Value, Selector.GetHandle("setHidden:"), hidden);
    }

    // ── ObjC class creation ──

    private static void CreateOverlayClass()
    {
        var nsViewClass = objc_getClass("NSView");
        if (nsViewClass == IntPtr.Zero)
        {
            Log("NSView class not found!");
            return;
        }

        _overlayClass = objc_allocateClassPair(nsViewClass, "FKDropOverlayView", 0);
        if (_overlayClass == IntPtr.Zero)
        {
            // Class may already exist from a previous run
            _overlayClass = objc_getClass("FKDropOverlayView");
            if (_overlayClass == IntPtr.Zero)
            {
                Log("Failed to create FKDropOverlayView class");
                return;
            }
            Log("FKDropOverlayView class already exists, reusing");
            return;
        }

        // Add NSDraggingDestination protocol methods

        _draggingEnteredFn = OnDraggingEntered;
        class_addMethod(_overlayClass,
            Selector.GetHandle("draggingEntered:"),
            Marshal.GetFunctionPointerForDelegate(_draggingEnteredFn),
            "Q@:@");

        _draggingUpdatedFn = OnDraggingUpdated;
        class_addMethod(_overlayClass,
            Selector.GetHandle("draggingUpdated:"),
            Marshal.GetFunctionPointerForDelegate(_draggingUpdatedFn),
            "Q@:@");

        _draggingExitedFn = OnDraggingExited;
        class_addMethod(_overlayClass,
            Selector.GetHandle("draggingExited:"),
            Marshal.GetFunctionPointerForDelegate(_draggingExitedFn),
            "v@:@");

        _prepareDragFn = OnPrepareForDragOperation;
        class_addMethod(_overlayClass,
            Selector.GetHandle("prepareForDragOperation:"),
            Marshal.GetFunctionPointerForDelegate(_prepareDragFn),
            "B@:@");

        _performDragFn = OnPerformDragOperation;
        class_addMethod(_overlayClass,
            Selector.GetHandle("performDragOperation:"),
            Marshal.GetFunctionPointerForDelegate(_performDragFn),
            "B@:@");

        objc_registerClassPair(_overlayClass);
        Log("FKDropOverlayView class created and registered");
    }

    // ── NSDraggingDestination callbacks ──

    private static nuint OnDraggingEntered(IntPtr self, IntPtr sel, IntPtr draggingInfo)
    {
        Log("draggingEntered");
        return NSDragOperationGeneric;
    }

    private static nuint OnDraggingUpdated(IntPtr self, IntPtr sel, IntPtr draggingInfo)
    {
        return NSDragOperationGeneric;
    }

    private static void OnDraggingExited(IntPtr self, IntPtr sel, IntPtr draggingInfo)
    {
        Log("draggingExited");
    }

    private static byte OnPrepareForDragOperation(IntPtr self, IntPtr sel, IntPtr draggingInfo)
    {
        Log("prepareForDragOperation");
        return 1; // YES
    }

    private static byte OnPerformDragOperation(IntPtr self, IntPtr sel, IntPtr draggingInfo)
    {
        Log("performDragOperation: extracting files...");
        try
        {
            // Get dragging location for target folder resolution
            var location = objc_msgSend_ret_CGPoint(draggingInfo, Selector.GetHandle("draggingLocation"));
            Log($"  draggingLocation: ({location.X}, {location.Y})");

            // Find the NSWindow that owns this overlay
            var overlayWindow = objc_msgSend(self, Selector.GetHandle("window"));

            // Extract file paths from pasteboard
            var paths = ExtractFilePathsFromPasteboard(draggingInfo);

            if (paths.Count > 0)
            {
                Log($"  Extracted {paths.Count} file path(s)");

                // Try to resolve target directory via JS, then notify bridge
                ResolveTargetAndNotify(overlayWindow, self, location, paths);
            }
            else
            {
                Log("  No file paths extracted from pasteboard");
            }
        }
        catch (Exception ex)
        {
            Log($"performDragOperation error: {ex.Message}\n{ex.StackTrace}");
        }

        return 1; // YES — always accept the drop, async work continues in background
    }

    // ── Pasteboard file path extraction ──

    private static List<string> ExtractFilePathsFromPasteboard(IntPtr draggingInfo)
    {
        var paths = new List<string>();

        try
        {
            var pasteboard = objc_msgSend(draggingInfo, Selector.GetHandle("draggingPasteboard"));
            if (pasteboard == IntPtr.Zero)
            {
                Log("  draggingPasteboard is null");
                return paths;
            }

            // Strategy A: NSFilenamesPboardType → propertyListForType: → NSArray of NSString
            paths = TryExtractViaFilenamesPboardType(pasteboard);
            if (paths.Count > 0)
            {
                Log($"  Strategy A (NSFilenamesPboardType): {paths.Count} path(s)");
                return paths;
            }

            // Strategy B: public.file-url → stringForType: → file:// URL string
            paths = TryExtractViaPublicFileUrl(pasteboard);
            if (paths.Count > 0)
            {
                Log($"  Strategy B (public.file-url): {paths.Count} path(s)");
                return paths;
            }

            // Strategy C: enumerate all types, look for anything with file://
            paths = TryExtractViaAllTypes(pasteboard);
            if (paths.Count > 0)
            {
                Log($"  Strategy C (all types scan): {paths.Count} path(s)");
                return paths;
            }

            Log("  All extraction strategies failed");
        }
        catch (Exception ex)
        {
            Log($"  ExtractFilePathsFromPasteboard error: {ex.Message}");
        }

        return paths;
    }

    private static List<string> TryExtractViaFilenamesPboardType(IntPtr pasteboard)
    {
        var paths = new List<string>();
        try
        {
            var typeStr = CreateNSString("NSFilenamesPboardType");
            if (typeStr == IntPtr.Zero) return paths;

            var plist = objc_msgSend_IntPtr(pasteboard,
                Selector.GetHandle("propertyListForType:"), typeStr);

            if (plist == IntPtr.Zero) return paths;

            // plist should be an NSArray of NSString
            var count = (int)objc_msgSend_count(plist, Selector.GetHandle("count"));
            for (int i = 0; i < count; i++)
            {
                var item = objc_msgSend_objectAtIndex(plist,
                    Selector.GetHandle("objectAtIndex:"), (nuint)i);
                if (item == IntPtr.Zero) continue;

                var nsStr = Runtime.GetNSObject<NSString>(item);
                var path = nsStr?.ToString();
                if (!string.IsNullOrEmpty(path))
                    paths.Add(path);
            }
        }
        catch (Exception ex)
        {
            Log($"  Strategy A error: {ex.Message}");
        }

        return paths;
    }

    private static List<string> TryExtractViaPublicFileUrl(IntPtr pasteboard)
    {
        var paths = new List<string>();
        try
        {
            var typeStr = CreateNSString("public.file-url");
            if (typeStr == IntPtr.Zero) return paths;

            // Try reading all strings for this type
            // First check if this type is available
            var types = objc_msgSend(pasteboard, Selector.GetHandle("types"));
            if (types == IntPtr.Zero) return paths;

            var typesCount = (int)objc_msgSend_count(types, Selector.GetHandle("count"));
            bool hasFileUrl = false;
            for (int i = 0; i < typesCount; i++)
            {
                var t = objc_msgSend_objectAtIndex(types,
                    Selector.GetHandle("objectAtIndex:"), (nuint)i);
                var tStr = Runtime.GetNSObject<NSString>(t)?.ToString();
                if (tStr == "public.file-url")
                {
                    hasFileUrl = true;
                    break;
                }
            }

            if (!hasFileUrl) return paths;

            var urlString = objc_msgSend_IntPtr(pasteboard,
                Selector.GetHandle("stringForType:"), typeStr);

            if (urlString != IntPtr.Zero)
            {
                var str = Runtime.GetNSObject<NSString>(urlString)?.ToString();
                if (!string.IsNullOrEmpty(str))
                {
                    var path = FileUrlToPath(str);
                    if (!string.IsNullOrEmpty(path))
                        paths.Add(path);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"  Strategy B error: {ex.Message}");
        }

        return paths;
    }

    private static List<string> TryExtractViaAllTypes(IntPtr pasteboard)
    {
        var paths = new List<string>();
        try
        {
            var types = objc_msgSend(pasteboard, Selector.GetHandle("types"));
            if (types == IntPtr.Zero) return paths;

            var count = (int)objc_msgSend_count(types, Selector.GetHandle("count"));
            Log($"  Scanning {count} pasteboard types...");

            for (int i = 0; i < count; i++)
            {
                var typeObj = objc_msgSend_objectAtIndex(types,
                    Selector.GetHandle("objectAtIndex:"), (nuint)i);
                var typeName = Runtime.GetNSObject<NSString>(typeObj)?.ToString();
                Log($"    type[{i}]: {typeName}");

                if (string.IsNullOrEmpty(typeName)) continue;

                var typeNSStr = CreateNSString(typeName);
                if (typeNSStr == IntPtr.Zero) continue;

                var data = objc_msgSend_IntPtr(pasteboard,
                    Selector.GetHandle("stringForType:"), typeNSStr);
                if (data == IntPtr.Zero) continue;

                var str = Runtime.GetNSObject<NSString>(data)?.ToString();
                if (!string.IsNullOrEmpty(str) && str.Contains("file://"))
                {
                    var path = FileUrlToPath(str);
                    if (!string.IsNullOrEmpty(path))
                    {
                        paths.Add(path);
                        Log($"    Found file URL in type '{typeName}': {path}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"  Strategy C error: {ex.Message}");
        }

        return paths;
    }

    // ── Target directory resolution + bridge notification ──

    private static void ResolveTargetAndNotify(
        IntPtr nsWindow, IntPtr overlay, CGPoint location, List<string> paths)
    {
        // Try to get target directory via JS (async)
        WKWebView? webView = null;
        if (nsWindow != IntPtr.Zero &&
            _windowToWebView.TryGetValue(nsWindow, out var weakRef))
        {
            weakRef.TryGetTarget(out webView);
        }

        if (webView != null)
        {
            // Convert AppKit coordinates to JS coordinates (Y-axis flip)
            var bounds = objc_msgSend_ret_CGRect(overlay, Selector.GetHandle("bounds"));
            var jsX = location.X;
            var jsY = bounds.Height - location.Y;

            var wv = webView;
            var filePaths = paths.ToArray();
            var targetWindowId = nsWindow.ToString();

            // Run JS evaluation on main thread
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                string? targetDir = null;
                try
                {
                    var result = await wv.EvaluateJavaScriptAsync(
                        $"typeof fkfinderNativeDrag !== 'undefined' ? fkfinderNativeDrag.getDropTargetAtPoint({jsX},{jsY}) : null");

                    var jsResult = result?.ToString();
                    // WKWebView returns "<null>" for JavaScript null, or "null" string
                    if (!string.IsNullOrEmpty(jsResult) 
                        && jsResult != "null" 
                        && jsResult != "<null>"
                        && jsResult != "undefined"
                        && jsResult != "(null)")
                    {
                        targetDir = jsResult;
                        Log($"  JS resolved target dir: {targetDir}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"  JS eval error: {ex.Message}");
                }

                // Fallback to current directory if JS didn't resolve a target
                // Use window-specific directory to support cross-window drag-drop
                if (string.IsNullOrEmpty(targetDir))
                {
                    targetDir = _bridge?.GetCurrentDirectoryForWindow(targetWindowId);
                    Log($"  Using current dir for window {targetWindowId}: {targetDir ?? "(null)"}");
                }

                // Ensure targetDir is never null when calling NotifyExternalDrop
                targetDir ??= "";

                Log($"  Drop: {filePaths.Length} file(s) -> '{targetDir}'");
                _bridge?.HandleExternalDrop(filePaths, targetDir, nsWindow);
            });
        }
        else
        {
            // No webView available, use bridge's current directory
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var targetDir = _bridge?.GetCurrentDirectory() ?? "";
                Log($"  Drop (no webview): {paths.Count} file(s) -> '{targetDir}'");
                _bridge?.HandleExternalDrop(paths.ToArray(), targetDir, nsWindow);
            });
        }
    }

    // ── Window notification handling ──

    private static void OnWindowCreatedForScene(NSNotification notification)
    {
        Log("Window created notification received");

        try
        {
            var nsWindow = FindNSWindowFromNotification(notification);

            if (nsWindow == IntPtr.Zero)
            {
                // Fallback: retry via NSApplication after a short delay
                DispatchQueue.MainQueue.DispatchAfter(
                    new DispatchTime(DispatchTime.Now, (long)(0.3 * 1_000_000_000)),
                    () => SetupFromNSApplication());
                return;
            }

            InstallOverlay(nsWindow);
        }
        catch (Exception ex)
        {
            Log($"Error in window notification: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static IntPtr FindNSWindowFromNotification(NSNotification notification)
    {
        try
        {
            if (notification.UserInfo != null)
            {
                var sceneId = notification.UserInfo.ValueForKey(new NSString("SceneIdentifier"));
                if (sceneId != null)
                {
                    Log($"SceneIdentifier = {sceneId}");

                    var nsAppClass = objc_getClass("NSApplication");
                    var sharedApp = objc_msgSend(nsAppClass, Selector.GetHandle("sharedApplication"));
                    if (sharedApp != IntPtr.Zero)
                    {
                        var appDelegate = objc_msgSend(sharedApp, Selector.GetHandle("delegate"));
                        if (appDelegate != IntPtr.Zero)
                        {
                            var hostWindow = objc_msgSend_IntPtr(appDelegate,
                                Selector.GetHandle("hostWindowForSceneIdentifier:"),
                                sceneId.Handle);
                            if (hostWindow != IntPtr.Zero)
                            {
                                Log($"Got NSWindow via hostWindowForSceneIdentifier: {hostWindow}");
                                return hostWindow;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"FindNSWindowFromNotification failed: {ex.Message}");
        }

        return IntPtr.Zero;
    }

    private static void SetupFromNSApplication(int retryCount = 0)
    {
        if (retryCount > 10) return;

        try
        {
            var nsAppClass = objc_getClass("NSApplication");
            var sharedApp = objc_msgSend(nsAppClass, Selector.GetHandle("sharedApplication"));
            if (sharedApp == IntPtr.Zero) return;

            var keyWindow = objc_msgSend(sharedApp, Selector.GetHandle("keyWindow"));
            if (keyWindow != IntPtr.Zero && !_windowOverlays.ContainsKey(keyWindow))
            {
                InstallOverlay(keyWindow);
                return;
            }

            var mainWindow = objc_msgSend(sharedApp, Selector.GetHandle("mainWindow"));
            if (mainWindow != IntPtr.Zero && !_windowOverlays.ContainsKey(mainWindow))
            {
                InstallOverlay(mainWindow);
                return;
            }

            Log($"No unconfigured NSWindow found, retry {retryCount}...");
            DispatchQueue.MainQueue.DispatchAfter(
                new DispatchTime(DispatchTime.Now, (long)(0.3 * 1_000_000_000)),
                () => SetupFromNSApplication(retryCount + 1));
        }
        catch (Exception ex)
        {
            Log($"SetupFromNSApplication failed: {ex.Message}");
        }
    }

    // ── Overlay installation ──

    private static void InstallOverlay(IntPtr nsWindow)
    {
        if (nsWindow == IntPtr.Zero) return;
        if (_windowOverlays.ContainsKey(nsWindow))
        {
            Log($"Overlay already installed for window {nsWindow}");
            return;
        }

        if (_overlayClass == IntPtr.Zero)
        {
            Log("Overlay class not created yet");
            return;
        }

        try
        {
            var contentView = objc_msgSend(nsWindow, Selector.GetHandle("contentView"));
            if (contentView == IntPtr.Zero)
            {
                Log("contentView is null");
                return;
            }

            // alloc + initWithFrame:contentView.bounds
            var overlay = objc_msgSend(_overlayClass, Selector.GetHandle("alloc"));
            var bounds = objc_msgSend_ret_CGRect(contentView, Selector.GetHandle("bounds"));
            overlay = objc_msgSend_CGRect(overlay, Selector.GetHandle("initWithFrame:"), bounds);

            if (overlay == IntPtr.Zero)
            {
                Log("Failed to create overlay view");
                return;
            }

            // autoresizingMask = NSViewWidthSizable | NSViewHeightSizable = 18
            objc_msgSend_void_nuint(overlay, Selector.GetHandle("setAutoresizingMask:"), 18);

            // Register for dragged types: public.file-url and NSFilenamesPboardType
            RegisterDraggedTypes(overlay);

            // Add overlay as topmost subview of contentView
            // NSWindowAbove = 1
            objc_msgSend_addSubview_positioned(contentView,
                Selector.GetHandle("addSubview:positioned:relativeTo:"),
                overlay, 1, IntPtr.Zero);

            _windowOverlays[nsWindow] = overlay;

            Log($"Overlay installed for window {nsWindow}, bounds={bounds}");
            
            // Notify subscribers that a new window has been created
            try
            {
                WindowCreated?.Invoke(nsWindow);
            }
            catch (Exception ex)
            {
                Log($"WindowCreated event handler error: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Log($"InstallOverlay failed: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static void RegisterDraggedTypes(IntPtr overlay)
    {
        try
        {
            // Create NSArray with two type strings
            var type1 = new NSString("public.file-url");
            var type2 = new NSString("NSFilenamesPboardType");
            var typesArray = NSArray.FromNSObjects(type1, type2);

            objc_msgSend_void_IntPtr(overlay,
                Selector.GetHandle("registerForDraggedTypes:"),
                typesArray.Handle);

            Log("Registered dragged types: public.file-url, NSFilenamesPboardType");
        }
        catch (Exception ex)
        {
            Log($"RegisterDraggedTypes failed: {ex.Message}");
        }
    }

    // ── Helpers ──

    private static IntPtr CreateNSString(string str)
    {
        var nsStr = new NSString(str);
        return nsStr.Handle;
    }

    private static string? FileUrlToPath(string fileUrl)
    {
        try
        {
            if (fileUrl.StartsWith("file://"))
            {
                var uri = new Uri(fileUrl);
                return uri.LocalPath;
            }
            return fileUrl;
        }
        catch
        {
            // Fallback: manual decode
            var path = fileUrl.Replace("file://", "");
            return Uri.UnescapeDataString(path);
        }
    }

    private static void Log(string msg)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] DropOverlay: {msg}\n";
            File.AppendAllText(LogPath, line);
        }
        catch { /* ignore */ }
    }
}
