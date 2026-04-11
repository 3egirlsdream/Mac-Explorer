namespace FKFinder;

public partial class App : Application
{
    private bool _dockMenuRegistered;

	public App()
	{
		InitializeComponent();

#if MACCATALYST
        // Register for the NSWindow creation notification as early as possible
        Platforms.MacCatalyst.Handlers.VibrancyHelper.Register();
#endif
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new MainPage()) { Title = "FKFinder" };

#if MACCATALYST
        window.Activated += OnWindowActivated;
#endif

        return window;
	}

    /// <summary>
    /// Opens a new FKFinder window (multi-instance support).
    /// </summary>
    public void OpenNewWindow()
    {
        Console.WriteLine("[FKFinder] OpenNewWindow called");
#if MACCATALYST
        // MAUI's OpenWindow() is broken on Mac Catalyst (silently does nothing).
        // Use native UIKit scene API directly.
#pragma warning disable CA1422
        Console.WriteLine("[FKFinder] Calling RequestSceneSessionActivation");
        UIKit.UIApplication.SharedApplication.RequestSceneSessionActivation(
            null, null, null, (error) =>
            {
                Console.WriteLine($"[FKFinder] RequestSceneSessionActivation error: {error}");
            });
        Console.WriteLine("[FKFinder] RequestSceneSessionActivation returned");
#pragma warning restore CA1422
#else
        var newWindow = new Window(new MainPage()) { Title = "FKFinder" };
        Application.Current?.OpenWindow(newWindow);
#endif
    }

#if MACCATALYST
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        var window = sender as Window;
        if (window?.Handler?.PlatformView is UIKit.UIWindow platformWindow)
        {
            // Make UIKit layers transparent so the AppKit NSVisualEffectView shows through
            Platforms.MacCatalyst.Handlers.VibrancyHelper.MakeUIKitLayerTransparent(platformWindow);
        }

        // Register Dock menu once services are available
        if (!_dockMenuRegistered)
        {
            TryRegisterDockMenu(window);
        }

        // Only unsubscribe once Dock menu is registered (or for non-first windows)
        if (_dockMenuRegistered && window != null)
            window.Activated -= OnWindowActivated;
    }

    private void TryRegisterDockMenu(Window? window)
    {
        if (window?.Page?.Handler?.MauiContext?.Services is not IServiceProvider sp)
        {
            // Services not ready yet — retry after a short delay
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (!_dockMenuRegistered)
                        TryRegisterDockMenu(window);
                });
            });
            return;
        }

        var frequentFolderService = sp.GetService<Services.IFrequentFolderService>();
        var navigationBridge = sp.GetService<Services.NavigationBridge>();
        if (frequentFolderService != null && navigationBridge != null)
        {
            Platforms.MacCatalyst.Handlers.DockMenuHelper.Register(
                frequentFolderService,
                navigationBridge,
                () => MainThread.BeginInvokeOnMainThread(OpenNewWindow));
            _dockMenuRegistered = true;
            System.Diagnostics.Debug.WriteLine("App: Dock menu registered successfully");
        }
    }
#endif
}
