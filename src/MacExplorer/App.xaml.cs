using MacExplorer.Services;

namespace MacExplorer;

public partial class App : Application
{
    private bool _dockMenuRegistered;
    private bool _dropOverlayRegistered;
    private readonly HashSet<Window> _initializedWindows = new();

	public App(ISettingsService settingsService)
	{
		InitializeComponent();

#if MACCATALYST
        Platforms.MacCatalyst.Handlers.VibrancyHelper.Enabled = settingsService.Get("vibrancy_enabled", true);
        Platforms.MacCatalyst.Handlers.VibrancyHelper.Alpha = settingsService.Get("vibrancy_alpha", 0.85);
        // Register for the NSWindow creation notification as early as possible
        Platforms.MacCatalyst.Handlers.VibrancyHelper.Register();
        Platforms.MacCatalyst.Handlers.DropOverlayHelper.Register(null!);
#endif
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new MainPage()) { Title = "Mac Explorer" };

#if MACCATALYST
        // Set consistent default window size for all windows (including new ones from Dock menu)
        window.Width = 1372;
        window.Height = 849;
        window.MinimumWidth = 800;
        window.MinimumHeight = 500;
        window.Activated += OnWindowActivated;
#endif

        return window;
	}

    /// <summary>
    /// Opens a new MacExplorer window (multi-instance support).
    /// </summary>
    public void OpenNewWindow()
    {
        Console.WriteLine("[MacExplorer] OpenNewWindow called");
#if MACCATALYST
        // MAUI's OpenWindow() is broken on Mac Catalyst (silently does nothing).
        // Use native UIKit scene API directly.
#pragma warning disable CA1422
        Console.WriteLine("[MacExplorer] Calling RequestSceneSessionActivation");
        UIKit.UIApplication.SharedApplication.RequestSceneSessionActivation(
            null, null, null, (error) =>
            {
                Console.WriteLine($"[MacExplorer] RequestSceneSessionActivation error: {error}");
            });
        Console.WriteLine("[MacExplorer] RequestSceneSessionActivation returned");
#pragma warning restore CA1422
#else
        var newWindow = new Window(new MainPage()) { Title = "Mac Explorer" };
        Application.Current?.OpenWindow(newWindow);
#endif
    }

#if MACCATALYST
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        var window = sender as Window;
        if (window != null && _initializedWindows.Add(window))
        {
            // First activation of this window — apply one-time setup
            if (window.Handler?.PlatformView is UIKit.UIWindow platformWindow)
            {
                // Make UIKit layers transparent so the AppKit NSVisualEffectView shows through
                Platforms.MacCatalyst.Handlers.VibrancyHelper.MakeUIKitLayerTransparent(platformWindow);
            }

            // Register drop overlay bridge once services are available
            if (!_dropOverlayRegistered)
            {
                var dragDropBridge = window?.Page?.Handler?.MauiContext?.Services?.GetService<Services.IDragDropBridge>();
                if (dragDropBridge != null)
                {
                    Platforms.MacCatalyst.Handlers.DropOverlayHelper.SetBridge(dragDropBridge);
                    _dropOverlayRegistered = true;
                }
            }
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
