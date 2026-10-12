using System.Reflection;
using System.Runtime.InteropServices;
using MacExplorer.Platforms.MacOS;
using Xunit;

namespace MacExplorer.Tests;

public sealed class MacWindowChromeTests
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private static readonly MethodInfo GetRect = typeof(MacWindowChrome).GetMethod(
        "GetRect", BindingFlags.Static | BindingFlags.NonPublic)!;

    [Theory]
    [InlineData(-13.5, 27.25, 640.5, 480.75)]
    [InlineData(0, 0, 1, 2)]
    public void NativeRectangleReturnPreservesAllFourDoubles(double x, double y, double width, double height)
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Requires the macOS Objective-C runtime.");
        // Run this test under both osx-arm64 and osx-x64: headless Avalonia
        // windows have no NSView and cannot exercise the startup ABI in #20.
        NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");
        var pool = Send(Send(objc_getClass("NSAutoreleasePool"), sel_registerName("alloc")), sel_registerName("init"));
        try
        {
            var value = ValueWithRect(objc_getClass("NSValue"), sel_registerName("valueWithRect:"),
                new NativeRect { X = x, Y = y, Width = width, Height = height });
            Assert.NotEqual(IntPtr.Zero, value);
            var result = GetRect.Invoke(null, [value, "rectValue"])!;
            Assert.Equal((x, y, width, height), ReadRect(result));
        }
        finally
        {
            SendVoid(pool, sel_registerName("drain"));
        }
    }

    [Fact]
    public void NilRectangleReceiverReturnsZeroWithoutNativeDispatch()
    {
        var result = GetRect.Invoke(null, [IntPtr.Zero, "bounds"])!;
        Assert.Equal((0d, 0d, 0d, 0d), ReadRect(result));
    }

    private static (double, double, double, double) ReadRect(object rect)
    {
        var type = rect.GetType();
        return ((double)type.GetField("X")!.GetValue(rect)!, (double)type.GetField("Y")!.GetValue(rect)!,
            (double)type.GetField("Width")!.GetValue(rect)!, (double)type.GetField("Height")!.GetValue(rect)!);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public double X, Y, Width, Height;
    }

    [DllImport(LibObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(LibObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoid(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr ValueWithRect(IntPtr receiver, IntPtr selector, NativeRect rect);
}
