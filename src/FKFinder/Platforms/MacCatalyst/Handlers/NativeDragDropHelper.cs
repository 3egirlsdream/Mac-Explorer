using FKFinder.Services;
using Foundation;
using ObjCRuntime;
using UIKit;
using UniformTypeIdentifiers;
using WebKit;

namespace FKFinder.Platforms.MacCatalyst.Handlers;

/// <summary>
/// Attaches a native UIDropInteraction to the WKWebView for receiving file drops
/// from Finder, other apps, and other FKFinder windows.
/// Drag OUT is handled via HTML5 dragstart + text/uri-list in native-drag.js.
/// </summary>
public static class NativeDragDropHelper
{
    public static void AttachToWebView(WKWebView webView, IDragDropBridge bridge)
    {
        // Only add UIDropInteraction for receiving external drops.
        // Do NOT add UIDragInteraction — WKWebView's internal event handling
        // prevents UIKit gesture recognizers from triggering on Mac Catalyst.
        // Drag OUT is handled by HTML5 drag + file:// URLs set in JS dragstart.
        var dropDelegate = new FileDropDelegate(webView, bridge);
        var dropInteraction = new UIDropInteraction(dropDelegate);
        webView.AddInteraction(dropInteraction);

        Console.WriteLine("[FKFinder/Drag] Attached UIDropInteraction for receiving drops");
    }

    private class FileDropDelegate : NSObject, IUIDropInteractionDelegate
    {
        private readonly WKWebView _webView;
        private readonly IDragDropBridge _bridge;

        public FileDropDelegate(WKWebView webView, IDragDropBridge bridge)
        {
            _webView = webView;
            _bridge = bridge;
        }

        [Export("dropInteraction:canHandleSession:")]
        public bool CanHandleSession(UIDropInteraction interaction, IUIDropSession session)
        {
            bool canHandle = session.CanLoadObjects(new Class(typeof(NSUrl)));
            Console.WriteLine($"[FKFinder/Drop] CanHandle: {canHandle}");
            return canHandle;
        }

        [Export("dropInteraction:sessionDidUpdate:")]
        public UIDropProposal SessionDidUpdate(UIDropInteraction interaction, IUIDropSession session)
        {
            var point = session.LocationInView(_webView);
            var js = FormattableString.Invariant(
                $"window.fkfinderNativeDrag && window.fkfinderNativeDrag.setDropHighlight({point.X}, {point.Y})");
            _webView.EvaluateJavaScript(js, null!);

            return new UIDropProposal(UIDropOperation.Move);
        }

        [Export("dropInteraction:performDrop:")]
        public void PerformDrop(UIDropInteraction interaction, IUIDropSession session)
        {
            Console.WriteLine("[FKFinder/Drop] PerformDrop");
            var point = session.LocationInView(_webView);

            var js = FormattableString.Invariant(
                $"window.fkfinderNativeDrag ? window.fkfinderNativeDrag.getDropTargetAtPoint({point.X}, {point.Y}) : null");

            _webView.EvaluateJavaScript(js, (result, error) =>
            {
                string? targetDir = null;
                if (result is NSString nsStr)
                    targetDir = nsStr.ToString();

                if (string.IsNullOrEmpty(targetDir))
                    targetDir = _bridge.GetCurrentDirectory();

                Console.WriteLine($"[FKFinder/Drop] Target: {targetDir}");

                var dir = targetDir;
                session.LoadObjects(new Class(typeof(NSUrl)), (items) =>
                {
                    var paths = items
                        .OfType<NSUrl>()
                        .Where(u => u.IsFileUrl)
                        .Select(u => u.Path!)
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToArray();

                    Console.WriteLine($"[FKFinder/Drop] Loaded {paths.Length} file URL(s)");

                    if (paths.Length > 0)
                    {
                        MainThread.BeginInvokeOnMainThread(() =>
                            _bridge.NotifyExternalDrop(paths, dir));
                    }
                });
            });

            _webView.EvaluateJavaScript(
                "window.fkfinderNativeDrag && window.fkfinderNativeDrag.clearDropHighlight()", null!);
        }

        [Export("dropInteraction:sessionDidExit:")]
        public void SessionDidExit(UIDropInteraction interaction, IUIDropSession session)
        {
            _webView.EvaluateJavaScript(
                "window.fkfinderNativeDrag && window.fkfinderNativeDrag.clearDropHighlight()", null!);
        }

        [Export("dropInteraction:sessionDidEnd:withOperation:")]
        public void SessionDidEnd(UIDropInteraction interaction, IUIDropSession session, UIDropOperation operation)
        {
            _webView.EvaluateJavaScript(
                "window.fkfinderNativeDrag && window.fkfinderNativeDrag.clearDropHighlight()", null!);
        }
    }
}
