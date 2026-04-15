using Foundation;
using Microsoft.Maui;
using ObjCRuntime;
using UIKit;
using MacExplorer.Services;

namespace MacExplorer;

[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate
{
    /// <summary>
    /// Static fallback for cold-start path when DI is not yet available.
    /// </summary>
    internal static string? _coldStartPath;

    /// <summary>
    /// Called by macOS on cold start (scene:willConnectToSession:options:).
    /// Captures the folder URL passed via "open -a MacExplorer /path".
    /// </summary>
    [Export("scene:willConnectToSession:options:")]
    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        // 关键：在 base.WillConnect 之前提取 URL，防止 MAUI 基类消费 connectionOptions
        string? folderPath = null;
        if (connectionOptions?.UrlContexts != null)
        {
            Console.WriteLine($"[MacExplorer] SceneDelegate.WillConnect: UrlContexts count={connectionOptions.UrlContexts.Count}");
            foreach (var ctx in connectionOptions.UrlContexts.ToArray<UIOpenUrlContext>())
            {
                var url = ctx.Url;
                if (url == null) continue;

                var path = url.FilePathUrl?.Path ?? url.Path;
                if (!string.IsNullOrEmpty(path) && System.IO.Directory.Exists(path))
                {
                    folderPath = path;
                    Console.WriteLine($"[MacExplorer] WillConnect: extracted path BEFORE base call: {path}");
                    break;
                }
            }
        }
        else
        {
            Console.WriteLine("[MacExplorer] SceneDelegate.WillConnect: UrlContexts is null");
        }

        // 然后调用 base，让 MAUI 完成初始化
        base.WillConnect(scene, session, connectionOptions!);

        // base 调用后再存储路径
        if (!string.IsNullOrEmpty(folderPath))
        {
            Console.WriteLine($"[MacExplorer] WillConnect: storing path after base call: {folderPath}");
            // 始终设置 _coldStartPath 作为最终后备
            _coldStartPath = folderPath;

            var bridge = IPlatformApplication.Current?.Services?.GetService<NavigationBridge>();
            if (bridge != null)
            {
                bridge.PendingNavigationPath = folderPath;
                Console.WriteLine($"[MacExplorer] WillConnect: set PendingNavigationPath='{folderPath}'");
            }
            else
            {
                Console.WriteLine($"[MacExplorer] WillConnect: DI not ready, relying on _coldStartPath='{folderPath}'");
            }
        }
    }

    /// <summary>
    /// Called by macOS when the user double-clicks a folder (or uses "open" command)
    /// while the app is already running (warm start).
    /// </summary>
    [Export("scene:openURLContexts:")]
    public void OpenUrlContexts(UIScene scene, NSSet<UIOpenUrlContext> urlContexts)
    {
        Console.WriteLine($"[MacExplorer] SceneDelegate.OpenUrlContexts called with {urlContexts.Count} URL(s)");

        foreach (var ctx in urlContexts.ToArray<UIOpenUrlContext>())
        {
            var url = ctx.Url;
            if (url == null) continue;

            var path = url.FilePathUrl?.Path ?? url.Path;
            if (string.IsNullOrEmpty(path)) continue;

            Console.WriteLine($"[MacExplorer] SceneDelegate.OpenUrlContexts: raw path='{path}'");

            if (!System.IO.Directory.Exists(path))
            {
                Console.WriteLine($"[MacExplorer] SceneDelegate.OpenUrlContexts: path is not a directory, skipping");
                continue;
            }

            Console.WriteLine($"[MacExplorer] SceneDelegate.OpenUrlContexts: valid folder path='{path}'");

            // 始终设置 _coldStartPath 作为最终后备
            _coldStartPath = path;

            var bridge = IPlatformApplication.Current?.Services?.GetService<NavigationBridge>();
            if (bridge != null)
            {
                // 同步立即设置 PendingNavigationPath（不需要 MainThread，这只是设置一个属性）
                bridge.PendingNavigationPath = path;
                Console.WriteLine($"[MacExplorer] SceneDelegate.OpenUrlContexts: set PendingNavigationPath='{path}'");

                // 然后异步尝试即时导航（适用于热启动——已有活跃窗口的情况）
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    Console.WriteLine($"[MacExplorer] SceneDelegate.OpenUrlContexts: attempting NavigateAsync for '{path}'");
                    await bridge.NavigateAsync(path);
                });
            }
            else
            {
                Console.WriteLine($"[MacExplorer] SceneDelegate.OpenUrlContexts: DI not ready, relying on _coldStartPath='{path}'");
            }

            break; // 只处理第一个文件夹
        }
    }
}
