using UIKit;
using WebKit;

namespace FKFinder.Platforms.MacCatalyst.Handlers;

/// <summary>
/// Custom BlazorWebView handler that makes the WKWebView background transparent,
/// allowing the NSVisualEffectView vibrancy to show through.
/// </summary>
public class TransparentWebViewHandler : Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebViewHandler
{
    protected override WKWebView CreatePlatformView()
    {
        var webView = base.CreatePlatformView();

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
        }
    }
}
