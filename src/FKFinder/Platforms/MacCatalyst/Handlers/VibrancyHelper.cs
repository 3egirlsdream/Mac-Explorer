using System.Runtime.InteropServices;
using CoreFoundation;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using UIKit;

namespace FKFinder.Platforms.MacCatalyst.Handlers;

/// <summary>
/// Makes the Mac Catalyst window truly transparent with vibrancy by operating
/// at the AppKit level (not UIKit), following Steven Troughton-Smith's proven approach.
///
/// Key insight: inserting NSVisualEffectView (AppKit) into NSWindow.contentView
/// is invisible to MAUI's layout engine, so it never gets reset.
/// We listen for UISBHSDidCreateWindowForSceneNotification to get the exact
/// moment the NSWindow exists.
/// </summary>
public static class VibrancyHelper
{
    // ── ObjC runtime ──
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_bool(IntPtr receiver, IntPtr selector, bool arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_nint(IntPtr receiver, IntPtr selector, nint arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_CGRect(IntPtr receiver, IntPtr selector, CGRect rect);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend_stret")]
    private static extern void objc_msgSend_stret_CGRect(out CGRect result, IntPtr receiver, IntPtr selector);

    // For arm64 stret is not used; CGRect is returned in registers
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern CGRect objc_msgSend_ret_CGRect(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_nuint(IntPtr receiver, IntPtr selector, nuint arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_double(IntPtr receiver, IntPtr selector, double arg);

    private static bool _registered;
    private static readonly HashSet<IntPtr> _configuredWindows = new();
    private static NSObject? _notificationObserver;

    /// <summary>
    /// Call once at app startup. Registers for the private notification that fires
    /// when the NSWindow is created for a UIWindowScene.
    /// </summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;

        // Listen for the notification that fires when AppKit creates the host NSWindow
        // This fires for EVERY new window scene, not just the first one
        _notificationObserver = NSNotificationCenter.DefaultCenter.AddObserver(
            new NSString("UISBHSDidCreateWindowForSceneNotification"),
            OnWindowCreatedForScene);

        System.Diagnostics.Debug.WriteLine("VibrancyHelper: Registered for UISBHSDidCreateWindowForSceneNotification");
    }

    private static void OnWindowCreatedForScene(NSNotification notification)
    {
        System.Diagnostics.Debug.WriteLine($"VibrancyHelper: Window created notification received");

        try
        {
            // Get the NSWindow via NSApplication.sharedApplication.delegate.hostWindowForUIWindow:
            // But first we need the NSWindow — try from the notification's userInfo
            var nsWindow = FindNSWindowFromNotification(notification);

            if (nsWindow == IntPtr.Zero)
            {
                // Fallback: try via NSApplication after a short delay
                DispatchQueue.MainQueue.DispatchAfter(
                    new DispatchTime(DispatchTime.Now, (long)(0.1 * 1_000_000_000)),
                    () => SetupFromNSApplication());
                return;
            }

            ConfigureNSWindow(nsWindow);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"VibrancyHelper: Error in notification handler: {ex.Message}");
        }
    }

    private static IntPtr FindNSWindowFromNotification(NSNotification notification)
    {
        // The notification userInfo may contain a SceneIdentifier
        // Try to get the NSWindow via NSApp.delegate.hostWindowForSceneIdentifier:
        try
        {
            if (notification.UserInfo != null)
            {
                var sceneId = notification.UserInfo.ValueForKey(new NSString("SceneIdentifier"));
                if (sceneId != null)
                {
                    System.Diagnostics.Debug.WriteLine($"VibrancyHelper: SceneIdentifier = {sceneId}");

                    var nsAppClass = Class.GetHandle("NSApplication");
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
                                System.Diagnostics.Debug.WriteLine("VibrancyHelper: Got NSWindow via hostWindowForSceneIdentifier:");
                                return hostWindow;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"VibrancyHelper: hostWindowForSceneIdentifier failed: {ex.Message}");
        }

        return IntPtr.Zero;
    }

    private static void SetupFromNSApplication(int retryCount = 0)
    {
        if (retryCount > 10) return; // Give up after ~3 seconds
        try
        {
            var nsAppClass = Class.GetHandle("NSApplication");
            var sharedApp = objc_msgSend(nsAppClass, Selector.GetHandle("sharedApplication"));
            if (sharedApp == IntPtr.Zero) return;

            // Try keyWindow first (the most recently activated window)
            var keyWindow = objc_msgSend(sharedApp, Selector.GetHandle("keyWindow"));
            if (keyWindow != IntPtr.Zero && !_configuredWindows.Contains(keyWindow))
            {
                ConfigureNSWindow(keyWindow);
                return;
            }

            // Try mainWindow
            var mainWindow = objc_msgSend(sharedApp, Selector.GetHandle("mainWindow"));
            if (mainWindow != IntPtr.Zero && !_configuredWindows.Contains(mainWindow))
            {
                ConfigureNSWindow(mainWindow);
                return;
            }

            System.Diagnostics.Debug.WriteLine($"VibrancyHelper: No unconfigured NSWindow found, retry {retryCount}...");
            DispatchQueue.MainQueue.DispatchAfter(
                new DispatchTime(DispatchTime.Now, (long)(0.3 * 1_000_000_000)),
                () => SetupFromNSApplication(retryCount + 1));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"VibrancyHelper: SetupFromNSApplication failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The core setup: configure NSWindow transparency and insert NSVisualEffectView
    /// at the AppKit layer (not UIKit), so MAUI can never reset it.
    /// </summary>
    private static void ConfigureNSWindow(IntPtr nsWindow)
    {
        // Track per-window to avoid configuring the same NSWindow twice
        if (_configuredWindows.Contains(nsWindow)) return;
        _configuredWindows.Add(nsWindow);

        try
        {
            // ── 1. Make NSWindow non-opaque ──
            objc_msgSend_void_bool(nsWindow, Selector.GetHandle("setOpaque:"), false);
            System.Diagnostics.Debug.WriteLine("VibrancyHelper: setOpaque:NO");

            // ── 2. Get the NSWindow's contentView (this is an NSView, not UIView) ──
            var contentView = objc_msgSend(nsWindow, Selector.GetHandle("contentView"));
            if (contentView == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("VibrancyHelper: contentView is null!");
                return;
            }

            // ── 3. Create NSVisualEffectView ──
            var nsVisualEffectViewClass = Class.GetHandle("NSVisualEffectView");
            if (nsVisualEffectViewClass == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("VibrancyHelper: NSVisualEffectView class not found!");
                return;
            }

            // alloc + init
            var effectView = objc_msgSend(nsVisualEffectViewClass, Selector.GetHandle("alloc"));
            effectView = objc_msgSend(effectView, Selector.GetHandle("init"));

            // ── 4. Configure NSVisualEffectView properties ──
            // material = .hudWindow (NSVisualEffectMaterialHUDWindow = 13)
            // More transparent than .underWindowBackground, lets more desktop show through
            objc_msgSend_void_nint(effectView, Selector.GetHandle("setMaterial:"), 13);

            // blendingMode = .behindWindow (NSVisualEffectBlendingModeBehindWindow = 0)
            objc_msgSend_void_nint(effectView, Selector.GetHandle("setBlendingMode:"), 0);

            // state = .followsWindowActiveState (NSVisualEffectStateFollowsWindowActiveState = 0)
            objc_msgSend_void_nint(effectView, Selector.GetHandle("setState:"), 0);

            // autoresizingMask = [.width, .height] (NSViewWidthSizable | NSViewHeightSizable = 18)
            objc_msgSend_void_nuint(effectView, Selector.GetHandle("setAutoresizingMask:"), 18);

            // Set frame to contentView.bounds
            var bounds = objc_msgSend_ret_CGRect(contentView, Selector.GetHandle("bounds"));
            objc_msgSend_CGRect(effectView, Selector.GetHandle("setFrame:"), bounds);

            // Set alphaValue to increase transparency (0.85 = 15% more see-through)
            objc_msgSend_void_double(effectView, Selector.GetHandle("setAlphaValue:"), 0.85);

            System.Diagnostics.Debug.WriteLine($"VibrancyHelper: contentView bounds = {bounds}");

            // ── 5. Insert NSVisualEffectView at index 0 of contentView's subviews ──
            // [contentView addSubview:effectView positioned:NSWindowBelow relativeTo:nil]
            // NSWindowBelow = -1, but we'll use a simpler approach:
            // Get existing subviews, insert at index 0
            var subviews = objc_msgSend(contentView, Selector.GetHandle("subviews"));
            if (subviews != IntPtr.Zero)
            {
                // Use insertSubview-like approach: addSubview:positioned:relativeTo:
                // positioned: NSWindowBelow = -1
                objc_msgSend_addSubview_positioned(contentView,
                    Selector.GetHandle("addSubview:positioned:relativeTo:"),
                    effectView, -1, IntPtr.Zero);
            }
            else
            {
                // Fallback: just add it
                objc_msgSend_void_IntPtr(contentView, Selector.GetHandle("addSubview:"), effectView);
            }

            // ── 6. Set shadow ──
            objc_msgSend_void_bool(nsWindow, Selector.GetHandle("setHasShadow:"), true);

            // ── 7. Set default window size (1372×849) at NSWindow level ──
            // NSWindow uses flipped coordinates (origin at bottom-left)
            // First get current frame to preserve origin, then resize with center
            var currentFrame = objc_msgSend_ret_CGRect(nsWindow, Selector.GetHandle("frame"));
            var newFrame = new CGRect(currentFrame.X, currentFrame.Y, 1372, 849);
            objc_msgSend_setFrame(nsWindow, Selector.GetHandle("setFrame:display:"), newFrame, true);

            // ── 8. Center the new window on screen ──
            objc_msgSend(nsWindow, Selector.GetHandle("center"));

            System.Diagnostics.Debug.WriteLine($"VibrancyHelper: NSVisualEffectView inserted at AppKit layer for window {nsWindow}. Total configured: {_configuredWindows.Count}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"VibrancyHelper: ConfigureNSWindow failed: {ex.Message}");
        }
    }

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_addSubview_positioned(
        IntPtr receiver, IntPtr selector, IntPtr view, nint place, IntPtr relativeTo);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_setFrame(
        IntPtr receiver, IntPtr selector, CGRect frame, bool display);

    /// <summary>
    /// Also ensure UIKit views are transparent so the AppKit effect shows through.
    /// Call this after the MAUI window is ready.
    /// </summary>
    public static void MakeUIKitLayerTransparent(UIWindow platformWindow)
    {
        platformWindow.BackgroundColor = UIColor.Clear;
        platformWindow.Opaque = false;

        if (platformWindow.RootViewController?.View is UIView rootView)
        {
            rootView.BackgroundColor = UIColor.Clear;
            rootView.Opaque = false;
        }
    }

}
