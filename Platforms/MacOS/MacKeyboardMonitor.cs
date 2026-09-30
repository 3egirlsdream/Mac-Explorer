using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;

namespace MacExplorer.Platforms.MacOS;

internal enum KeyboardActivity { Modifiers, KeyDown, PointerDown, Deactivated, KeyUp }

internal sealed class MacKeyboardMonitor : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int KeyboardCallback(int kind, ushort keyCode, int modifiers, int repeat);
    private readonly KeyboardCallback _callback;
    private IntPtr _handle;

    public MacKeyboardMonitor(Window window, Func<KeyboardActivity, Key, KeyModifiers, bool, bool> callback)
    {
        _callback = (kind, keyCode, modifiers, repeat) =>
        {
            try
            {
                var flags = ((modifiers & 1) != 0 ? KeyModifiers.Meta : 0)
                    | ((modifiers & 2) != 0 ? KeyModifiers.Shift : 0)
                    | ((modifiers & 4) != 0 ? KeyModifiers.Alt : 0)
                    | ((modifiers & 8) != 0 ? KeyModifiers.Control : 0);
                return callback((KeyboardActivity)kind, FromKeyCode(keyCode), flags, repeat != 0) ? 1 : 0;
            }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError($"Shortcut input: {ex}"); return 0; }
        };
        if (window.TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle handle)
            _handle = MacExplorerKeyboardCreate(handle.NSView, _callback);
    }

    internal static Key FromKeyCode(ushort code) => code switch
    {
        0 => Key.A, 1 => Key.S, 2 => Key.D, 3 => Key.F, 4 => Key.H, 5 => Key.G,
        6 => Key.Z, 7 => Key.X, 8 => Key.C, 9 => Key.V, 11 => Key.B, 12 => Key.Q,
        13 => Key.W, 14 => Key.E, 15 => Key.R, 16 => Key.Y, 17 => Key.T,
        18 => Key.D1, 19 => Key.D2, 20 => Key.D3, 21 => Key.D4, 22 => Key.D6, 23 => Key.D5,
        24 => Key.OemPlus, 25 => Key.D9, 26 => Key.D7, 27 => Key.OemMinus, 28 => Key.D8,
        29 => Key.D0, 30 => Key.OemCloseBrackets, 31 => Key.O, 32 => Key.U, 33 => Key.OemOpenBrackets,
        34 => Key.I, 35 => Key.P, 36 or 76 => Key.Enter, 37 => Key.L, 38 => Key.J,
        39 => Key.OemQuotes, 40 => Key.K, 41 => Key.OemSemicolon, 42 => Key.OemBackslash,
        43 => Key.OemComma, 44 => Key.OemQuestion, 45 => Key.N, 46 => Key.M, 47 => Key.OemPeriod,
        48 => Key.Tab, 49 => Key.Space, 50 => Key.OemTilde, 51 => Key.Back, 53 => Key.Escape,
        54 => Key.RWin, 55 => Key.LWin, 56 => Key.LeftShift, 58 => Key.LeftAlt, 59 => Key.LeftCtrl,
        60 => Key.RightShift, 61 => Key.RightAlt, 62 => Key.RightCtrl,
        65 => Key.Decimal, 67 => Key.Multiply, 69 => Key.Add, 75 => Key.Divide, 78 => Key.Subtract,
        82 => Key.NumPad0, 83 => Key.NumPad1, 84 => Key.NumPad2, 85 => Key.NumPad3, 86 => Key.NumPad4,
        87 => Key.NumPad5, 88 => Key.NumPad6, 89 => Key.NumPad7, 91 => Key.NumPad8, 92 => Key.NumPad9,
        96 => Key.F5, 97 => Key.F6, 98 => Key.F7, 99 => Key.F3, 100 => Key.F8, 101 => Key.F9,
        103 => Key.F11, 105 => Key.F13, 106 => Key.F16, 107 => Key.F14, 109 => Key.F10,
        111 => Key.F12, 113 => Key.F15, 114 => Key.Insert, 115 => Key.Home, 116 => Key.PageUp,
        117 => Key.Delete, 118 => Key.F4, 119 => Key.End, 120 => Key.F2, 121 => Key.PageDown,
        122 => Key.F1, 123 => Key.Left, 124 => Key.Right, 125 => Key.Down, 126 => Key.Up,
        _ => Key.None
    };

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) { MacExplorerKeyboardDestroy(_handle); _handle = IntPtr.Zero; }
        GC.KeepAlive(_callback);
    }
    [DllImport("MacExplorerNativeDrag")] private static extern IntPtr MacExplorerKeyboardCreate(IntPtr view, KeyboardCallback callback);
    [DllImport("MacExplorerNativeDrag")] private static extern void MacExplorerKeyboardDestroy(IntPtr handle);
}
