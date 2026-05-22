using System.Runtime.InteropServices;
using CoreFoundation;
using MacExplorer.Services;
using Foundation;
using ObjCRuntime;
using UIKit;
using WebKit;

namespace MacExplorer.Platforms.MacCatalyst.Handlers;

/// <summary>
/// Handles native drag-and-drop for MacExplorer.
/// - Drag OUT: HTML5 drag + file:// URLs (injected in native-drag.js)
/// - Drop IN (external / cross-window): DropOverlayHelper handles at AppKit level
/// - Drop IN (same-window): JS drop event → WKScriptMessageHandler → IDragDropBridge
/// - Drag State: tracks internal drag state to hide/show overlay
/// </summary>
public static class NativeDragDropHelper
{
    // ── ObjC runtime P/Invoke ──

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_count(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_objectAtIndex(IntPtr receiver, IntPtr selector, nuint index);

    // ── Static state ──
    private static IDragDropBridge? _bridge;
    private static readonly List<FileDropMessageHandler> _jsHandlers = new();
    private static readonly List<DragStateMessageHandler> _stateHandlers = new();
    private static readonly List<JsLogMessageHandler> _logHandlers = new();
    private static readonly Dictionary<WKWebView, IntPtr> _webViewNSWindowMap = new();
    private static IntPtr _lastDragWindow;

    /// <summary>
    /// True while an internal HTML5 drag is in progress (same-window or cross-window).
    /// Used by DropOverlayHelper to avoid intercepting internal drags.
    /// </summary>
    public static bool IsInternalDragActive { get; private set; }

    /// <summary>
    /// File paths being dragged in the current internal drag session.
    /// Set from JS dragstart via fkfinderDragState message.
    /// Used by DropOverlayHelper.performDragOperation to avoid pasteboard extraction.
    /// </summary>
    public static string[] InternalDragPaths { get; private set; } = [];

    /// <summary>
    /// Attach to a WKWebView for both JS-based drops and drag state tracking.
    /// Called from TransparentWebViewHandler.ConnectHandler for each new WKWebView.
    /// </summary>
    public static void AttachToWebView(WKWebView webView, IDragDropBridge bridge)
    {
        _bridge = bridge;

        // JS message handler for same-window HTML5 DnD (internal drag → subfolder)
        var handler = new FileDropMessageHandler(bridge);
        _jsHandlers.Add(handler);
        webView.Configuration.UserContentController.AddScriptMessageHandler(handler, "fkfinderDrop");

        // JS message handler for drag state tracking (hide/show overlay)
        var stateHandler = new DragStateMessageHandler();
        _stateHandlers.Add(stateHandler);
        webView.Configuration.UserContentController.AddScriptMessageHandler(stateHandler, "fkfinderDragState");

        // JS → Native log bridge (console.log doesn't work reliably in WKWebView)
        var logHandler = new JsLogMessageHandler();
        _logHandlers.Add(logHandler);
        webView.Configuration.UserContentController.AddScriptMessageHandler(logHandler, "fkfinderLog");

        Log("AttachToWebView: native drop handled by DropOverlayHelper at AppKit level");
    }

    /// <summary>
    /// Register a WKWebView and discover its NSWindow (delayed since window may not be ready).
    /// Called from TransparentWebViewHandler.ConnectHandler.
    /// </summary>
    public static void RegisterWebViewForWindow(WKWebView webView)
    {
        // Delayed discovery: UIWindow → NSWindow mapping
        // The UIWindow may not be available immediately during ConnectHandler
        DiscoverNSWindowForWebView(webView, 0);
    }

    private static void DiscoverNSWindowForWebView(WKWebView webView, int attempt)
    {
        if (attempt > 20)
        {
            Log("Gave up finding NSWindow for WKWebView");
            return;
        }

        var uiWindow = webView.Window;
        if (uiWindow == null)
        {
            DispatchQueue.MainQueue.DispatchAfter(
                new DispatchTime(DispatchTime.Now, (long)(0.3 * 1_000_000_000)),
                () => DiscoverNSWindowForWebView(webView, attempt + 1));
            return;
        }

        // Find NSWindow via NSApplication
        var nsWindow = FindNSWindowForUIWindow(uiWindow);
        if (nsWindow != IntPtr.Zero)
        {
            _webViewNSWindowMap[webView] = nsWindow;
            DropOverlayHelper.RegisterWebView(nsWindow, webView);
            Log($"Mapped WKWebView to NSWindow {nsWindow}");
        }
        else if (attempt < 20)
        {
            DispatchQueue.MainQueue.DispatchAfter(
                new DispatchTime(DispatchTime.Now, (long)(0.3 * 1_000_000_000)),
                () => DiscoverNSWindowForWebView(webView, attempt + 1));
        }
    }

    private static IntPtr FindNSWindowForUIWindow(UIKit.UIWindow uiWindow)
    {
        try
        {
            // Get the scene from the UIWindow
            var windowScene = uiWindow.WindowScene;
            if (windowScene == null)
            {
                Log("FindNSWindowForUIWindow: windowScene is null, falling back to keyWindow");
                return GetKeyWindow();
            }

            // Get the UIWindow's underlying view's layer host pointer
            // On Mac Catalyst, each UIWindow is backed by an NSWindow
            // We can find it by matching the UIWindow's scene with NSWindow's scene
            
            var nsAppClass = ObjCRuntime.Class.GetHandle("NSApplication");
            var sharedApp = objc_msgSend(nsAppClass, Selector.GetHandle("sharedApplication"));
            if (sharedApp == IntPtr.Zero)
            {
                Log("FindNSWindowForUIWindow: sharedApplication is null");
                return IntPtr.Zero;
            }

            // Get all windows from NSApplication
            var windows = objc_msgSend(sharedApp, Selector.GetHandle("windows"));
            if (windows == IntPtr.Zero)
            {
                Log("FindNSWindowForUIWindow: windows array is null");
                return GetKeyWindow();
            }

            var windowCount = objc_msgSend_count(windows, Selector.GetHandle("count"));
            Log($"FindNSWindowForUIWindow: checking {windowCount} NSWindows...");

            // Get the UIScene's persistent identifier for comparison
            var session = windowScene.Session;
            var scenePersistentId = session?.PersistentIdentifier ?? "";

            // Iterate through all NSWindows to find the one matching our UIWindow
            for (nuint i = 0; i < (nuint)windowCount; i++)
            {
                var nsWindow = objc_msgSend_objectAtIndex(windows, Selector.GetHandle("objectAtIndex:"), i);
                if (nsWindow == IntPtr.Zero) continue;

                // Get the window's scene identifier
                var windowSceneId = GetWindowSceneIdentifier(nsWindow);
                if (string.IsNullOrEmpty(windowSceneId)) continue;

                // Check if this NSWindow's scene identifier contains our scene's persistent ID
                if (!string.IsNullOrEmpty(scenePersistentId) && 
                    windowSceneId.Contains(scenePersistentId))
                {
                    Log($"FindNSWindowForUIWindow: matched NSWindow {nsWindow} by scene ID");
                    return nsWindow;
                }
            }

            // Fallback: use keyWindow if no match found
            Log($"FindNSWindowForUIWindow: no matching NSWindow found for scene {scenePersistentId}, falling back to keyWindow");
            return GetKeyWindow();
        }
        catch (Exception ex)
        {
            Log($"FindNSWindowForUIWindow error: {ex.Message}");
            return GetKeyWindow();
        }
    }

    private static string? GetWindowSceneIdentifier(IntPtr nsWindow)
    {
        try
        {
            // Get the window's windowScene
            var windowScene = objc_msgSend(nsWindow, Selector.GetHandle("windowScene"));
            if (windowScene == IntPtr.Zero) return null;

            // Get the session
            var session = objc_msgSend(windowScene, Selector.GetHandle("session"));
            if (session == IntPtr.Zero) return null;

            // Get the persistentIdentifier
            var persistentId = objc_msgSend(session, Selector.GetHandle("persistentIdentifier"));
            if (persistentId == IntPtr.Zero) return null;

            // Convert to string
            var nsStr = Runtime.GetNSObject<Foundation.NSString>(persistentId);
            return nsStr?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static IntPtr GetKeyWindow()
    {
        try
        {
            var nsAppClass = ObjCRuntime.Class.GetHandle("NSApplication");
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
    /// Find the NSWindow associated with a WKWebView.
    /// </summary>
    public static IntPtr FindNSWindowForWebView(WKWebView webView)
    {
        if (_webViewNSWindowMap.TryGetValue(webView, out var nsWindow))
            return nsWindow;
        return IntPtr.Zero;
    }

    // ── Helpers ──

    private static readonly string LogPath = "/tmp/fkfinder-drag.log";

    private static void Log(string msg)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n";
            File.AppendAllText(LogPath, line);
        }
        catch { /* ignore */ }
    }

    // ── WKScriptMessageHandler for JS drops (same-window HTML5 DnD) ──

    private class FileDropMessageHandler : NSObject, IWKScriptMessageHandler
    {
        private readonly IDragDropBridge _bridge;

        public FileDropMessageHandler(IDragDropBridge bridge) => _bridge = bridge;

        public void DidReceiveScriptMessage(
            WKUserContentController userContentController, WKScriptMessage message)
        {
            if (message.Body is not NSDictionary dict)
                return;

            var pathsArray = dict["paths"] as NSArray;
            if (pathsArray == null || pathsArray.Count == 0)
                return;

            string? targetDir = null;
            if (dict["target"] is NSString targetStr)
                targetDir = targetStr.ToString();

            if (string.IsNullOrEmpty(targetDir))
                targetDir = _bridge.GetCurrentDirectory();

            var paths = new string[(int)pathsArray.Count];
            for (nuint i = 0; i < pathsArray.Count; i++)
                paths[i] = pathsArray.GetItem<NSString>(i).ToString();

            var isInternal = dict["isInternal"] as NSNumber;
            if (isInternal?.BoolValue == true)
            {
                Log($"JS internal drop: {paths.Length} path(s) -> {targetDir}");
                var nsWindow = message.WebView != null ? FindNSWindowForWebView(message.WebView) : IntPtr.Zero;
                _bridge.HandleInternalDrop(paths, targetDir, nsWindow);
            }
            else
            {
                Log($"JS drop: {paths.Length} path(s) -> {targetDir}");
                var nsWindow = message.WebView != null ? FindNSWindowForWebView(message.WebView) : IntPtr.Zero;
                _bridge.HandleExternalDrop(paths, targetDir, nsWindow);
            }
        }
    }

    // ── WKScriptMessageHandler for drag state tracking ──

    private class DragStateMessageHandler : NSObject, IWKScriptMessageHandler
    {
        public void DidReceiveScriptMessage(
            WKUserContentController userContentController, WKScriptMessage message)
        {
            if (message.Body is not NSDictionary dict) return;

            var started = dict["started"] as NSNumber;
            if (started == null) return;

            var isDragStarted = started.BoolValue;
            Log($"DragState: internal drag {(isDragStarted ? "STARTED" : "ENDED")}");

            IsInternalDragActive = isDragStarted;

            if (isDragStarted)
            {
                // Store file paths sent from JS for use in performDragOperation.
                // This avoids relying on native pasteboard extraction, which may
                // not work with JS-set dataTransfer in WKWebView.
                var pathsArray = dict["paths"] as NSArray;
                if (pathsArray != null && pathsArray.Count > 0)
                {
                    var paths = new string[(int)pathsArray.Count];
                    for (nuint i = 0; i < pathsArray.Count; i++)
                        paths[i] = pathsArray.GetItem<NSString>(i).ToString();
                    InternalDragPaths = paths;
                    Log($"DragState: stored {paths.Length} internal drag path(s)");
                }
            }
            else
            {
                InternalDragPaths = [];
            }

            // NOTE: We do NOT hide the overlay during internal drags.
            // WKWebView suppresses HTML5 dragover/drop events when a native
            // NSDraggingSession is active, so internal drags must be handled
            // entirely at the native AppKit layer via DropOverlayHelper.
            // The overlay must remain visible to receive draggingEntered,
            // draggingUpdated, and performDragOperation callbacks.
        }
    }

    // ── WKScriptMessageHandler for JS → Native log bridge ──
    // console.log output is not reliably captured in WKWebView on Mac Catalyst.
    // This handler writes JS diagnostic messages to the native drag log file.

    private class JsLogMessageHandler : NSObject, IWKScriptMessageHandler
    {
        public void DidReceiveScriptMessage(
            WKUserContentController userContentController, WKScriptMessage message)
        {
            var msg = message.Body?.ToString() ?? "(null)";
            Log($"[JS] {msg}");
        }
    }
}
