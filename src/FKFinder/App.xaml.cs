namespace FKFinder;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new MainPage()) { Title = "FKFinder" };

#if MACCATALYST
        window.Created += OnWindowCreated;
#endif

        return window;
	}

#if MACCATALYST
    private void OnWindowCreated(object? sender, EventArgs e)
    {
        var window = sender as Window;
        if (window?.Handler?.PlatformView is UIKit.UIWindow platformWindow)
        {
            Platforms.MacCatalyst.Handlers.VibrancyHelper.ApplyWindowStyle(platformWindow);
        }
    }
#endif
}
