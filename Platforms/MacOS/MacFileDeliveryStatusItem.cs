using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace MacExplorer.Platforms.MacOS;

internal sealed class MacFileDeliveryStatusItem : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StatusCallback(int action);
    private readonly StatusCallback _callback;
    public event Action? Clicked;
    public event Action? OutsideClicked;

    public MacFileDeliveryStatusItem()
    {
        _callback = action =>
        {
            try { if (action == 0) Clicked?.Invoke(); else OutsideClicked?.Invoke(); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError($"File delivery: {ex}"); }
        };
        MacExplorerDeliveryCreate(_callback);
    }

    public void Place(Window window)
    {
        if (window.TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle handle)
            MacExplorerDeliveryPlace(handle.NSView);
    }

    public bool ContainsPoint(Avalonia.Point point) => MacExplorerDeliveryContainsPoint(point.X, point.Y) != 0;
    public void Dispose() { MacExplorerDeliveryDestroy(); GC.KeepAlive(_callback); }

    [DllImport("MacExplorerNativeDrag")] private static extern void MacExplorerDeliveryCreate(StatusCallback callback);
    [DllImport("MacExplorerNativeDrag")] private static extern void MacExplorerDeliveryDestroy();
    [DllImport("MacExplorerNativeDrag")] private static extern void MacExplorerDeliveryPlace(IntPtr view);
    [DllImport("MacExplorerNativeDrag")] private static extern int MacExplorerDeliveryContainsPoint(double x, double y);
}
