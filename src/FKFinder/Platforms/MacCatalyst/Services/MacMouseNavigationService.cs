using Foundation;
using GameController;
using FKFinder.Services;

namespace FKFinder.Platforms.MacCatalyst.Services;

public class MacMouseNavigationService : IMouseNavigationService
{
    public event Action? BackButtonPressed;
    public event Action? ForwardButtonPressed;

    private bool _started;
    private NSObject? _connectObserver;

    public void Start()
    {
        if (_started) return;
        _started = true;

        // Setup mouse already connected
        SetupMouse(GCMouse.Current);

        // Watch for future mouse connections
        _connectObserver = NSNotificationCenter.DefaultCenter.AddObserver(
            GCMouse.DidConnectNotification,
            notification =>
            {
                if (notification.Object is GCMouse mouse)
                    SetupMouse(mouse);
            });
    }

    public void Stop()
    {
        if (_connectObserver != null)
        {
            NSNotificationCenter.DefaultCenter.RemoveObserver(_connectObserver);
            _connectObserver.Dispose();
            _connectObserver = null;
        }
        _started = false;
    }

    private void SetupMouse(GCMouse? mouse)
    {
        var input = mouse?.MouseInput;
        if (input == null) return;

        var aux = input.AuxiliaryButtons;
        if (aux == null) return;

        // auxiliaryButtons[0] = mouse button 4 (back/XButton1)
        if (aux.Length > 0)
        {
            aux[0].PressedChangedHandler = (_, _, pressed) =>
            {
                if (pressed)
                    MainThread.BeginInvokeOnMainThread(() => BackButtonPressed?.Invoke());
            };
        }

        // auxiliaryButtons[1] = mouse button 5 (forward/XButton2)
        if (aux.Length > 1)
        {
            aux[1].PressedChangedHandler = (_, _, pressed) =>
            {
                if (pressed)
                    MainThread.BeginInvokeOnMainThread(() => ForwardButtonPressed?.Invoke());
            };
        }
    }
}
