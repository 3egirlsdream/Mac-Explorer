using UIKit;

namespace FKFinder.Platforms.MacCatalyst.Handlers;

/// <summary>
/// Applies macOS native window styling for a modern look.
/// NOTE: Frosted glass / vibrancy effect is achieved via CSS backdrop-filter
/// in the Blazor web layer, not via native UIVisualEffectView, because
/// making the WKWebView transparent causes rendering issues (black screen).
/// This helper only configures the window title bar style.
/// </summary>
public static class VibrancyHelper
{
    public static void ApplyWindowStyle(UIWindow platformWindow)
    {
        // Configure the window for a clean, modern appearance
        platformWindow.BackgroundColor = UIColor.SystemBackground;

        // Set the root view background to match the system
        var viewController = platformWindow.RootViewController;
        if (viewController?.View != null)
        {
            viewController.View.BackgroundColor = UIColor.SystemBackground;
        }
    }
}
