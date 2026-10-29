using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace MacExplorer.Platforms.MacOS;

internal sealed class MacFileDeliveryStatusItem : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StatusCallback(int action);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DropCallback(IntPtr paths);
    private readonly StatusCallback _callback;
    private readonly DropCallback _dropCallback;
    public event Action<bool>? FileDragChanged;
    public event Action<bool>? DropHoverChanged;
    public event Func<string[], bool>? FilesDropped;
    public event Action? Clicked;
    public event Action? OutsideClicked;

    public MacFileDeliveryStatusItem()
    {
        _callback = action =>
        {
            try
            {
                switch (action)
                {
                    case 0: Clicked?.Invoke(); break;
                    case 1: OutsideClicked?.Invoke(); break;
                    case 2: FileDragChanged?.Invoke(true); break;
                    case 3: FileDragChanged?.Invoke(false); break;
                    case 4: DropHoverChanged?.Invoke(true); break;
                    case 5: DropHoverChanged?.Invoke(false); break;
                }
            }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError($"File delivery: {ex}"); }
        };
        _dropCallback = pointer =>
        {
            try
            {
                var paths = System.Text.Json.JsonSerializer.Deserialize<string[]>(Marshal.PtrToStringUTF8(pointer)!) ?? [];
                return FilesDropped?.Invoke(paths) == true ? 1 : 0;
            }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError($"File delivery drop: {ex}"); return 0; }
        };
        MacExplorerDeliveryCreate(_callback, _dropCallback);
    }

    public void Prepare(Window window)
    {
        if (GetNSView(window) is var view && view != IntPtr.Zero)
            MacExplorerDeliveryPrepare(view);
    }

    public void PlaceDropWindow(Window window)
    {
        if (GetNSView(window) is var view && view != IntPtr.Zero)
        {
            MacExplorerDeliveryPlaceDrop(view);
            MacWindowChrome.MakeTransparent(window);
        }
    }

    public static void ClearStagingAccess()
    {
        if (OperatingSystem.IsMacOS()) MacExplorerDeliveryClearStagingAccess();
    }

    public void Place(Window window)
    {
        if (GetNSView(window) is var view && view != IntPtr.Zero)
        {
            MacExplorerDeliveryPlace(view);
            MacWindowChrome.MakeTransparent(window);
        }
    }

    private static IntPtr GetNSView(Window window)
    {
        var handle = window.TryGetPlatformHandle();
        return handle is IMacOSTopLevelPlatformHandle mac ? mac.NSView
            : handle?.HandleDescriptor == "NSView" ? handle.Handle : IntPtr.Zero;
    }

#if DEBUG
    internal static object GetDiagnosticState()
    {
        var state = MacExplorerDeliveryInspect(out var x, out var y, out var polls, out var starts, out var changeCount);
        return new { state, x, y, polls, starts, changeCount };
    }
    [DllImport("MacExplorerNativeDrag")] private static extern int MacExplorerDeliveryInspect(out double x, out double y, out int polls, out int starts, out int changeCount);
#endif

    public bool ContainsPoint(Avalonia.Point point) => MacExplorerDeliveryContainsPoint(point.X, point.Y) != 0;
    public void Dispose() { MacExplorerDeliveryDestroy(); GC.KeepAlive(_callback); GC.KeepAlive(_dropCallback); }

    [DllImport("MacExplorerNativeDrag")] private static extern void MacExplorerDeliveryCreate(StatusCallback callback, DropCallback dropCallback);
    [DllImport("MacExplorerNativeDrag")] private static extern void MacExplorerDeliveryDestroy();
    [DllImport("MacExplorerNativeDrag")] private static extern void MacExplorerDeliveryPlaceDrop(IntPtr view);
    [DllImport("MacExplorerNativeDrag")] private static extern void MacExplorerDeliveryClearStagingAccess();
    [DllImport("MacExplorerNativeDrag")] private static extern void MacExplorerDeliveryPrepare(IntPtr view);
    [DllImport("MacExplorerNativeDrag")] private static extern void MacExplorerDeliveryPlace(IntPtr view);
    [DllImport("MacExplorerNativeDrag")] private static extern int MacExplorerDeliveryContainsPoint(double x, double y);
}
