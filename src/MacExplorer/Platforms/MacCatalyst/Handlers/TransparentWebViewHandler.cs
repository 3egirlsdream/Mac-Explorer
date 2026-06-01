using MacExplorer.Services;
using UIKit;
using WebKit;

namespace MacExplorer.Platforms.MacCatalyst.Handlers;

/// <summary>
/// Custom BlazorWebView handler that makes the WKWebView background transparent,
/// allowing the NSVisualEffectView vibrancy to show through.
/// </summary>
public class TransparentWebViewHandler : Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebViewHandler
{
    protected override WKWebView CreatePlatformView()
    {
        var webView = base.CreatePlatformView();

#if DEBUG
        // Enable Safari DevTools inspection for the WKWebView
        if (OperatingSystem.IsIOSVersionAtLeast(16, 4))
            webView.Inspectable = true;
#endif

        // Make WebView transparent so vibrancy background is visible
        webView.Opaque = false;
        webView.BackgroundColor = UIColor.Clear;

        // SetScrollViewBackground must happen after the view is created
        if (webView.ScrollView != null)
        {
            webView.ScrollView.BackgroundColor = UIColor.Clear;
            webView.ScrollView.Bounces = false;

            // Remove scroll indicators for cleaner look
            webView.ScrollView.ShowsHorizontalScrollIndicator = false;
            webView.ScrollView.ShowsVerticalScrollIndicator = false;
        }

        return webView;
    }

    protected override void ConnectHandler(WKWebView platformView)
    {
        base.ConnectHandler(platformView);

        // Ensure transparency is applied after connection
        platformView.Opaque = false;
        platformView.BackgroundColor = UIColor.Clear;
        if (platformView.ScrollView != null)
        {
            platformView.ScrollView.BackgroundColor = UIColor.Clear;
            platformView.ScrollView.Opaque = false;
        }

        // Attach native drag-and-drop interactions for external file transfer
        var bridge = IPlatformApplication.Current?.Services?.GetService<IDragDropBridge>();
        if (bridge != null)
        {
            NativeDragDropHelper.AttachToWebView(platformView, bridge);
            NativeDragDropHelper.RegisterWebViewForWindow(platformView);
        }
    }

    protected override void DisconnectHandler(WKWebView platformView)
    {
        NativeDragDropHelper.DetachFromWebView(platformView);
        base.DisconnectHandler(platformView);
    }
}
