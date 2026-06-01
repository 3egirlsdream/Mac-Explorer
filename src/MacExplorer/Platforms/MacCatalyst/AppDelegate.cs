using Foundation;
using UIKit;
using MacExplorer.Services;

namespace MacExplorer;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	/// <summary>
	/// Fallback handler for folder open requests (e.g. when app is already running
	/// and the scene delegate doesn't handle it).
	/// </summary>
	[Export("application:openURLs:")]
	public void OpenUrls(UIApplication application, NSUrl[] urls)
	{
		Console.WriteLine($"[MacExplorer] AppDelegate.OpenUrls called with {urls.Length} URL(s)");

		foreach (var url in urls)
		{
			var path = url.FilePathUrl?.Path ?? url.Path;
			Console.WriteLine($"[MacExplorer] AppDelegate.OpenUrls: raw path='{path}'");
			if (string.IsNullOrEmpty(path)) continue;

			if (!System.IO.Directory.Exists(path))
			{
				Console.WriteLine($"[MacExplorer] AppDelegate.OpenUrls: path is not a directory, skipping");
				continue;
			}

			Console.WriteLine($"[MacExplorer] AppDelegate.OpenUrls: valid folder path='{path}'");

			// 始终设置 _coldStartPath 作为最终后备
			SceneDelegate._coldStartPath = path;

			// 同步立即设置 PendingNavigationPath（不在 MainThread 回调内）
			var bridge = IPlatformApplication.Current?.Services?.GetService<NavigationBridge>();
			if (bridge != null)
			{
				bridge.PendingNavigationPath = path;
				Console.WriteLine($"[MacExplorer] AppDelegate.OpenUrls: set PendingNavigationPath='{path}'");

				// 然后异步尝试即时导航
					MainThread.BeginInvokeOnMainThread(async () =>
					{
						try
						{
							Console.WriteLine($"[MacExplorer] AppDelegate.OpenUrls: attempting NavigateAsync for '{path}'");
							await bridge.NavigateAsync(path);
						}
						catch (Exception ex)
						{
							Console.WriteLine($"[MacExplorer] AppDelegate.OpenUrls: navigation failed: {ex}");
						}
					});
			}
			else
			{
				Console.WriteLine($"[MacExplorer] AppDelegate.OpenUrls: NavigationBridge not available, relying on _coldStartPath");
			}

			break; // 只处理第一个有效文件夹路径
		}
	}
}
